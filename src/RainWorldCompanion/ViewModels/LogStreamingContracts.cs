namespace RainWorldCompanion.ViewModels;

public interface ILogStreamingController
{
    LogStreamingUiState Snapshot();

    Task SetReceiverAvailabilityAsync(bool available);

    Task SetCaptureStateAsync(LogStreamingCaptureState state);

    Task SetDeepTraceEnabledAsync(bool enabled);

    Task PrepareSharingAsync(IReadOnlyList<string> receiverIds);

    Task RevokeSharingAsync(IReadOnlyList<string> receiverIds);

    Task RevokeAllSharingAsync();

    Task OpenCaptureFolderAsync();

    Task SetCaptureDestinationAsync(string path);

    Task MarkEventAsync(string note);
}

public enum LogStreamingCaptureState
{
    Stopped,
    Capturing,
    Paused
}

public enum LogStreamingPeerState
{
    Unsupported,
    NotSharing,
    Ready,
    Streaming,
    Reconnecting,
    ReceiverPaused,
    StorageLimited,
    Disconnected
}

public enum LogStreamingStatusTone
{
    Danger,
    Success,
    Warning,
    Muted
}

public sealed record LogStreamingPeerUiState
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsHost { get; init; }
    public bool IsLocal { get; init; }
    public bool CanReceiveMyLogs { get; init; }
    public bool IsReceivingMyLogs { get; init; }
    public bool ReceiverDeepTraceEnabled { get; init; }
    public LogStreamingDirectionUiState Incoming { get; init; } = new();
    public LogStreamingDirectionUiState Outgoing { get; init; } = new();
}

public sealed record LogStreamingDirectionUiState
{
    public LogStreamingPeerState State { get; init; } = LogStreamingPeerState.NotSharing;
    public string Detail { get; init; } = "";
    public double ThroughputBytesPerSecond { get; init; }
    public long AcknowledgedBytes { get; init; }
    public long BacklogBytes { get; init; }
    public TimeSpan? AcknowledgementAge { get; init; }
    public int ReconnectCount { get; init; }
    public string LogSession { get; init; } = "";
}

public sealed record LogStreamingLineUiState
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string SenderId { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed record LogStreamingChartUiSample(
    DateTimeOffset Time,
    double ThroughputBytesPerSecond,
    long BacklogBytes,
    double? AcknowledgementAgeSeconds = null);

public sealed record LogStreamingUiState
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsSteamLobby { get; init; }
    public bool ReceiverAdvertised { get; init; }
    public bool DeepTraceEnabled { get; init; }
    public LogStreamingCaptureState CaptureState { get; init; }
    public string CaptureFolder { get; init; } = "";
    public string CaptureDestination { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<LogStreamingPeerUiState> Peers { get; init; } = [];
    public IReadOnlyList<LogStreamingLineUiState> Lines { get; init; } = [];
    public IReadOnlyList<LogStreamingChartUiSample> Samples { get; init; } = [];
}
