using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainWorldCompanion.Core.LogStreaming;

public sealed record LogStreamCaptureOptions
{
    public const long DefaultMaxCaptureBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultReservedFreeSpaceBytes = 1L * 1024 * 1024 * 1024;

    public long MaxCaptureBytes { get; init; } = DefaultMaxCaptureBytes;
    public long ReservedFreeSpaceBytes { get; init; } = DefaultReservedFreeSpaceBytes;
    public int MaxViewerBytes { get; init; } = 1024 * 1024;
    public string? LobbyId { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<string, long>? AvailableFreeSpace { get; init; }
    internal Func<string, IOException?>? MetadataWriteFailure { get; init; }
}

public sealed class LogStreamCaptureWriter
{
    private const int MaximumChunkBytes = LogStreamSenderOptions.DefaultChunkSize;
    private const long MaximumChunkSequence = 4_000_000;
    private const int MaximumSenders = 64;
    private const int MaximumSessionsPerSender = 64;
    private const int MaximumGenerationsPerSession = 1024;
    private const int MaximumQueuedEvents = 4096;
    private const int MaximumJournaledRejectionKinds = 256;
    private const int MaximumDuplicateReceipts = 1024;
    private const int MaximumMetadataFlushPasses = 3;
    private const int MaximumMetadataOperationsPerFlush = 256;
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly object _sync = new();
    private readonly LogStreamCaptureOptions _options;
    private readonly Func<string, long> _availableFreeSpace;
    private readonly Dictionary<ulong, SenderState> _senders = [];
    private readonly Queue<LogStreamEvent> _events = new();
    private readonly Queue<LogStreamEvent> _pendingJournalEvents = new();
    private readonly Queue<LogStreamViewerChunk> _viewerChunks = new();
    private readonly HashSet<RejectionKey> _journaledRejections = [];
    private readonly DateTimeOffset _createdUtc;
    private long _bytesWritten;
    private long _captureStorageBytes;
    private long _suppressedRejectionEvents;
    private int _viewerBytes;
    private string? _blockedReason;
    private string? _metadataError;
    private string? _lastMetadataFailure;
    private bool _captureMetadataDirty = true;
    private bool _journalOverflow;
    private bool _journalRepairFailed;
    private bool _isClosed;
    private bool _isComplete;
    private DateTimeOffset? _completedUtc;

    public LogStreamCaptureWriter(
        string destinationRoot,
        string captureId,
        LogStreamCaptureOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        LogStreamValidation.RequireToken(captureId, nameof(captureId));
        CaptureId = captureId;
        _options = options ?? new LogStreamCaptureOptions();
        if (_options.MaxCaptureBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The capture limit must be positive.");
        }
        if (_options.ReservedFreeSpaceBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The free-space reserve cannot be negative.");
        }
        if (_options.MaxViewerBytes < MaximumChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The viewer buffer must hold at least one chunk.");
        }
        ArgumentNullException.ThrowIfNull(_options.TimeProvider);
        if (_options.LobbyId is not null)
        {
            LogStreamValidation.RequireToken(_options.LobbyId, nameof(options));
        }
        _availableFreeSpace = _options.AvailableFreeSpace ?? ReadAvailableFreeSpace;
        _createdUtc = _options.TimeProvider.GetUtcNow().ToUniversalTime();

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The log capture destination cannot be a linked folder.");
        }
        CaptureDirectory = CreateCaptureDirectory(root, captureId, _options.TimeProvider.GetLocalNow());
        Record(new LogStreamEvent(
            Now,
            LogStreamEventKind.CaptureStarted,
            CaptureId,
            "Incoming log capture started."));
        TryFlushMetadata();
    }

    public string CaptureId { get; }
    public string CaptureDirectory { get; }

