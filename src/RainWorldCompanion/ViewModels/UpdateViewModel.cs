using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// Whether the app is free to close itself right now. Implemented by <see cref="MainViewModel"/>,
/// which knows what it is in the middle of.
/// </summary>
public interface IBusyGuard
{
    /// <summary>A sentence explaining why not, or null when there is no reason not to.</summary>
    string? WhyNotNow();
}

/// <summary>
/// No message box and no timer, because App.Tests never constructs a Window, an Application or a
/// Dispatcher. Failures become <see cref="StatusMessage"/> instead. Nothing here writes
/// settings.json either: the store serialises the whole object, so a second writer would clobber
/// the first, and changes go out through a callback MainViewModel owns.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IReleaseSource _source;
    private readonly IInstallerDownloader _downloader;
    private readonly IInstallerLauncher _launcher;
    private readonly IBusyGuard _busy;
    private readonly Action<Action<AppSettings>> _persist;
    private readonly Action _requestShutdown;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Never written to disk on purpose: persisting a skipped version is how somebody ends up
    /// stranded on a broken build. A restart brings the offer back.
    /// </summary>
    private string? _dismissed;

    /// <summary>Mirrors settings, so the check can answer without a file read.</summary>
    private string _lastSeenChangelog = "";

    public UpdateViewModel(
        BuildStamp build,
        IReleaseSource source,
        IInstallerDownloader downloader,
        IInstallerLauncher launcher,
        IBusyGuard busy,
        Action<Action<AppSettings>> persist,
        Action requestShutdown,
        Func<DateTimeOffset>? now = null)
    {
        Build = build;
        _source = source;
        _downloader = downloader;
        _launcher = launcher;
        _busy = busy;
        _persist = persist;
        _requestShutdown = requestShutdown;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public BuildStamp Build { get; }

    /// <summary>
    /// The commit only shows for a branch build: a beta or a stable release is already named
    /// exactly by its tag, so the commit would only repeat what the version already says.
    /// </summary>
    public string VersionText => Build.IsBranchBuild && Build.ShortSha.Length > 0
        ? $"{Build.Version}, commit {Build.ShortSha}"
        : Build.Version;

    public string RunningVersionText => $"Running {VersionText}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOffer))]
    [NotifyPropertyChangedFor(nameof(BannerText))]
    [NotifyPropertyChangedFor(nameof(HasStandaloneProblem))]
    private UpdateOffer? offer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStandaloneProblem))]
    private bool isProblem;

    [ObservableProperty]
    private bool isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isDownloading;

    [ObservableProperty]
    private double downloadPercent;

    [ObservableProperty]
    private UpdateChannel channel = UpdateChannel.Stable;

    [ObservableProperty]
    private bool autoCheck = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    private DateTimeOffset? lastCheckedUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWhatsNew))]
    private string whatsNewNotes = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhatsNewTitle))]
    private string whatsNewVersion = "";

    public bool HasWhatsNew => WhatsNewNotes.Length != 0;

    public string WhatsNewTitle => $"What's new in {WhatsNewVersion}";

    public bool HasOffer => Offer is not null;

    public bool HasStatus => StatusMessage.Length != 0;

    /// <summary>A failure with an offer behind it goes inside that banner instead.</summary>
    public bool HasStandaloneProblem => IsProblem && Offer is null;

    public bool IsIdle => !IsDownloading;

    public string LastCheckedText => UpdateCooldown.Describe(LastCheckedUtc, _now());

    public string BannerText => Offer is null
        ? ""
        : Offer.IsPrerelease
            ? $"Version {Offer.VersionText} is available as a pre-release."
            : $"Version {Offer.VersionText} is available.";

    /// <summary>Called once the real settings have been loaded.</summary>
    public void Adopt(AppSettings settings)
    {
        Channel = UpdateChannels.Parse(settings.UpdateChannel);
        AutoCheck = settings.AutoCheckUpdates;
        LastCheckedUtc = settings.LastUpdateCheckUtc;
        _lastSeenChangelog = settings.LastSeenChangelogVersion ?? "";
    }

    /// <summary>
    /// AutoCheck belongs here rather than in <see cref="CheckAsync"/>, which the manual button also
    /// calls. Folded into the check, it made that button report "this is the newest build".
    /// </summary>
    public bool IsAutomaticCheckDue() => AutoCheck && UpdateCooldown.IsDue(LastCheckedUtc, _now());

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userAsked: true, CancellationToken.None);

    /// <param name="userAsked">
    /// True when somebody pressed a button, which shows an offer they had already dismissed and
    /// reports "you are up to date" rather than saying nothing.
    /// </param>
    public async Task CheckAsync(bool userAsked, CancellationToken cancellationToken)
    {
        if (IsChecking || IsDownloading)
        {
            return;
        }

        // Branch builds are never offered, and running the check would report "this is the newest
        // version" on a channel where the picker is empty by design.
        if (!Channel.CanBeOfferedAutomatically())
        {
            Offer = null;
            Say("", problem: false);
            return;
        }

        IsChecking = true;

        // Stamped before the request, not after. A machine that is offline at every launch would
        // otherwise never record a check and would ask again every single time.
        var previous = LastCheckedUtc;
        Stamp(_now());

        try
        {
            var releases = await _source.GetReleasesAsync(cancellationToken);
            var found = UpdatePicker.Pick(releases, Build.ParsedVersion, Channel);

            if (found is not null && (userAsked || found.VersionText != _dismissed))
            {
                Offer = found;
                Say("", problem: false);
            }
            else if (userAsked)
            {
                Offer = found is null ? null : Offer;
                Say(found is null ? "This is the newest version." : "", problem: false);
            }
        }
        catch (OperationCanceledException)
        {
            Stamp(previous);
        }
        catch (Exception e)
        {
            // A check that never reached GitHub has not used its turn.
            Stamp(previous);
            if (userAsked)
            {
                Say(Describe(e), problem: true);
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// What the build already running brought with it, shown once. The version comparison is local,
    /// so only a version that actually changed since the last launch asks GitHub anything.
    /// </summary>
    public async Task CheckForWhatsNewAsync(CancellationToken cancellationToken)
    {
        var running = Build.Version;
        if (running.Length == 0 || _lastSeenChangelog == running)
        {
            return;
        }

        // Nothing recorded means a first run, and recording it silently keeps a fresh install from
        // opening on a changelog.
        if (_lastSeenChangelog.Length == 0 || Build.ParsedVersion is not { } version)
        {
            RememberChangelogSeen(running);
            return;
        }

        try
        {
            var releases = await _source.GetReleasesAsync(cancellationToken);

            // The widest list, then an exact match, because the running build may be a
            // pre-release and the stable list would not hold it.
            var match = UpdatePicker
                .ForChannel(releases, UpdateChannel.Prerelease)
                .FirstOrDefault(offer => offer.Version == version);

            if (match is null || !match.HasNotes)
            {
                // A branch build has no release of its own, and a release can carry no body.
                RememberChangelogSeen(running);
                return;
            }

            WhatsNewVersion = match.VersionText;
            WhatsNewNotes = match.Notes;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The setting is left alone, so the next launch tries again.
        }
    }

    [RelayCommand]
    private void DismissWhatsNew()
    {
        RememberChangelogSeen(Build.Version);
        WhatsNewNotes = "";
        WhatsNewVersion = "";
    }

    private void RememberChangelogSeen(string version)
    {
        _lastSeenChangelog = version;
        _persist(settings => settings.LastSeenChangelogVersion = version);
    }

    [RelayCommand]
    private void DismissOffer()
    {
        _dismissed = Offer?.VersionText;
        Offer = null;
        Say("", problem: false);
    }

    [RelayCommand]
    private Task InstallOfferAsync() => InstallAsync(Offer, CancellationToken.None);

    /// <summary>
    /// Downloads an offer and hands it to the installer.
    ///
    /// The guard is asked twice, and the second time is the one that matters. A download takes long
    /// enough for a backup to have been started since the click, and it is the exit at the end, not
    /// the click at the start, that would interrupt it.
    /// </summary>
    public async Task InstallAsync(UpdateOffer? target, CancellationToken cancellationToken)
    {
        if (target is null || IsDownloading)
        {
            return;
        }

        if (_busy.WhyNotNow() is { } reason)
        {
            Say(reason, problem: true);
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;
        Say($"Downloading version {target.VersionText}...", problem: false);

        try
        {
            var progress = new Progress<double>(fraction => DownloadPercent = fraction * 100);
            var installer = await _downloader.DownloadAsync(
                target.DownloadUrl, target.AssetName, target.SizeBytes, progress, cancellationToken);

            // Asked again, because the download took long enough for the answer to change.
            if (_busy.WhyNotNow() is { } startedSince)
            {
                Say(startedSince, problem: true);
                return;
            }

            Say("Starting the installer...", problem: false);
            var outcome = _launcher.Start(installer);
            Say(outcome.Message, problem: !outcome.ShouldExit);

            if (outcome.ShouldExit)
            {
                _requestShutdown();
            }
        }
        catch (OperationCanceledException)
        {
            Say("The update was cancelled.", problem: false);
        }
        catch (Exception e)
        {
            Say(Describe(e), problem: true);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>
    /// In this class rather than the window: a second copy of the guard, the progress and the
    /// handoff is a second chance to forget to ask whether a save is being written.
    /// </summary>
    public async Task InstallBranchBuildAsync(AlphaBuild? build, CancellationToken cancellationToken)
    {
        if (build is null || IsDownloading)
        {
            return;
        }

        if (_busy.WhyNotNow() is { } reason)
        {
            Say(reason, problem: true);
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;
        Say($"Downloading {build.Label}...", problem: false);

        try
        {
            var progress = new Progress<double>(fraction => DownloadPercent = fraction * 100);
            var installer = await _downloader.DownloadBranchBuildAsync(
                build.DownloadUrl, build.RunId, progress, cancellationToken);

            if (_busy.WhyNotNow() is { } startedSince)
            {
                Say(startedSince, problem: true);
                return;
            }

            Say("Starting the installer...", problem: false);
            var outcome = _launcher.Start(installer);
            Say(outcome.Message, problem: !outcome.ShouldExit);

            if (outcome.ShouldExit)
            {
                _requestShutdown();
            }
        }
        catch (OperationCanceledException)
        {
            Say("The download was cancelled.", problem: false);
        }
        catch (Exception e)
        {
            Say(Describe(e), problem: true);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>
    /// The widest list rather than the current channel's, so one fetch backs both. Narrowing it is
    /// the window's job: a list fetched as stable has the pre-releases already dropped.
    /// </summary>
    public async Task<IReadOnlyList<UpdateOffer>> ListReleasesAsync(CancellationToken cancellationToken)
    {
        var releases = await _source.GetReleasesAsync(cancellationToken);
        return UpdatePicker.ForChannel(releases, UpdateChannel.Prerelease);
    }

    public async Task<IReadOnlyList<AlphaBuild>> ListBranchBuildsAsync(CancellationToken cancellationToken)
    {
        var runs = await _source.GetBranchBuildRunsAsync(cancellationToken);
        return AlphaBuilds.FromRuns(runs, Build);
    }

    public async Task SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        if (Channel == channel)
        {
            return;
        }

        Channel = channel;
        _persist(settings => settings.UpdateChannel = channel.ToStorageString());

        // The offer on screen came from the old channel and may not exist on this one.
        Offer = null;

        // Forced, because the hourly cooldown is about checks nobody asked for.
        Stamp(null);
        await CheckAsync(userAsked: true, cancellationToken);
    }

    public void SetAutoCheck(bool enabled)
    {
        if (AutoCheck == enabled)
        {
            return;
        }

        AutoCheck = enabled;
        _persist(settings => settings.AutoCheckUpdates = enabled);
    }

    /// <summary>
    /// Records that the list was fetched from GitHub. The Updates window reaches the same server
    /// the banner's own check does, so a refresh there counts as a check: without this the window
    /// would go on saying it last looked hours ago while it was looking just now.
    /// </summary>
    public void RecordCheck(DateTimeOffset at) => Stamp(at);

    private void Stamp(DateTimeOffset? at)
    {
        LastCheckedUtc = at;
        _persist(settings => settings.LastUpdateCheckUtc = at);
    }

    private void Say(string message, bool problem)
    {
        StatusMessage = message;
        IsProblem = problem;
    }

    /// <summary>UpdateCheckException already carries a sentence written to be shown as it is.</summary>
    private static string Describe(Exception e) => e is UpdateCheckException
        ? e.Message
        : "The update could not be completed. " + e.Message;
}
