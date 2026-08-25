using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// Whether the app is free to close itself right now.
///
/// Implemented by <see cref="MainViewModel"/>, which knows what it is in the middle of. The
/// updater cannot ask that question itself, and the answer decides whether an update is allowed to
/// end the process.
/// </summary>
public interface IBusyGuard
{
    /// <summary>A sentence explaining why not, or null when there is no reason not to.</summary>
    string? WhyNotNow();
}

/// <summary>
/// The update offer, the download, and the handoff to the installer.
///
/// Two rules shape this class, and both are about what it must not do.
///
/// It shows no message box and owns no timer. App.Tests has never constructed a Window, an
/// Application or a Dispatcher, which is what lets it run on whatever thread xunit hands it, and a
/// modal dialog or a DispatcherTimer in here would be the first thing to break that. Failures
/// become <see cref="StatusMessage"/> instead, rendered in the banner, which is also better than a
/// modal: a refusal is a whole sentence and the user can go on reading it while they deal with
/// whatever it named.
///
/// It does not write settings.json. A second writer would clobber the first, since the store
/// serialises the whole object, so changes go out through a callback that MainViewModel owns.
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
    /// Set when the user waves an offer away, and never written to disk on purpose.
    ///
    /// FinFetcher persisted the skipped version, and that is how somebody ends up stranded on a
    /// broken build: the one release that would have fixed it is the one they told it to stop
    /// mentioning. Here a restart brings the offer back, and a newer release is a new offer.
    /// </summary>
    private string? _dismissed;

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

    /// <summary>"Running 1.1.0, commit a1b2c3d", or just the version when there is no commit.</summary>
    public string RunningVersionText => Build.ShortSha.Length == 0
        ? $"Running {Build.Version}"
        : $"Running {Build.Version}, commit {Build.ShortSha}";

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

    /// <summary>True when an offer is on screen and has not been waved away.</summary>
    public bool HasOffer => Offer is not null;

    public bool HasStatus => StatusMessage.Length != 0;

    /// <summary>
    /// A failure with no offer behind it, which needs a banner of its own.
    ///
    /// When there is an offer the message goes inside that banner, beneath the version. Showing
    /// both would print the same sentence twice, once in each.
    /// </summary>
    public bool HasStandaloneProblem => IsProblem && Offer is null;

    /// <summary>False while a download is in flight, which is what hides the Update button.</summary>
    public bool IsIdle => !IsDownloading;

    public string LastCheckedText => UpdateCooldown.Describe(LastCheckedUtc, _now());

    public string BannerText => Offer is null
        ? ""
        : Offer.IsPrerelease
            ? $"Version {Offer.VersionText} is available as a pre-release."
            : $"Version {Offer.VersionText} is available.";

    /// <summary>
    /// Reads the settings the updater owns. Called once the real settings have been loaded.
    /// </summary>
    public void Adopt(AppSettings settings)
    {
        Channel = UpdateChannels.Parse(settings.UpdateChannel);
        AutoCheck = settings.AutoCheckUpdates;
        LastCheckedUtc = settings.LastUpdateCheckUtc;
    }

    /// <summary>
    /// Whether an automatic check is due. Read by the timer that owns the clock, never in here.
    ///
    /// AutoCheck is deliberately part of this and not part of <see cref="CheckAsync"/>. The manual
    /// button calls the same method, and folding the setting into the check made that button report
    /// "this is the newest build" to anyone who had turned automatic checking off.
    /// </summary>
    public bool IsAutomaticCheckDue() => AutoCheck && UpdateCooldown.IsDue(LastCheckedUtc, _now());

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userAsked: true, CancellationToken.None);

    /// <summary>
    /// Asks GitHub what is available and puts any answer on the banner.
    /// </summary>
    /// <param name="userAsked">
    /// True when somebody pressed a button. That shows an offer they had already dismissed, since
    /// going and asking is not the same as being told, and it reports "you are up to date" rather
    /// than saying nothing.
    /// </param>
    public async Task CheckAsync(bool userAsked, CancellationToken cancellationToken)
    {
        if (IsChecking || IsDownloading)
        {
            return;
        }

        // Branch builds are never offered, so there is no offer to go looking for. Returning here
        // rather than running the check keeps it from reporting "this is the newest version" on a
        // channel where the picker is empty by design and the sentence would be a lie.
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
            // A check that never reached GitHub has not used its turn. Without putting the old
            // stamp back, one offline launch costs the whole hour before the next attempt.
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

            // Asked again. Everything above took time, and the answer can have changed in it.
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
    /// Downloads a branch build and hands it to the installer.
    ///
    /// The same shape as <see cref="InstallAsync"/>, and deliberately in this class rather than in
    /// the window: the guard, the progress and the handoff are the parts that must not be written
    /// twice, because a second copy is a second chance to forget to ask whether a save is being
    /// written.
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
    /// Every installable release, newest first, pre-releases included.
    ///
    /// Deliberately the widest list rather than the current channel's. Stable is a subset of
    /// pre-release, so one fetch backs both and moving between them spends no request. Narrowing
    /// it is the window's job, and it has to be: a list fetched as stable has the pre-releases
    /// already dropped, so reusing one after a switch would show a pre-release channel with no
    /// pre-releases on it.
    /// </summary>
    public async Task<IReadOnlyList<UpdateOffer>> ListReleasesAsync(CancellationToken cancellationToken)
    {
        var releases = await _source.GetReleasesAsync(cancellationToken);
        return UpdatePicker.ForChannel(releases, UpdateChannel.Prerelease);
    }

    /// <summary>The latest build of each branch, for the branch-builds list.</summary>
    public async Task<IReadOnlyList<AlphaBuild>> ListBranchBuildsAsync(CancellationToken cancellationToken)
    {
        var runs = await _source.GetBranchBuildRunsAsync(cancellationToken);
        return AlphaBuilds.FromRuns(runs, Build);
    }

    /// <summary>Changes the channel, saves it, and looks again on the new one.</summary>
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

        // Forced, because switching channel is the user asking, and the hourly cooldown is about
        // checks nobody asked for.
        Stamp(null);
        await CheckAsync(userAsked: true, cancellationToken);
    }

    /// <summary>Turns automatic checking on or off, and saves it.</summary>
    public void SetAutoCheck(bool enabled)
    {
        if (AutoCheck == enabled)
        {
            return;
        }

        AutoCheck = enabled;
        _persist(settings => settings.AutoCheckUpdates = enabled);
    }

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

    /// <summary>
    /// The sentence to show for a failure. UpdateCheckException already carries one written for
    /// the purpose, so it is used as it is rather than wrapped in something vaguer.
    /// </summary>
    private static string Describe(Exception e) => e is UpdateCheckException
        ? e.Message
        : "The update could not be completed. " + e.Message;
}