    public LogStreamWriteResult Write(LogStreamPeerIdentity sender, LogStreamChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_sync)
        {
            if (_isClosed)
            {
                return new(LogStreamWriteStatus.CaptureComplete, "The capture has already closed.");
            }
            if (!TryFlushMetadata())
            {
                return MetadataUnavailable();
            }
            var validation = Validate(sender, chunk);
            if (validation is not null)
            {
                RecordRejection(sender, chunk, validation.Status, validation.Message);
                TryFlushMetadata();
                return validation;
            }

            if (!_senders.ContainsKey(sender.SteamId) && _senders.Count >= MaximumSenders)
            {
                return Reject(sender, chunk, LogStreamWriteStatus.CaptureLimitReached,
                    "The capture reached its sender limit.");
            }
            if (_senders.TryGetValue(sender.SteamId, out var knownSender)
                && !knownSender.Sessions.ContainsKey(chunk.SourceSessionId)
                && knownSender.Sessions.Count >= MaximumSessionsPerSender)
            {
                return Reject(sender, chunk, LogStreamWriteStatus.CaptureLimitReached,
                    "The sender reached its game-session limit for this capture.");
            }

            SenderState senderState;
            SessionState session;
            try
            {
                senderState = GetOrCreateSender(sender);
                session = GetOrCreateSession(senderState, sender, chunk.SourceSessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Reject(sender, chunk, LogStreamWriteStatus.UnsafeDestination,
                    "A safe capture directory could not be created: " + exception.GetType().Name);
            }
            if (chunk.Sequence < session.NextSequence)
            {
                return HandleDuplicate(sender, session, chunk);
            }
            if (chunk.Sequence > session.NextSequence)
            {
                MarkGap(session, chunk.Sequence);
                return Reject(sender, chunk, LogStreamWriteStatus.SequenceGap,
                    "The chunk sequence skipped data that has not been written.");
            }

            if (!session.TryGetExpectedOffset(chunk.FileId, chunk.Generation, out var expectedOffset))
            {
                MarkGap(session, chunk.Sequence);
                return Reject(sender, chunk, LogStreamWriteStatus.OffsetGap,
                    "The file generation is older than the latest received generation.");
            }
            if (chunk.Offset != expectedOffset)
            {
                MarkGap(session, chunk.Sequence);
                return Reject(sender, chunk, LogStreamWriteStatus.OffsetGap,
                    "The chunk offset does not continue the received file generation.");
            }
            if (!session.CanAcceptGeneration(chunk.FileId, chunk.Generation, MaximumGenerationsPerSession))
            {
                return Reject(sender, chunk, LogStreamWriteStatus.CaptureLimitReached,
                    "The game session reached its file-generation limit.");
            }

            var storageFailure = CheckStorageCapacity(chunk.Length, chunk.Length);
            if (storageFailure is not null)
            {
                RecordRejection(sender, chunk, storageFailure.Status, storageFailure.Message);
                TryFlushMetadata();
                return storageFailure;
            }

            string outputPath;
            try
            {
                outputPath = BuildOutputPath(session.Directory, chunk);
                if (!EnsureSafeParentDirectory(outputPath))
                {
                    return Reject(sender, chunk, LogStreamWriteStatus.UnsafeDestination,
                        "The capture destination contains a reparse point.");
                }
                if (File.Exists(outputPath)
                    && (File.GetAttributes(outputPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return Reject(sender, chunk, LogStreamWriteStatus.UnsafeDestination,
                        "The capture file is a reparse point.");
                }

                using var output = new FileStream(
                    outputPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    MaximumChunkBytes,
                    FileOptions.WriteThrough);
                if (output.Length != chunk.Offset)
                {
                    return Reject(sender, chunk, LogStreamWriteStatus.ConflictingDuplicate,
                        "The capture file was changed outside this capture.");
                }
                output.Position = chunk.Offset;
                output.Write(chunk.DataSpan);
                output.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Reject(sender, chunk, LogStreamWriteStatus.IoError,
                    "The chunk could not be written: " + exception.GetType().Name);
            }

            session.Accept(chunk, Now);
            _bytesWritten += chunk.Length;
            _captureStorageBytes += chunk.Length;
            _blockedReason = null;
            MarkMetadataDirty(session);
            AddViewerChunk(sender, chunk);
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.ChunkWritten,
                CaptureId,
                "A validated chunk was flushed to disk.",
                sender.SteamId.ToString(CultureInfo.InvariantCulture),
                chunk.FileId,
                chunk.Generation,
                chunk.Offset,
                chunk.Length,
                chunk.SourceSessionId));
            if (!TryFlushMetadata())
            {
                return MetadataUnavailable(outputPath);
            }
            var acknowledgement = new LogStreamAcknowledgement(
                CaptureId,
                chunk.SourceSessionId,
                chunk.Sequence,
                chunk.Sha256);
            session.MarkAcknowledged(chunk.Sequence);
            return new(LogStreamWriteStatus.Written, "Chunk written.", acknowledgement, outputPath);
        }
    }

