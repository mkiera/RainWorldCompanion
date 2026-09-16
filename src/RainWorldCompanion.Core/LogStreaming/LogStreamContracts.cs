using System.Security.Cryptography;

namespace RainWorldCompanion.Core.LogStreaming;

public enum LogStreamEventKind
{
    CaptureStarted,
    FileAppeared,
    FileDisappeared,
    GenerationStarted,
    BytesCaptured,
    SourceRejected,
    SpoolFull,
    ReceiverAdded,
    ReceiverRemoved,
    ReceiverAcknowledged,
    PeerRoleChanged,
    SessionStarted,
    SessionInterrupted,
    ChunkWritten,
    DuplicateReceived,
    ChunkRejected,
    CaptureLimitReached,
    InsufficientDiskSpace,
    CaptureInterrupted,
    CaptureCompleted,
    Marker,
    DeepTraceStarted,
    DeepTraceStopped,
}

public sealed record LogStreamEvent(
    DateTimeOffset Timestamp,
    LogStreamEventKind Kind,
    string CaptureId,
    string Message,
    string? PeerId = null,
    string? FileId = null,
    int? Generation = null,
    long? Offset = null,
    int? ByteCount = null,
    string? SourceSessionId = null);

public sealed class LogStreamChunk
{
    private readonly byte[] _data;

    public LogStreamChunk(
        string captureId,
        string sourceSessionId,
        ulong senderSteamId,
        long sequence,
        string fileId,
        int generation,
        long offset,
        ReadOnlySpan<byte> data,
        string? sha256 = null)
    {
        CaptureId = captureId;
        SourceSessionId = sourceSessionId;
        SenderSteamId = senderSteamId;
        Sequence = sequence;
        FileId = fileId;
        Generation = generation;
        Offset = offset;
        _data = data.ToArray();
        Sha256 = sha256 ?? ComputeSha256(_data);
    }

    public string CaptureId { get; }
    public string SourceSessionId { get; }
    public ulong SenderSteamId { get; }
    public long Sequence { get; }
    public string FileId { get; }
    public int Generation { get; }
    public long Offset { get; }
    public int Length => _data.Length;
    public long EndOffset => checked(Offset + _data.Length);
    public string Sha256 { get; }

    public byte[] CopyData() => (byte[])_data.Clone();

    internal ReadOnlySpan<byte> DataSpan => _data;

    internal bool HasValidHash()
    {
        var computed = ComputeSha256(_data);
        return Sha256.Length == computed.Length
            && CryptographicOperations.FixedTimeEquals(
                global::System.Text.Encoding.ASCII.GetBytes(Sha256.ToUpperInvariant()),
                global::System.Text.Encoding.ASCII.GetBytes(computed));
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));
}

public sealed record LogStreamAcknowledgement(
    string CaptureId,
    string SourceSessionId,
    long Sequence,
    string Sha256);

public enum LogStreamAcknowledgeStatus
{
    Accepted,
    Duplicate,
    UnknownReceiver,
    WrongSession,
    UnknownChunk,
    OutOfOrder,
    HashMismatch,
}

public sealed record LogStreamAcknowledgeResult(
    LogStreamAcknowledgeStatus Status,
    long LastAcknowledgedSequence,
    long BytesAcknowledged);

public sealed record LogStreamReceiverState(
    string ReceiverId,
    long LastAcknowledgedSequence,
    long BytesAcknowledged,
    long BacklogBytes);

public sealed record LogStreamSenderSnapshot(
    string CaptureId,
    string SourceSessionId,
    ulong SenderSteamId,
    long SpoolBytes,
    long MaxSpoolBytes,
    int ChunkCount,
    bool IsSpoolFull,
    IReadOnlyList<LogStreamReceiverState> Receivers);

public sealed record LogStreamSenderPollResult(
    long BytesAdded,
    int ChunksAdded,
    bool IsSpoolFull,
    IReadOnlyList<LogStreamEvent> Events);

public sealed record LogStreamPeerIdentity(ulong SteamId, string SteamName, bool IsHost);

public enum LogStreamWriteStatus
{
    Written,
    Duplicate,
    WrongCapture,
    WrongSender,
    InvalidPeer,
    InvalidSession,
    InvalidFile,
    InvalidChunk,
    HashMismatch,
    SequenceGap,
    OffsetGap,
    ConflictingDuplicate,
    CaptureLimitReached,
    InsufficientDiskSpace,
    CaptureComplete,
    UnsafeDestination,
    IoError,
}

public enum LogStreamCaptureTerminationKind
{
    ReceiverStopped,
    ContextInterrupted,
}

public sealed record LogStreamWriteResult(
    LogStreamWriteStatus Status,
    string Message,
    LogStreamAcknowledgement? Acknowledgement = null,
    string? OutputPath = null)
{
    public bool ShouldAcknowledge => Acknowledgement is not null;
}

public sealed record LogStreamCaptureSnapshot(
    string CaptureId,
    string CaptureDirectory,
    long BytesWritten,
    long MaxCaptureBytes,
    int SenderCount,
    int SessionCount,
    bool IsBlocked,
    string? BlockedReason,
    bool MetadataHealthy,
    string? MetadataError,
    bool IsComplete,
    bool HasGaps,
    IReadOnlyList<LogStreamCapturedSessionState> Sessions);

public sealed record LogStreamCapturedGenerationState(
    string FileId,
    int Generation,
    long BytesWritten);

public sealed record LogStreamCapturedSessionState(
    ulong SenderSteamId,
    string SenderSteamName,
    bool InitiallyHost,
    bool IsCurrentlyHost,
    string SourceSessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    long BytesWritten,
    bool IsIncomplete,
    bool HasGaps,
    IReadOnlyList<LogStreamCapturedGenerationState> Generations);

public sealed class LogStreamViewerChunk
{
    private readonly byte[] _data;

    internal LogStreamViewerChunk(
        DateTimeOffset timestamp,
        LogStreamPeerIdentity sender,
        LogStreamChunk chunk)
    {
        Timestamp = timestamp;
        Sender = sender;
        CaptureId = chunk.CaptureId;
        SourceSessionId = chunk.SourceSessionId;
        Sequence = chunk.Sequence;
        FileId = chunk.FileId;
        Generation = chunk.Generation;
        Offset = chunk.Offset;
        _data = chunk.CopyData();
    }

    public DateTimeOffset Timestamp { get; }
    public LogStreamPeerIdentity Sender { get; }
    public string CaptureId { get; }
    public string SourceSessionId { get; }
    public long Sequence { get; }
    public string FileId { get; }
    public int Generation { get; }
    public long Offset { get; }
    public int Length => _data.Length;
    public byte[] CopyData() => (byte[])_data.Clone();
}
