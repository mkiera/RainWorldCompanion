using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Stand-ins for everything the updater talks to that this suite must not.
///
/// Mirrors what PanelWorld does for the portraits: the point is that a test can build the real view
/// model on whatever thread xunit hands it, with no window, no dispatcher and no network. Only the
/// decisions are exercised here, because the network, the disk and the process launch are all real
/// enough elsewhere to be worth testing against the real thing rather than a double.
/// </summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    public List<ReleaseCandidate> Releases { get; } = [];

    public List<WorkflowRun> Runs { get; } = [];

    /// <summary>Set to make the next call fail the way a real one does.</summary>
    public Exception? Throws { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Throws is not null
            ? Task.FromException<IReadOnlyList<ReleaseCandidate>>(Throws)
            : Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Releases);
    }

    public Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken)
        => Throws is not null
            ? Task.FromException<IReadOnlyList<WorkflowRun>>(Throws)
            : Task.FromResult<IReadOnlyList<WorkflowRun>>(Runs);
}

internal sealed class FakeInstallerDownloader : IInstallerDownloader
{
    public string Result { get; set; } = @"C:\updates\RainWorldCompanion-Setup.exe";

    public Exception? Throws { get; set; }

    public int Calls { get; private set; }

    /// <summary>Runs between the download starting and finishing, to change the world mid-flight.</summary>
    public Action? WhileDownloading { get; set; }

    public Task<string> DownloadAsync(
        string downloadUrl,
        string assetName,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Calls++;
        progress?.Report(0.5);
        WhileDownloading?.Invoke();

        if (Throws is not null)
        {
            return Task.FromException<string>(Throws);
        }

        progress?.Report(1.0);
        return Task.FromResult(Result);
    }

    /// <summary>The run ids asked for, so a test can prove which row was pressed.</summary>
    public List<long> BranchRuns { get; } = [];

    public Task<string> DownloadBranchBuildAsync(
        string zipUrl,
        long runId,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Calls++;
        BranchRuns.Add(runId);
        progress?.Report(0.5);
        WhileDownloading?.Invoke();

        if (Throws is not null)
        {
            return Task.FromException<string>(Throws);
        }

        progress?.Report(1.0);
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingInstallerLauncher : IInstallerLauncher
{
    public LaunchOutcome Outcome { get; set; } = new(true, "The installer is running.");

    public List<string> Started { get; } = [];

    public LaunchOutcome Start(string installerPath)
    {
        Started.Add(installerPath);
        return Outcome;
    }
}

internal sealed class FakeBusyGuard : IBusyGuard
{
    public string? Reason { get; set; }

    public string? WhyNotNow() => Reason;
}

/// <summary>Builds an UpdateViewModel with everything faked, and keeps what it wrote.</summary>
internal sealed class UpdateWorld
{
    public FakeReleaseSource Source { get; } = new();

    public FakeInstallerDownloader Downloader { get; } = new();

    public RecordingInstallerLauncher Launcher { get; } = new();

    public FakeBusyGuard Busy { get; } = new();

    /// <summary>What the view model has asked to be saved, applied in order.</summary>
    public AppSettings Saved { get; } = new();

    public int ShutdownRequests { get; private set; }

    public DateTimeOffset Now { get; set; } = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public UpdateViewModel Build(string runningVersion = "1.0.0", string sha = "", string runId = "") =>
        new(new BuildStamp(runningVersion, sha, "", runId),
            Source,
            Downloader,
            Launcher,
            Busy,
            change => change(Saved),
            () => ShutdownRequests++,
            () => Now);

    /// <summary>A release carrying a well-formed installer asset.</summary>
    public static ReleaseCandidate Release(string tag, bool prerelease = false) => new(
        tag,
        "https://github.com/mkiera/RainWorldCompanion/releases/tag/" + tag,
        false,
        prerelease,
        DateTimeOffset.UnixEpoch,
        [new ReleaseAsset(
            "RainWorldCompanion-Setup.exe",
            "https://objects.githubusercontent.com/rwc/setup.exe",
            45_000_000)]);
}