    public void MarkEvent(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        lock (_sync)
        {
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.Marker,
                CaptureId,
                message.Trim()));
            TryFlushMetadata();
        }
    }

    public void ObservePeer(LogStreamPeerIdentity sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.SteamId == 0 || sender.SteamName is null || sender.SteamName.Length > 128) return;
        lock (_sync)
        {
            if (_senders.TryGetValue(sender.SteamId, out var existing)) ObservePeer(existing, sender);
            TryFlushMetadata();
        }
    }

    public void MarkSessionInterrupted(
        LogStreamPeerIdentity sender,
        string sourceSessionId,
        string reason)
    {
        TryMarkSessionInterrupted(sender, sourceSessionId, reason);
    }

    public bool TryMarkSessionInterrupted(
        LogStreamPeerIdentity sender,
        string sourceSessionId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ValidateSender(sender);
        LogStreamValidation.RequireToken(sourceSessionId, nameof(sourceSessionId));
        ValidateReason(reason);

        lock (_sync)
        {
            if (_isClosed
                || !_senders.TryGetValue(sender.SteamId, out var senderState)
                || !senderState.Sessions.TryGetValue(sourceSessionId, out var session))
            {
                TryFlushMetadata();
                return false;
            }

            ObservePeer(senderState, sender);
            MarkSessionInterrupted(sender, session, sourceSessionId, reason);
            TryFlushMetadata();
            return true;
        }
    }

    public void MarkInterrupted(string reason)
    {
        ValidateReason(reason);

        lock (_sync)
        {
            if (_isClosed)
            {
                TryFlushMetadata();
                return;
            }
            _isClosed = true;
            _isComplete = false;
            _completedUtc = null;
            foreach (var sender in _senders.Values)
            {
                foreach (var session in sender.Sessions.Values)
                {
                    session.MarkInterrupted(Now);
                    MarkMetadataDirty(session);
                }
            }
            _captureMetadataDirty = true;
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.CaptureInterrupted,
                CaptureId,
                reason.Trim()));
            TryFlushMetadata();
        }
    }

    public IReadOnlyList<LogStreamViewerChunk> GetRecentChunks(
        ulong? senderSteamId = null,
        string? fileId = null,
        int maximumBytes = 256 * 1024)
    {
        if (maximumBytes <= 0 || maximumBytes > _options.MaxViewerBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (fileId is not null && !LogStreamFileCatalog.TryGet(fileId, out _))
        {
            throw new ArgumentException("The viewer file is not in the log allowlist.", nameof(fileId));
        }

        lock (_sync)
        {
            var selected = new List<LogStreamViewerChunk>();
            var bytes = 0;
            foreach (var chunk in _viewerChunks.Reverse())
            {
                if (senderSteamId.HasValue && chunk.Sender.SteamId != senderSteamId.Value)
                {
                    continue;
                }
                if (fileId is not null && !string.Equals(chunk.FileId, fileId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (selected.Count > 0 && bytes + chunk.Length > maximumBytes)
                {
                    break;
                }
                selected.Add(chunk);
                bytes += chunk.Length;
                if (bytes >= maximumBytes)
                {
                    break;
                }
            }
            selected.Reverse();
            return selected;
        }
    }

    public LogStreamCaptureSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            TryFlushMetadata();
            return new LogStreamCaptureSnapshot(
                CaptureId,
                CaptureDirectory,
                _bytesWritten,
                _options.MaxCaptureBytes,
                _senders.Count,
                _senders.Values.Sum(sender => sender.Sessions.Count),
                _blockedReason is not null,
                _blockedReason,
                _metadataError is null,
                _metadataError,
                IsTruthfullyComplete,
                HasGaps,
                GetSessionSnapshots());
        }
    }

    public IReadOnlyList<LogStreamEvent> DrainEvents()
    {
        lock (_sync)
        {
            var events = _events.ToArray();
            _events.Clear();
            return events;
        }
    }

    private LogStreamWriteResult? Validate(LogStreamPeerIdentity sender, LogStreamChunk chunk)
    {
        if (!string.Equals(chunk.CaptureId, CaptureId, StringComparison.Ordinal))
        {
            return new(LogStreamWriteStatus.WrongCapture, "The chunk belongs to another capture.");
        }
        if (sender.SteamId == 0 || sender.SteamName is null || sender.SteamName.Length > 128)
        {
            return new(LogStreamWriteStatus.InvalidPeer, "The authenticated sender identity is invalid.");
        }
        if (chunk.SenderSteamId != sender.SteamId)
        {
            return new(LogStreamWriteStatus.WrongSender, "The authenticated sender does not match the chunk.");
        }
        if (!LogStreamValidation.IsToken(chunk.SourceSessionId))
        {
            return new(LogStreamWriteStatus.InvalidSession, "The source session identifier is invalid.");
        }
        if (!LogStreamFileCatalog.TryGet(chunk.FileId, out _))
        {
            return new(LogStreamWriteStatus.InvalidFile, "The file is not in the log allowlist.");
        }
        if (chunk.Sequence < 1 || chunk.Sequence > MaximumChunkSequence
            || chunk.Generation < 1 || chunk.Generation > MaximumGenerationsPerSession
            || chunk.Offset < 0 || chunk.Length is < 1 or > MaximumChunkBytes)
        {
            return new(LogStreamWriteStatus.InvalidChunk, "The chunk identity or size is invalid.");
        }
        if (!chunk.HasValidHash())
        {
            return new(LogStreamWriteStatus.HashMismatch, "The chunk hash does not match its bytes.");
        }
        return null;
    }

    private static void ValidateSender(LogStreamPeerIdentity sender)
    {
        if (sender.SteamId == 0 || sender.SteamName is null || sender.SteamName.Length > 128)
        {
            throw new ArgumentException("The authenticated sender identity is invalid.", nameof(sender));
        }
    }

    private static void ValidateReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    private LogStreamWriteResult HandleDuplicate(
        LogStreamPeerIdentity sender,
        SessionState session,
        LogStreamChunk chunk)
    {
        if (!session.TryGetReceipt(chunk.Sequence, out var receipt)
            || !receipt.Matches(chunk))
        {
            return Reject(sender, chunk, LogStreamWriteStatus.ConflictingDuplicate,
                "A received sequence was reused with different chunk data.");
        }

        var wasAcknowledged = receipt.Acknowledged;
        Record(new LogStreamEvent(
            Now,
            LogStreamEventKind.DuplicateReceived,
            CaptureId,
            wasAcknowledged
                ? "A duplicate flushed chunk was acknowledged again."
                : "A flushed chunk became acknowledgeable after its metadata recovered.",
            sender.SteamId.ToString(CultureInfo.InvariantCulture),
            chunk.FileId,
            chunk.Generation,
            chunk.Offset,
            chunk.Length,
            chunk.SourceSessionId));
        if (!TryFlushMetadata())
        {
            return MetadataUnavailable(BuildOutputPath(session.Directory, chunk));
        }
        var acknowledgement = new LogStreamAcknowledgement(
            CaptureId,
            chunk.SourceSessionId,
            chunk.Sequence,
            chunk.Sha256);
        var status = wasAcknowledged ? LogStreamWriteStatus.Duplicate : LogStreamWriteStatus.Written;
        receipt.MarkAcknowledged();
        return new(status, "Chunk was already written.", acknowledgement,
            BuildOutputPath(session.Directory, chunk));
    }

    private LogStreamWriteResult Reject(
        LogStreamPeerIdentity sender,
        LogStreamChunk chunk,
        LogStreamWriteStatus status,
        string message)
    {
        RecordRejection(sender, chunk, status, message);
        TryFlushMetadata();
        return new(status, message);
    }

    private LogStreamWriteResult MetadataUnavailable(string? outputPath = null)
        => new(
            LogStreamWriteStatus.IoError,
            _metadataError ?? "Required capture metadata is not yet durable.",
            OutputPath: outputPath);

    private void RecordRejection(
        LogStreamPeerIdentity sender,
        LogStreamChunk chunk,
        LogStreamWriteStatus status,
        string message)
    {
        var item = new LogStreamEvent(
            Now,
            status switch
            {
                LogStreamWriteStatus.CaptureLimitReached => LogStreamEventKind.CaptureLimitReached,
                LogStreamWriteStatus.InsufficientDiskSpace => LogStreamEventKind.InsufficientDiskSpace,
                _ => LogStreamEventKind.ChunkRejected,
            },
            CaptureId,
            message,
            sender.SteamId.ToString(CultureInfo.InvariantCulture),
            chunk.FileId,
            chunk.Generation,
            chunk.Offset,
            chunk.Length,
            chunk.SourceSessionId);
        bool journal = _journaledRejections.Count < MaximumJournaledRejectionKinds
            && _journaledRejections.Add(new(sender.SteamId, status));
        if (!journal && _suppressedRejectionEvents < long.MaxValue)
        {
            _suppressedRejectionEvents++;
            if (_suppressedRejectionEvents == 1
                || (_suppressedRejectionEvents & (_suppressedRejectionEvents - 1)) == 0)
                _captureMetadataDirty = true;
        }
        Record(item, journal);
    }

    private SenderState GetOrCreateSender(LogStreamPeerIdentity sender)
    {
        if (_senders.TryGetValue(sender.SteamId, out var existing))
        {
            ObservePeer(existing, sender);
            return existing;
        }

        var role = sender.IsHost ? "Host" : "Client";
        var senderDirectory = EnsureSafeDirectory(
            CaptureDirectory,
            SafeName(sender.SteamName) + " [" + role + "] "
                + sender.SteamId.ToString(CultureInfo.InvariantCulture));
        var created = new SenderState(senderDirectory, sender);
        _senders.Add(sender.SteamId, created);
        _captureMetadataDirty = true;
        return created;
    }

    private void ObservePeer(SenderState existing, LogStreamPeerIdentity sender)
    {
        var roleChanged = existing.CurrentIdentity.IsHost != sender.IsHost;
        var nameChanged = !string.Equals(existing.CurrentIdentity.SteamName, sender.SteamName, StringComparison.Ordinal);
        if (!roleChanged && !nameChanged) return;
        var previousRole = existing.CurrentIdentity.IsHost ? "Host" : "Client";
        existing.Observe(sender);
        if (roleChanged)
        {
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.PeerRoleChanged,
                CaptureId,
                "The sender role changed from " + previousRole + " to " + (sender.IsHost ? "Host" : "Client") + ".",
                sender.SteamId.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var session in existing.Sessions.Values) MarkMetadataDirty(session);
    }

    private SessionState GetOrCreateSession(
        SenderState senderState,
        LogStreamPeerIdentity sender,
        string sourceSessionId)
    {
        if (senderState.Sessions.TryGetValue(sourceSessionId, out var existing))
        {
            return existing;
        }

        var number = senderState.Sessions.Count + 1;
        var directory = EnsureSafeDirectory(
            senderState.Directory,
            "session-" + number.ToString("D3", CultureInfo.InvariantCulture) + " "
                + _options.TimeProvider.GetLocalNow().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
        var created = new SessionState(directory, sourceSessionId, Now);
        senderState.Sessions.Add(sourceSessionId, created);
        Record(new LogStreamEvent(
            Now,
            LogStreamEventKind.SessionStarted,
            CaptureId,
            "A new game log session started.",
            sender.SteamId.ToString(CultureInfo.InvariantCulture),
            SourceSessionId: sourceSessionId));
        MarkMetadataDirty(created);
        return created;
    }

    private static string BuildOutputPath(string sessionDirectory, LogStreamChunk chunk)
    {
        if (!LogStreamFileCatalog.TryGet(chunk.FileId, out var file))
        {
            throw new ArgumentException("The log file is not allowed.", nameof(chunk));
        }

        var generation = "generation-" + chunk.Generation.ToString("D3", CultureInfo.InvariantCulture);
        return Path.Combine(sessionDirectory, generation, file.RelativePath);
    }

    private bool EnsureSafeParentDirectory(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        var relative = Path.GetRelativePath(CaptureDirectory, parent);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        var current = CaptureDirectory;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = EnsureSafeDirectory(current, part);
        }
        return true;
    }

    private static string EnsureSafeDirectory(string parent, string child)
    {
        var path = Path.Combine(parent, child);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Capture directories cannot be reparse points.");
        }
        return path;
    }

    private static string CreateCaptureDirectory(
        string root,
        string captureId,
        DateTimeOffset timestamp)
    {
        var prefix = timestamp.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)
            + " " + captureId[..Math.Min(12, captureId.Length)];
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? "" : " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
            var candidate = Path.Combine(root, prefix + suffix);
            if (Directory.Exists(candidate))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(candidate);
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Capture directories cannot be reparse points.");
                }
                return candidate;
            }
            catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
            {
            }
        }
    }

    private static string SafeName(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value) ? "Unknown player" : value.Trim().Normalize();
        const string invalid = "<>:\"/\\|?*";
        var safe = new string(input.Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd(' ', '.');
        if (safe.Length > 60)
        {
            int length = char.IsHighSurrogate(safe[59]) ? 59 : 60;
            safe = safe[..length].TrimEnd(' ', '.');
        }
        if (safe.Length == 0)
        {
            safe = "Unknown player";
        }
        var stem = safe.Split('.')[0];
        if (ReservedFileNames.Contains(stem))
        {
            safe = "_" + safe;
        }
        return safe;
    }

    private static long ReadAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("The capture drive could not be determined.");
        }
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private void MarkGap(SessionState session, long sequence)
    {
        session.MarkGap(Now, sequence);
        MarkMetadataDirty(session);
    }

    private void MarkSessionInterrupted(
        LogStreamPeerIdentity sender,
        SessionState session,
        string sourceSessionId,
        string reason)
    {
        session.MarkInterrupted(Now);
        MarkMetadataDirty(session);
        Record(new LogStreamEvent(
            Now,
            LogStreamEventKind.SessionInterrupted,
            CaptureId,
            reason.Trim(),
            sender.SteamId.ToString(CultureInfo.InvariantCulture),
            SourceSessionId: sourceSessionId));
    }

    private void MarkMetadataDirty(SessionState session)
    {
        session.MarkMetadataDirty();
        _captureMetadataDirty = true;
    }

    private void AddViewerChunk(LogStreamPeerIdentity sender, LogStreamChunk chunk)
    {
        var item = new LogStreamViewerChunk(Now, sender, chunk);
        _viewerChunks.Enqueue(item);
        _viewerBytes += item.Length;
        while (_viewerBytes > _options.MaxViewerBytes && _viewerChunks.Count > 0)
        {
            _viewerBytes -= _viewerChunks.Dequeue().Length;
        }
    }

    private IReadOnlyList<LogStreamCapturedSessionState> GetSessionSnapshots()
        => _senders.Values
            .OrderBy(sender => sender.CurrentIdentity.SteamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(sender => sender.CurrentIdentity.SteamId)
            .SelectMany(sender => sender.Sessions.Values
                .OrderBy(session => session.StartedUtc)
                .Select(session => session.Snapshot(sender.InitialIdentity, sender.CurrentIdentity)))
            .ToArray();

    private bool TryFlushMetadata()
    {
        var operations = 0;
        for (var pass = 0;
             pass < MaximumMetadataFlushPasses
                 && operations < MaximumMetadataOperationsPerFlush
                 && HasPendingMetadata;
             pass++)
        {
            foreach (var sender in _senders.Values)
            {
                foreach (var session in sender.Sessions.Values.Where(item => item.MetadataDirty))
                {
                    if (operations >= MaximumMetadataOperationsPerFlush) break;
                    operations++;
                    if (TryWriteSessionMetadata(sender, session, out var error))
                    {
                        session.MarkMetadataCurrent();
                    }
                    else
                    {
                        SetMetadataFailure(error!);
                    }
                }
                if (operations >= MaximumMetadataOperationsPerFlush) break;
            }

            if (_captureMetadataDirty && operations < MaximumMetadataOperationsPerFlush)
            {
                operations++;
                if (TryWriteCaptureMetadata(out var error))
                {
                    _captureMetadataDirty = false;
                }
                else
                {
                    SetMetadataFailure(error!);
                }
            }

            while (_pendingJournalEvents.Count > 0
                   && operations < MaximumMetadataOperationsPerFlush)
            {
                operations++;
                if (!TryAppendEvent(_pendingJournalEvents.Peek(), out var error))
                {
                    SetMetadataFailure(error!);
                    break;
                }
                _pendingJournalEvents.Dequeue();
            }
        }

        if (HasPendingMetadata)
        {
            _metadataError = _lastMetadataFailure ?? "Required capture metadata is still waiting to be written.";
            return false;
        }

        _metadataError = null;
        _lastMetadataFailure = null;
        return true;
    }

    private bool TryWriteCaptureMetadata(out string? error)
    {
        var sessions = GetSessionSnapshots();
        var complete = IsTruthfullyComplete;
        var metadata = new
        {
            schemaVersion = 1,
            captureId = CaptureId,
            lobbyId = _options.LobbyId,
            createdUtc = _createdUtc,
            completedUtc = complete ? _completedUtc : null,
            incomplete = !complete || sessions.Any(session => session.IsIncomplete),
            hasGaps = HasGaps,
            bytesWritten = _bytesWritten,
            maxCaptureBytes = _options.MaxCaptureBytes,
            suppressedRejectionEvents = _suppressedRejectionEvents,
            sessions = sessions.Select(session => new
            {
                senderSteamId = session.SenderSteamId.ToString(CultureInfo.InvariantCulture),
                senderSteamName = session.SenderSteamName,
                initialRole = session.InitiallyHost ? "Host" : "Client",
                currentRole = session.IsCurrentlyHost ? "Host" : "Client",
                sourceSessionId = session.SourceSessionId,
                session.StartedUtc,
                session.UpdatedUtc,
                session.CompletedUtc,
                session.BytesWritten,
                session.IsIncomplete,
                session.HasGaps,
            }),
        };
        return TryWriteMetadata(Path.Combine(CaptureDirectory, "capture.json"), metadata, out error);
    }

    private bool TryWriteSessionMetadata(SenderState sender, SessionState session, out string? error)
    {
        var snapshot = session.Snapshot(sender.InitialIdentity, sender.CurrentIdentity);
        var metadata = new
        {
            schemaVersion = 1,
            captureId = CaptureId,
            lobbyId = _options.LobbyId,
            senderSteamId = snapshot.SenderSteamId.ToString(CultureInfo.InvariantCulture),
            senderSteamName = snapshot.SenderSteamName,
            initialRole = snapshot.InitiallyHost ? "Host" : "Client",
            currentRole = snapshot.IsCurrentlyHost ? "Host" : "Client",
            sourceSessionId = snapshot.SourceSessionId,
            snapshot.StartedUtc,
            snapshot.UpdatedUtc,
            snapshot.CompletedUtc,
            snapshot.BytesWritten,
            snapshot.IsIncomplete,
            snapshot.HasGaps,
            generations = snapshot.Generations,
        };
        return TryWriteMetadata(Path.Combine(session.Directory, "session.json"), metadata, out error);
    }

    private bool TryWriteMetadata<T>(string path, T value, out string? error)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Metadata files cannot be reparse points.");
            }
            var injectedFailure = _options.MetadataWriteFailure?.Invoke(path);
            if (injectedFailure is not null) throw injectedFailure;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, MetadataJson);
            long previousLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            var storageFailure = CheckStorageCapacity(bytes.LongLength - previousLength, bytes.LongLength);
            if (storageFailure is not null)
            {
                error = storageFailure.Message;
                return false;
            }
            using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            _captureStorageBytes += bytes.LongLength - previousLength;
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "Metadata could not be written: " + exception.GetType().Name;
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    private bool TryAppendEvent(LogStreamEvent item, out string? error)
    {
        var path = Path.Combine(CaptureDirectory, "events.jsonl");
        long? originalLength = null;
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Event journals cannot be reparse points.");
            }
            var injectedFailure = _options.MetadataWriteFailure?.Invoke(path);
            if (injectedFailure is not null) throw injectedFailure;
            var line = JsonSerializer.Serialize(item, EventJson) + Environment.NewLine;
            var bytes = new UTF8Encoding(false).GetBytes(line);
            var storageFailure = CheckStorageCapacity(bytes.LongLength, bytes.LongLength);
            if (storageFailure is not null)
            {
                error = storageFailure.Message;
                return false;
            }
            using var output = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            originalLength = output.Length;
            output.Position = output.Length;
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            _captureStorageBytes += bytes.LongLength;
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (originalLength.HasValue)
            {
                try
                {
                    using var repair = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.WriteThrough);
                    repair.SetLength(originalLength.Value);
                    repair.Flush(flushToDisk: true);
                }
                catch (Exception repairException) when (repairException is IOException or UnauthorizedAccessException)
                {
                    _journalRepairFailed = true;
                    error = "The event journal could not be restored after a partial write: "
                        + repairException.GetType().Name;
                    return false;
                }
            }
            error = "The event journal could not be written: " + exception.GetType().Name;
            return false;
        }
    }

    private void SetMetadataFailure(string error)
    {
        _lastMetadataFailure = error;
        _metadataError = error;
    }

    private void Record(LogStreamEvent item, bool journal = true)
    {
        while (_events.Count >= MaximumQueuedEvents)
        {
            _events.Dequeue();
        }
        _events.Enqueue(item);
        if (!journal) return;
        if (_pendingJournalEvents.Count >= MaximumQueuedEvents)
        {
            _journalOverflow = true;
            SetMetadataFailure("The event journal recovery queue reached its limit.");
            return;
        }
        _pendingJournalEvents.Enqueue(item);
    }

    private LogStreamWriteResult? CheckStorageCapacity(long finalGrowth, long temporaryBytes)
    {
        if (finalGrowth > 0 && _captureStorageBytes > _options.MaxCaptureBytes - finalGrowth)
        {
            _blockedReason = "The capture reached its configured size limit.";
            return new(LogStreamWriteStatus.CaptureLimitReached, _blockedReason);
        }

        long available;
        try
        {
            available = _availableFreeSpace(CaptureDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(LogStreamWriteStatus.IoError, "Available disk space could not be checked.");
        }
        if (available < temporaryBytes || available - temporaryBytes < _options.ReservedFreeSpaceBytes)
        {
            _blockedReason = "Writing capture data would use the configured free-space reserve.";
            return new(LogStreamWriteStatus.InsufficientDiskSpace, _blockedReason);
        }
        return null;
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow().ToUniversalTime();

    private bool HasGaps => _senders.Values
        .SelectMany(sender => sender.Sessions.Values)
        .Any(session => session.HasGaps);

    private bool HasIncompleteSessions => _senders.Values
        .SelectMany(sender => sender.Sessions.Values)
        .Any(session => session.IsIncomplete);

    private bool IsTruthfullyComplete => _isComplete && !HasIncompleteSessions && !HasGaps;

    private bool HasPendingMetadata => _captureMetadataDirty
        || _journalOverflow
        || _journalRepairFailed
        || _pendingJournalEvents.Count > 0
        || _senders.Values.SelectMany(sender => sender.Sessions.Values).Any(session => session.MetadataDirty);

    private readonly record struct RejectionKey(ulong SenderSteamId, LogStreamWriteStatus Status);

    private sealed class SenderState(string directory, LogStreamPeerIdentity identity)
    {
        public string Directory { get; } = directory;
        public LogStreamPeerIdentity InitialIdentity { get; } = identity;
        public LogStreamPeerIdentity CurrentIdentity { get; private set; } = identity;
        public Dictionary<string, SessionState> Sessions { get; } = new(StringComparer.Ordinal);

        public void Observe(LogStreamPeerIdentity identity) => CurrentIdentity = identity;
    }

    private sealed class SessionState(
        string directory,
        string sourceSessionId,
        DateTimeOffset startedUtc)
    {
        private readonly Dictionary<string, int> _latestGenerations = new(StringComparer.Ordinal);
        private readonly Dictionary<(string FileId, int Generation), long> _offsets = [];
        private readonly Dictionary<long, Receipt> _receipts = [];
        private readonly Queue<long> _receiptOrder = [];

        public string Directory { get; } = directory;
        public string SourceSessionId { get; } = sourceSessionId;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public DateTimeOffset UpdatedUtc { get; private set; } = startedUtc;
        public DateTimeOffset? CompletedUtc { get; private set; }
        public long BytesWritten { get; private set; }
        public bool HasGaps { get; private set; }
        public bool IsIncomplete => CompletedUtc is null || HasGaps;
        public bool MetadataDirty { get; private set; } = true;
        private long _highestGapSequence;
        public long NextSequence { get; private set; } = 1;

        public bool TryGetExpectedOffset(string fileId, int generation, out long offset)
        {
            if (_latestGenerations.TryGetValue(fileId, out var latest) && generation < latest)
            {
                offset = 0;
                return false;
            }

            if (!_latestGenerations.TryGetValue(fileId, out latest) || generation > latest)
            {
                offset = 0;
                return true;
            }

            return _offsets.TryGetValue((fileId, generation), out offset);
        }

        public bool CanAcceptGeneration(string fileId, int generation, int maximumGenerations)
            => _offsets.ContainsKey((fileId, generation)) || _offsets.Count < maximumGenerations;

        public void Accept(LogStreamChunk chunk, DateTimeOffset timestamp)
        {
            _latestGenerations[chunk.FileId] = chunk.Generation;
            _offsets[(chunk.FileId, chunk.Generation)] = chunk.EndOffset;
            _receipts[chunk.Sequence] = Receipt.From(chunk);
            _receiptOrder.Enqueue(chunk.Sequence);
            while (_receiptOrder.Count > MaximumDuplicateReceipts)
            {
                _receipts.Remove(_receiptOrder.Dequeue());
            }
            NextSequence++;
            BytesWritten += chunk.Length;
            UpdatedUtc = timestamp;
            CompletedUtc = null;
            if (_highestGapSequence > 0 && NextSequence > _highestGapSequence)
            {
                _highestGapSequence = 0;
                HasGaps = false;
            }
        }

        public bool TryGetReceipt(long sequence, out Receipt receipt)
            => _receipts.TryGetValue(sequence, out receipt!);

        public void MarkAcknowledged(long sequence)
        {
            if (_receipts.TryGetValue(sequence, out var receipt)) receipt.MarkAcknowledged();
        }

        public void MarkGap(DateTimeOffset timestamp, long sequence)
        {
            HasGaps = true;
            _highestGapSequence = Math.Max(_highestGapSequence, sequence);
            CompletedUtc = null;
            UpdatedUtc = timestamp;
        }

        public void MarkInterrupted(DateTimeOffset timestamp)
        {
            CompletedUtc = null;
            UpdatedUtc = timestamp;
        }

        public void MarkMetadataDirty() => MetadataDirty = true;

        public void MarkMetadataCurrent() => MetadataDirty = false;

        public LogStreamCapturedSessionState Snapshot(
            LogStreamPeerIdentity initialSender,
            LogStreamPeerIdentity currentSender)
        {
            var generations = _offsets
                .OrderBy(item => item.Key.FileId, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Generation)
                .Select(item => new LogStreamCapturedGenerationState(
                    item.Key.FileId,
                    item.Key.Generation,
                    item.Value))
                .ToArray();
            return new LogStreamCapturedSessionState(
                currentSender.SteamId,
                currentSender.SteamName,
                initialSender.IsHost,
                currentSender.IsHost,
                SourceSessionId,
                StartedUtc,
                UpdatedUtc,
                CompletedUtc,
                BytesWritten,
                IsIncomplete,
                HasGaps,
                generations);
        }
    }

    private sealed class Receipt(
        string fileId,
        int generation,
        long offset,
        int length,
        string sha256)
    {
        public string FileId { get; } = fileId;
        public int Generation { get; } = generation;
        public long Offset { get; } = offset;
        public int Length { get; } = length;
        public string Sha256 { get; } = sha256;
        public bool Acknowledged { get; private set; }

        public static Receipt From(LogStreamChunk chunk)
            => new(chunk.FileId, chunk.Generation, chunk.Offset, chunk.Length, chunk.Sha256);

        public void MarkAcknowledged() => Acknowledged = true;

        public bool Matches(LogStreamChunk chunk)
            => Generation == chunk.Generation
                && Offset == chunk.Offset
                && Length == chunk.Length
                && string.Equals(FileId, chunk.FileId, StringComparison.Ordinal)
                && string.Equals(Sha256, chunk.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
