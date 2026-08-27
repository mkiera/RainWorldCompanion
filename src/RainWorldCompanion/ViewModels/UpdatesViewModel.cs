using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

public sealed partial class ChannelViewModel(UpdateChannel channel) : ObservableObject
{
    public UpdateChannel Channel { get; } = channel;

    public string Title { get; } = channel.Title();

    public string Description { get; } = channel.Description();

    [ObservableProperty]
    private bool isSelected;
}

public sealed partial class ReleaseRowViewModel : ObservableObject
{
    public ReleaseRowViewModel(UpdateOffer offer, ReleaseAction action)
    {
        Offer = offer;
        Action = action;
    }

    public UpdateOffer Offer { get; }

    public ReleaseAction Action { get; }

    public string VersionText => Offer.VersionText;

    public string ActionVerb => Action.Verb();

    public bool IsRunning => Action == ReleaseAction.Reinstall;

    public bool IsPrerelease => Offer.IsPrerelease;

    public string ReleaseUrl => Offer.ReleaseUrl;

    public string Notes => Offer.Notes;

    public bool HasNotes => Offer.HasNotes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesVerb))]
    private bool isExpanded;

    public string NotesVerb => IsExpanded ? "Hide" : "Notes";

    /// <summary>"Pre-release, 12 March 2026, 43.1 MB", or as much of it as is known.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (Offer.IsPrerelease)
            {
                parts.Add("Pre-release");
            }

            if (Offer.PublishedUtc is { } published)
            {
                parts.Add(published.ToLocalTime().ToString("d MMMM yyyy"));
            }

            if (Offer.SizeBytes > 0)
            {
                parts.Add($"{Offer.SizeBytes / 1024d / 1024d:0.#} MB");
            }

            return string.Join(", ", parts);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmationText))]
    private bool isArmed;

    public string ConfirmationText =>
        IsArmed ? ReleaseActions.ConfirmationText(VersionText) : "";
}

public sealed class BranchRowViewModel(AlphaBuild build)
{
    public AlphaBuild Build { get; } = build;

    public string Label { get; } = build.Label;

    public bool IsRunning { get; } = build.IsRunningCopy;

    public string RunUrl { get; } = build.RunUrl;

    public string Subtitle { get; } = build.CreatedUtc is { } created
        ? $"commit {build.ShortSha}, {created.ToLocalTime():d MMMM yyyy}"
        : $"commit {build.ShortSha}";
}

