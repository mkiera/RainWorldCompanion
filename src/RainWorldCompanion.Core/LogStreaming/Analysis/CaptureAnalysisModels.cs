namespace RainWorldCompanion.Core.LogStreaming.Analysis;

public enum CaptureEventSeverity
{
    Trace,
    Info,
    Notice,
    Warning,
    Error,
    Critical,
}

public enum CaptureEventCategory
{
    Capture,
    Session,
    Player,
    Location,
    Connection,
    Network,
    Performance,
    Action,
    Mods,
    Marker,
    Log,
}

public enum CaptureTimingConfidence
{
    Exact,
    Aligned,
    Arrival,
    Estimated,
}

public enum CaptureObservationAuthority
{
    Native,
    Direct,
    DeepTrace,
}

public sealed record CaptureParticipant(
    string Id,
    string DisplayName,
    bool? IsHost,
    bool HasSharedLogs,
    bool HasNativeObservations,
    string CoverageText);

public sealed record CaptureTimelineMoment(
    long Sequence,
    DateTimeOffset Timestamp,
    DateTimeOffset? SenderTimestamp,
    DateTimeOffset? ArrivalTimestamp,
    CaptureTimingConfidence TimingConfidence,
    CaptureEventSeverity Severity,
    CaptureEventCategory Category,
    string Kind,
    string SenderId,
    string SenderName,
    string SourceSessionId,
    string Summary,
    string Details,
    string SourceFile,
    int Generation,
    long ByteOffset,
    string? PlayerId = null,
    string? PlayerName = null,
    string? RoomId = null,
    string? Region = null,
    bool? Dead = null,
    string? Fingerprint = null,
    int DuplicateCount = 1);

public sealed record CapturePlayerObservation(
    DateTimeOffset Timestamp,
    string SenderId,
    string SenderName,
    string SourceSessionId,
    string PlayerId,
    string PlayerName,
    string? RoomId,
    string? Region,
    bool? Dead,
    bool IsLocal,
    bool IsHost,
    CaptureObservationAuthority Authority,
    string ObserverName,
    bool IsAvailable = true);

public sealed record CaptureNetworkSample(
    DateTimeOffset Timestamp,
    string ObserverId,
    string ObserverName,
    string SourceSessionId,
    string PeerId,
    string PeerName,
    bool IsHost,
    double? PingMilliseconds,
    double? LocalDeliveryQuality,
    double? RemoteDeliveryQuality,
    double? IncomingBytesPerSecond,
    double? OutgoingBytesPerSecond,
    long? PendingReliableBytes,
    long? UnacknowledgedReliableBytes,
    double? QueueMilliseconds);

public sealed record CapturePerformanceSample(
    DateTimeOffset Timestamp,
    string SenderId,
    string SenderName,
    string SourceSessionId,
    double? FrameMilliseconds,
    long? ManagedMemoryBytes,
    int? RainTimer,
    int? RainCycleLength,
    int? Cycle,
    int? Karma,
    int? KarmaCap);

public sealed record CaptureModEntry(
    string Id,
    string DisplayName,
    string Version,
    string CodeFingerprint,
    string FingerprintStatus);

public sealed record CaptureModSnapshot(
    DateTimeOffset Timestamp,
    string SenderId,
    string SenderName,
    string SourceSessionId,
    string AppVersion,
    string GameVersion,
    string GameHookVersion,
    IReadOnlyList<CaptureModEntry> Mods,
    bool Truncated);

public sealed record CaptureMapContext(
    DateTimeOffset Timestamp,
    string SenderId,
    string SourceSessionId,
    string Campaign,
    string Timeline,
    bool DownpourEnabled,
    bool DownpourKnown = true);

public sealed record CaptureIncident(
    string Id,
    DateTimeOffset Started,
    DateTimeOffset Ended,
    CaptureEventSeverity Severity,
    string Summary,
    IReadOnlyList<string> ParticipantIds,
    IReadOnlyList<string> ParticipantNames,
    IReadOnlyList<long> MomentSequences,
    bool IsCrossPlayer,
    string? RoomId,
    string? Region,
    IReadOnlyList<CaptureCauseCandidate> PossibleCauses);

public sealed record CaptureCauseCandidate(
    string ModId,
    string ModName,
    double Likelihood,
    string ConfidenceText,
    IReadOnlyList<string> Evidence);

public sealed record CaptureAnalysisSnapshot
{
    public static CaptureAnalysisSnapshot Empty(string folder = "") => new()
    {
        CaptureFolder = folder,
        StatusText = folder.Length == 0 ? "Load a capture to begin." : "The capture has no readable events.",
    };

    public string CaptureFolder { get; init; } = "";
    public string CaptureId { get; init; } = "";
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public bool IsComplete { get; init; }
    public bool IsIncomplete { get; init; }
    public bool HasGaps { get; init; }
    public long BytesWritten { get; init; }
    public string TerminationText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<CaptureParticipant> Participants { get; init; } = [];
    public IReadOnlyList<CaptureTimelineMoment> Moments { get; init; } = [];
    public IReadOnlyList<CaptureIncident> Incidents { get; init; } = [];
    public IReadOnlyList<CapturePlayerObservation> PlayerObservations { get; init; } = [];
    public IReadOnlyList<CaptureNetworkSample> NetworkSamples { get; init; } = [];
    public IReadOnlyList<CapturePerformanceSample> PerformanceSamples { get; init; } = [];
    public IReadOnlyList<CaptureModSnapshot> ModSnapshots { get; init; } = [];
    public IReadOnlyList<CaptureMapContext> MapContexts { get; init; } = [];
}
