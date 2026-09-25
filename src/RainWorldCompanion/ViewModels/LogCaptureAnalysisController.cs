using RainWorldCompanion.Core.LogStreaming.Analysis;

namespace RainWorldCompanion.ViewModels;

public interface ILogCaptureAnalysisController
{
    string CaptureFolder { get; }
    CaptureAnalysisSnapshot Snapshot { get; }
    Task<CaptureAnalysisSnapshot> OpenAsync(string folder, CancellationToken cancellationToken = default);
    Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

internal sealed class LogCaptureAnalysisController : ILogCaptureAnalysisController
{
    private LogCaptureAnalysisSession? _session;

    public string CaptureFolder => _session?.CaptureFolder ?? "";
    public CaptureAnalysisSnapshot Snapshot { get; private set; } = CaptureAnalysisSnapshot.Empty();

    public async Task<CaptureAnalysisSnapshot> OpenAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        var session = new LogCaptureAnalysisSession(folder);
        CaptureAnalysisSnapshot snapshot = await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
        _session = session;
        Snapshot = snapshot;
        return snapshot;
    }

    public async Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) return Snapshot;
        Snapshot = await _session.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Snapshot;
    }
}