/// <summary>
/// Constructs no Window and no Dispatcher, so App.Tests can drive it on whatever thread xunit
/// hands it.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    /// <summary>
    /// Anonymous GitHub requests are capped at sixty an hour and shared with everything else on
    /// the machine, so flicking between the three channels must not spend one per click.
    /// </summary>
    public static readonly TimeSpan ListCacheLife = TimeSpan.FromMinutes(5);

    private readonly UpdateViewModel _updates;
    private readonly Func<DateTimeOffset> _now;

    private IReadOnlyList<UpdateOffer>? _cachedReleases;
    private DateTimeOffset _releasesFetchedAt;
    private IReadOnlyList<AlphaBuild>? _cachedBranches;
    private DateTimeOffset _branchesFetchedAt;

    public UpdatesViewModel(UpdateViewModel updates, Func<DateTimeOffset>? now = null)
    {
        _updates = updates;
        _now = now ?? (() => DateTimeOffset.Now);

        foreach (var channel in UpdateChannels.All)
        {
            Channels.Add(new ChannelViewModel(channel) { IsSelected = channel == updates.Channel });
        }

        AutoCheck = updates.AutoCheck;
    }

    /// <summary>Raised when the window should close. The view subscribes, the model never calls it.</summary>
    public event Action? CloseRequested;

    public UpdateViewModel Updates => _updates;

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<ReleaseRowViewModel> Releases { get; } = new();

    public ObservableCollection<BranchRowViewModel> BranchBuilds { get; } = new();

    public string RunningVersionText => _updates.RunningVersionText;

    public UpdateChannel Channel => _updates.Channel;

    public bool IsAlpha => Channel == UpdateChannel.Alpha;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool isLoading;

    public bool CanRefresh => !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string message = "";

    [ObservableProperty]
    private bool isProblem;

    public bool HasMessage => Message.Length != 0;

    /// <summary>"Checked 4 minutes ago", shared with the banner so both agree.</summary>
    public string LastCheckedText => _updates.LastCheckedText;

    [ObservableProperty]
    private bool autoCheck;

    partial void OnAutoCheckChanged(bool value) => _updates.SetAutoCheck(value);

    /// <summary>
    /// True when a channel has been asked for and answered with nothing, which is different from
    /// not having asked yet.
    /// </summary>
    [ObservableProperty]
    private bool isEmpty;

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        LoadAsync(force: false, cancellationToken);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(force: true, CancellationToken.None);

    [RelayCommand]
    private async Task SelectChannelAsync(ChannelViewModel? row)
    {
        if (row is null || row.Channel == Channel)
        {
            return;
        }

        await _updates.SetChannelAsync(row.Channel, CancellationToken.None);

        foreach (var channel in Channels)
        {
            channel.IsSelected = channel.Channel == Channel;
        }

        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(IsAlpha));

        await LoadAsync(force: false, CancellationToken.None);
    }

    [RelayCommand]
    private async Task InstallReleaseAsync(ReleaseRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.Action.NeedsConfirmation() && !row.IsArmed)
        {
            Disarm();
            row.IsArmed = true;
            return;
        }

        Disarm();
        await _updates.InstallAsync(row.Offer, CancellationToken.None);
        Adopt();
    }

    [RelayCommand]
    private async Task InstallBranchAsync(BranchRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Disarm();
        await _updates.InstallBranchBuildAsync(row.Build, CancellationToken.None);
        Adopt();
    }

    /// <summary>
    /// Disarms for the same reason every other press does: an armed downgrade is waiting for a
    /// second press on its own button, and anything else the user does in between is a sign they
    /// went somewhere other than through with it.
    /// </summary>
    [RelayCommand]
    private void ToggleNotes(ReleaseRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var wasExpanded = row.IsExpanded;
        Disarm();
        row.IsExpanded = !wasExpanded;
    }

    [RelayCommand]
    private void OpenReleasePage(ReleaseRowViewModel? row) => OpenPage(row?.ReleaseUrl);

    [RelayCommand]
    private void OpenRunPage(BranchRowViewModel? row) => OpenPage(row?.RunUrl);

    /// <summary>
    /// Held to the same allowlist a download is, because both addresses arrive in a JSON document
    /// off the network.
    /// </summary>
    private void OpenPage(string? url)
    {
        if (!UpdateUrls.IsAllowedDownload(url))
        {
            Say("That page is not somewhere this app will open.", problem: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say("The page could not be opened. " + e.Message, problem: true);
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private async Task LoadAsync(bool force, CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        Say("", problem: false);

        try
        {
            if (IsAlpha)
            {
                await LoadBranchesAsync(force, cancellationToken);
            }
            else
            {
                await LoadReleasesAsync(force, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Say(e is UpdateCheckException ? e.Message : "The list could not be read. " + e.Message,
                problem: true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(LastCheckedText));
        }
    }

    private async Task LoadReleasesAsync(bool force, CancellationToken cancellationToken)
    {
        if (force || IsStale(_cachedReleases, _releasesFetchedAt))
        {
            _cachedReleases = await _updates.ListReleasesAsync(cancellationToken);
            _releasesFetchedAt = _now();

            // After the await, so a fetch that never reached GitHub has not used its turn.
            _updates.RecordCheck(_releasesFetchedAt);
        }

        var running = _updates.Build.ParsedVersion;
        var rows = _cachedReleases!
            .Where(offer => Channel == UpdateChannel.Prerelease || !offer.IsPrerelease)
            .Select(offer => new ReleaseRowViewModel(offer, ReleaseActions.For(offer.Version, running)));

        Replace(Releases, rows);
        IsEmpty = Releases.Count == 0;

        if (IsEmpty)
        {
            Say("No releases have been published on this channel yet.", problem: false);
        }
    }

    private async Task LoadBranchesAsync(bool force, CancellationToken cancellationToken)
    {
        if (force || IsStale(_cachedBranches, _branchesFetchedAt))
        {
            _cachedBranches = await _updates.ListBranchBuildsAsync(cancellationToken);
            _branchesFetchedAt = _now();
            _updates.RecordCheck(_branchesFetchedAt);
        }

        Replace(BranchBuilds, _cachedBranches!.Select(build => new BranchRowViewModel(build)));
        IsEmpty = BranchBuilds.Count == 0;

        if (IsEmpty)
        {
            Say("No branch builds are available. Artifacts expire, so only recent runs appear here.",
                problem: false);
        }
    }

    private bool IsStale<T>(IReadOnlyList<T>? cached, DateTimeOffset fetchedAt)
        => cached is null || _now() - fetchedAt >= ListCacheLife;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private void Disarm()
    {
        foreach (var row in Releases)
        {
            row.IsArmed = false;
        }
    }

    /// <summary>Takes back whatever the shared updater changed while it was installing.</summary>
    private void Adopt()
    {
        AutoCheck = _updates.AutoCheck;
        OnPropertyChanged(nameof(LastCheckedText));
    }

    private void Say(string text, bool problem)
    {
        Message = text;
        IsProblem = problem;
    }
}
