using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// One channel, as a row in the picker at the top of the window.
/// </summary>
public sealed partial class ChannelViewModel(UpdateChannel channel) : ObservableObject
{
    public UpdateChannel Channel { get; } = channel;

    public string Title { get; } = channel.Title();

    public string Description { get; } = channel.Description();

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// One published release in the list.
///
/// Carries the word for its own button. The list holds versions older than the running one so a
/// build can be gone back to, which means the same button is an update on one row and a downgrade
/// two rows down, and only the row knows which.
/// </summary>
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

    /// <summary>
    /// Set by the first press on a downgrade, cleared by anything else. Going back to an older
    /// build is the one direction that is hard to notice having chosen, so it takes two presses.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmationText))]
    private bool isArmed;

    public string ConfirmationText =>
        IsArmed ? ReleaseActions.ConfirmationText(VersionText) : "";
}

/// <summary>
/// One branch build in the list. No version of its own: a branch build is named by its branch and
/// the workflow's run number, because nothing in it was ever tagged.
/// </summary>
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
/// The updates window: which channel, what is on it, and what each row would do.
///
/// Owns no network code and no install logic. Both live on <see cref="UpdateViewModel"/>, which
/// the banner also uses, so the guard that refuses to close the app mid-save is written once and
/// asked the same way whichever surface started the install. Like that class it constructs no
/// Window and no Dispatcher, so App.Tests can drive it on whatever thread xunit hands it.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    /// <summary>
    /// How long a fetched list stays good for.
    ///
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

    /// <summary>Drives the Check now button. A second read while one is in flight is wasted.</summary>
    public bool CanRefresh => !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string message = "";

    [ObservableProperty]
    private bool isProblem;

    public bool HasMessage => Message.Length != 0;

    /// <summary>"Checked 4 minutes ago", shared with the banner so both agree.</summary>
    public string LastCheckedText => _updates.LastCheckedText;

    /// <summary>
    /// Two-way with the checkbox, and it saves itself.
    ///
    /// The setting is written through the shared updater rather than here, because that class
    /// owns the callback into MainViewModel and settings.json has one writer on purpose.
    /// </summary>
    [ObservableProperty]
    private bool autoCheck;

    partial void OnAutoCheckChanged(bool value) => _updates.SetAutoCheck(value);

    /// <summary>
    /// True when a channel has been asked for and answered with nothing, which is different from
    /// not having asked yet. Without it an empty list reads as a failure.
    /// </summary>
    [ObservableProperty]
    private bool isEmpty;

    /// <summary>Loads the current channel. Called once when the window opens.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        LoadAsync(force: false, cancellationToken);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(force: true, CancellationToken.None);

    /// <summary>Switches channel, saves the choice, and lists what is on the new one.</summary>
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

    /// <summary>
    /// Installs a release. A downgrade needs the button twice, and the first press only arms it.
    /// </summary>
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

    [RelayCommand]
    private void OpenReleasePage(ReleaseRowViewModel? row) => OpenPage(row?.ReleaseUrl);

    [RelayCommand]
    private void OpenRunPage(BranchRowViewModel? row) => OpenPage(row?.RunUrl);

    /// <summary>
    /// Hands a URL to the browser.
    ///
    /// Held to the same allowlist a download is, because both addresses arrive in a JSON document
    /// off the network. A release page is only ever opened, never executed, but the check costs
    /// nothing and the alternative is one place where a rewritten API response picks the
    /// destination.
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
            // The window is closing. Nothing to report to somebody who is no longer looking.
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
        }

        // Narrowed here rather than refetched. The cached list is the wide one, so a channel
        // switch is a filter over what is already in hand.
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
