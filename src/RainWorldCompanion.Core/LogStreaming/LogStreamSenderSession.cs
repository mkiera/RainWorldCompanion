using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RainWorldCompanion.Core.LogStreaming;

public sealed record LogStreamSenderOptions
{
    public const int DefaultChunkSize = 8 * 1024;
    public const long DefaultMaxSpoolBytes = 256L * 1024 * 1024;

    public int ChunkSize { get; init; } = DefaultChunkSize;
    public long MaxSpoolBytes { get; init; } = DefaultMaxSpoolBytes;
    public bool PollSourceLogs { get; init; } = true;
    public bool CompactAcknowledgedChunks { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed class LogStreamSenderSession
{
    private const int ProbeSize = 128;
    private const int MaximumQueuedEvents = 4096;
    private readonly object _sync = new();
    private readonly string _installRoot;
    private readonly LogStreamSenderOptions _options;
    private readonly Dictionary<string, SourceFileState> _files;
    private readonly Dictionary<string, long> _generatedOffsets = new(StringComparer.Ordinal);
    private readonly List<LogStreamChunk> _chunks = [];
    private readonly Dictionary<string, ReceiverState> _receivers = new(StringComparer.Ordinal);
    private readonly Queue<LogStreamEvent> _events = new();
    private long _spoolBytes;
    private long _firstRetainedSequence = 1;
    private long _nextSequence = 1;
    private bool _spoolBlocked;
    private bool _spoolFullReported;
    private bool _compactReceiverAdded;

    public LogStreamSenderSession(
        string installRoot,
        string captureId,
        string sourceSessionId,
        ulong senderSteamId,
        LogStreamSenderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        LogStreamValidation.RequireToken(captureId, nameof(captureId));
        LogStreamValidation.RequireToken(sourceSessionId, nameof(sourceSessionId));

        _installRoot = Path.GetFullPath(installRoot);
        CaptureId = captureId;
        SourceSessionId = sourceSessionId;
        SenderSteamId = senderSteamId;
        _options = options ?? new LogStreamSenderOptions();
        if (_options.ChunkSize != LogStreamSenderOptions.DefaultChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Log stream chunks must be 8 KiB.");
        }
        if (_options.MaxSpoolBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The spool limit must be positive.");
        }
        ArgumentNullException.ThrowIfNull(_options.TimeProvider);

        _files = (_options.PollSourceLogs ? LogStreamFileCatalog.Files : []).ToDictionary(
            file => file.Id,
            file => new SourceFileState(file),
            StringComparer.Ordinal);
        Record(new LogStreamEvent(
            Now,
            LogStreamEventKind.CaptureStarted,
            CaptureId,
            "Log capture started.",
            SourceSessionId: SourceSessionId));
    }

    public string CaptureId { get; }
    public string SourceSessionId { get; }
    public ulong SenderSteamId { get; }

    public LogStreamSenderPollResult Poll()
    {
        lock (_sync)
        {
            var addedBytes = 0L;
            var addedChunks = 0;
            var events = new List<LogStreamEvent>();

            if (_spoolBlocked)
            {
                return new LogStreamSenderPollResult(0, 0, true, events);
            }

            foreach (var state in _files.Values)
            {
                Poll(state, events, ref addedBytes, ref addedChunks);
            }

            var isFull = _spoolBytes >= _options.MaxSpoolBytes;
            if (isFull && !_spoolFullReported)
            {
                ReportSpoolFull(events);
            }

            return new LogStreamSenderPollResult(addedBytes, addedChunks, isFull, events);
        }
    }

    public bool AddReceiver(string receiverId, bool startAtCurrentEnd = false)
    {
        LogStreamValidation.RequireToken(receiverId, nameof(receiverId));
        lock (_sync)
        {
            if (_options.CompactAcknowledgedChunks && _compactReceiverAdded)
            {
                return false;
            }
            if (_receivers.ContainsKey(receiverId))
            {
                return false;
            }

            _receivers.Add(receiverId, new ReceiverState(receiverId)
            {
                LastAcknowledgedSequence = startAtCurrentEnd
                    ? _nextSequence - 1
                    : _firstRetainedSequence - 1,
                BytesTraversed = startAtCurrentEnd ? _spoolBytes : 0,
            });
            _compactReceiverAdded = _options.CompactAcknowledgedChunks;
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.ReceiverAdded,
                CaptureId,
                "A receiver was added and will receive the complete current capture.",
                receiverId,
                SourceSessionId: SourceSessionId));
            return true;
        }
    }

    public bool TryAppendGenerated(string fileId, ReadOnlySpan<byte> data)
    {
        if (!LogStreamFileCatalog.TryGet(fileId, out var file) || !file.IsGenerated)
        {
            throw new ArgumentException("The file is not a generated log stream.", nameof(fileId));
        }
        if (data.Length == 0) return true;

        lock (_sync)
        {
            if (data.Length > _options.MaxSpoolBytes - _spoolBytes)
            {
                ReportSpoolFull();
                return false;
            }

            long offset = _generatedOffsets.GetValueOrDefault(fileId);
            if (offset == 0)
            {
                Record(new LogStreamEvent(
                    Now,
                    LogStreamEventKind.GenerationStarted,
                    CaptureId,
                    "A generated diagnostic stream started.",
                    FileId: fileId,
                    Generation: 1,
                    Offset: 0,
                    SourceSessionId: SourceSessionId));
            }

            int consumed = 0;
            while (consumed < data.Length)
            {
                int length = Math.Min(_options.ChunkSize, data.Length - consumed);
                var chunk = new LogStreamChunk(
                    CaptureId,
                    SourceSessionId,
                    SenderSteamId,
                    _nextSequence++,
                    fileId,
                    1,
                    offset,
                    data.Slice(consumed, length));
                _chunks.Add(chunk);
                _spoolBytes += length;
                offset += length;
                consumed += length;
                Record(new LogStreamEvent(
                    Now,
                    LogStreamEventKind.BytesCaptured,
                    CaptureId,
                    "Generated diagnostic bytes were added to the shared spool.",
                    FileId: fileId,
                    Generation: 1,
                    Offset: chunk.Offset,
                    ByteCount: length,
                    SourceSessionId: SourceSessionId));
            }
            _generatedOffsets[fileId] = offset;
            return true;
        }
    }

    public bool RemoveReceiver(string receiverId)
    {
        LogStreamValidation.RequireToken(receiverId, nameof(receiverId));
        lock (_sync)
        {
            if (!_receivers.Remove(receiverId))
            {
                return false;
            }

            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.ReceiverRemoved,
                CaptureId,
                "The receiver was removed.",
                receiverId,
                SourceSessionId: SourceSessionId));
            return true;
        }
    }

    public IReadOnlyList<LogStreamChunk> GetPendingChunks(string receiverId, int maximumChunks = 8)
    {
        LogStreamValidation.RequireToken(receiverId, nameof(receiverId));
        if (maximumChunks is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        }

        lock (_sync)
        {
            if (!_receivers.TryGetValue(receiverId, out var receiver))
            {
                return [];
            }

            var start = checked((int)(receiver.LastAcknowledgedSequence - _firstRetainedSequence + 1));
            return _chunks.Skip(start).Take(maximumChunks).ToArray();
        }
    }

    public LogStreamAcknowledgeResult Acknowledge(
        string receiverId,
        LogStreamAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        LogStreamValidation.RequireToken(receiverId, nameof(receiverId));
        lock (_sync)
        {
            if (!_receivers.TryGetValue(receiverId, out var receiver))
            {
                return new(LogStreamAcknowledgeStatus.UnknownReceiver, 0, 0);
            }

            if (!string.Equals(acknowledgement.CaptureId, CaptureId, StringComparison.Ordinal)
                || !string.Equals(acknowledgement.SourceSessionId, SourceSessionId, StringComparison.Ordinal))
            {
                return Result(receiver, LogStreamAcknowledgeStatus.WrongSession);
            }

            if (acknowledgement.Sequence <= receiver.LastAcknowledgedSequence)
            {
                return Result(receiver, LogStreamAcknowledgeStatus.Duplicate);
            }

            if (acknowledgement.Sequence < _firstRetainedSequence
                || acknowledgement.Sequence >= _nextSequence)
            {
                return Result(receiver, LogStreamAcknowledgeStatus.UnknownChunk);
            }

            var chunk = _chunks[checked((int)(acknowledgement.Sequence - _firstRetainedSequence))];
            if (!string.Equals(chunk.Sha256, acknowledgement.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Result(receiver, LogStreamAcknowledgeStatus.HashMismatch);
            }

            if (acknowledgement.Sequence != receiver.LastAcknowledgedSequence + 1)
            {
                return Result(receiver, LogStreamAcknowledgeStatus.OutOfOrder);
            }

            receiver.LastAcknowledgedSequence = acknowledgement.Sequence;
            receiver.BytesAcknowledged += chunk.Length;
            receiver.BytesTraversed += chunk.Length;
            Record(new LogStreamEvent(
                Now,
                LogStreamEventKind.ReceiverAcknowledged,
                CaptureId,
                "A receiver acknowledged a flushed chunk.",
                receiverId,
                chunk.FileId,
                chunk.Generation,
                chunk.Offset,
                chunk.Length,
                SourceSessionId));
            CompactAcknowledgedPrefix();
            return Result(receiver, LogStreamAcknowledgeStatus.Accepted);
        }
    }

    public LogStreamSenderSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var receivers = _receivers.Values
                .OrderBy(receiver => receiver.Id, StringComparer.Ordinal)
                .Select(receiver => new LogStreamReceiverState(
                    receiver.Id,
                    receiver.LastAcknowledgedSequence,
                    receiver.BytesAcknowledged,
                    _spoolBytes - receiver.BytesTraversed))
                .ToArray();
            return new LogStreamSenderSnapshot(
                CaptureId,
                SourceSessionId,
                SenderSteamId,
                _spoolBytes,
                _options.MaxSpoolBytes,
                _chunks.Count,
                _spoolBlocked || _spoolBytes >= _options.MaxSpoolBytes,
                receivers);
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

    private void Poll(
        SourceFileState state,
        List<LogStreamEvent> events,
        ref long addedBytes,
        ref int addedChunks)
    {
        var path = Path.Combine(_installRoot, state.File.RelativePath);
        if (!IsSafeSourcePath(state.File.RelativePath))
        {
            if (!state.UnsafeSourceReported)
            {
                state.UnsafeSourceReported = true;
                AddEvent(events, new LogStreamEvent(
                    Now,
                    LogStreamEventKind.SourceRejected,
                    CaptureId,
                    "A source log was rejected because its path contains a reparse point.",
                    FileId: state.File.Id,
                    SourceSessionId: SourceSessionId));
            }
            return;
        }
        state.UnsafeSourceReported = false;
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                _options.ChunkSize,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            MarkMissing(state, events);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            MarkMissing(state, events);
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        using (stream)
        {
            var identity = SourceIdentity.Read(stream, path);
            var startsGeneration = !state.IsPresent
                || state.Identity != identity
                || stream.Length < state.Offset
                || !CapturedPrefixMatches(stream, state);

            if (!state.IsPresent)
            {
                AddEvent(events, new LogStreamEvent(
                    Now,
                    LogStreamEventKind.FileAppeared,
                    CaptureId,
                    "A log file became available.",
                    FileId: state.File.Id,
                    SourceSessionId: SourceSessionId));
            }

            if (startsGeneration)
            {
                state.StartGeneration(identity);
                AddEvent(events, new LogStreamEvent(
                    Now,
                    LogStreamEventKind.GenerationStarted,
                    CaptureId,
                    "A new source file generation started.",
                    FileId: state.File.Id,
                    Generation: state.Generation,
                    Offset: 0,
                    SourceSessionId: SourceSessionId));
            }

            var observedLength = stream.Length;
            stream.Position = state.Offset;
            while (state.Offset < observedLength)
            {
                var capacity = _options.MaxSpoolBytes - _spoolBytes;
                if (capacity <= 0)
                {
                    return;
                }

                var requested = checked((int)Math.Min(
                    Math.Min(observedLength - state.Offset, _options.ChunkSize),
                    capacity));
                var buffer = new byte[requested];
                var read = ReadAtMost(stream, buffer);
                if (read == 0)
                {
                    return;
                }

                var chunk = new LogStreamChunk(
                    CaptureId,
                    SourceSessionId,
                    SenderSteamId,
                    _nextSequence++,
                    state.File.Id,
                    state.Generation,
                    state.Offset,
                    buffer.AsSpan(0, read));
                _chunks.Add(chunk);
                _spoolBytes += read;
                addedBytes += read;
                addedChunks++;
                state.Accept(buffer.AsSpan(0, read));
                AddEvent(events, new LogStreamEvent(
                    Now,
                    LogStreamEventKind.BytesCaptured,
                    CaptureId,
                    "Source log bytes were added to the shared spool.",
                    FileId: chunk.FileId,
                    Generation: chunk.Generation,
                    Offset: chunk.Offset,
                    ByteCount: chunk.Length,
                    SourceSessionId: SourceSessionId));
            }
        }
    }

    private void MarkMissing(SourceFileState state, List<LogStreamEvent> events)
    {
        if (!state.IsPresent)
        {
            return;
        }

        state.MarkMissing();
        AddEvent(events, new LogStreamEvent(
            Now,
            LogStreamEventKind.FileDisappeared,
            CaptureId,
            "A source log file disappeared. Its captured bytes remain available.",
            FileId: state.File.Id,
            SourceSessionId: SourceSessionId));
    }

    private static bool CapturedPrefixMatches(FileStream stream, SourceFileState state)
    {
        if (!state.IsPresent || state.Offset == 0)
        {
            return true;
        }

        return ProbeMatches(stream, 0, state.HeadProbe)
            && ProbeMatches(stream, state.Offset - state.TailProbe.Length, state.TailProbe);
    }

    private static bool ProbeMatches(FileStream stream, long offset, byte[] expected)
    {
        if (expected.Length == 0 || offset < 0 || stream.Length < offset + expected.Length)
        {
            return expected.Length == 0;
        }

        var actual = new byte[expected.Length];
        stream.Position = offset;
        var read = ReadAtMost(stream, actual);
        return read == actual.Length && actual.AsSpan().SequenceEqual(expected);
    }

    private static int ReadAtMost(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private bool IsSafeSourcePath(string relativePath)
    {
        var current = _installRoot;
        foreach (var part in relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    private LogStreamAcknowledgeResult Result(
        ReceiverState receiver,
        LogStreamAcknowledgeStatus status)
        => new(status, receiver.LastAcknowledgedSequence, receiver.BytesAcknowledged);

    private void CompactAcknowledgedPrefix()
    {
        if (!_options.CompactAcknowledgedChunks || _receivers.Count != 1)
        {
            return;
        }

        long acknowledged = _receivers.Values.Single().LastAcknowledgedSequence;
        int count = checked((int)(acknowledged - _firstRetainedSequence + 1));
        if (count <= 0)
        {
            return;
        }

        long removedBytes = 0;
        for (int index = 0; index < count; index++)
        {
            removedBytes += _chunks[index].Length;
        }
        _chunks.RemoveRange(0, count);
        _firstRetainedSequence = acknowledged + 1;
        _spoolBytes -= removedBytes;
        foreach (ReceiverState receiver in _receivers.Values)
        {
            receiver.BytesTraversed = Math.Max(0, receiver.BytesTraversed - removedBytes);
        }
        if (_spoolBytes < _options.MaxSpoolBytes)
        {
            _spoolBlocked = false;
            _spoolFullReported = false;
        }
    }

    private void AddEvent(List<LogStreamEvent> current, LogStreamEvent item)
    {
        current.Add(item);
        Record(item);
    }

    private void Record(LogStreamEvent item)
    {
        while (_events.Count >= MaximumQueuedEvents)
        {
            _events.Dequeue();
        }
        _events.Enqueue(item);
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow().ToUniversalTime();

    private void ReportSpoolFull(List<LogStreamEvent>? current = null)
    {
        if (_spoolFullReported) return;
        _spoolBlocked = true;
        _spoolFullReported = true;
        var item = new LogStreamEvent(
            Now,
            LogStreamEventKind.SpoolFull,
            CaptureId,
            "The sender spool reached its configured limit.",
            ByteCount: 0,
            SourceSessionId: SourceSessionId);
        if (current is null) Record(item);
        else AddEvent(current, item);
    }

    private sealed class ReceiverState(string id)
    {
        public string Id { get; } = id;
        public long LastAcknowledgedSequence { get; set; }
        public long BytesAcknowledged { get; set; }
        public long BytesTraversed { get; set; }
    }

    private sealed class SourceFileState(LogStreamFile file)
    {
        public LogStreamFile File { get; } = file;
        public bool IsPresent { get; private set; }
        public int Generation { get; private set; }
        public long Offset { get; private set; }
        public SourceIdentity Identity { get; private set; }
        public byte[] HeadProbe { get; private set; } = [];
        public byte[] TailProbe { get; private set; } = [];
        public bool UnsafeSourceReported { get; set; }

        public void StartGeneration(SourceIdentity identity)
        {
            IsPresent = true;
            Generation++;
            Offset = 0;
            Identity = identity;
            HeadProbe = [];
            TailProbe = [];
        }

        public void Accept(ReadOnlySpan<byte> bytes)
        {
            if (HeadProbe.Length < ProbeSize)
            {
                var take = Math.Min(ProbeSize - HeadProbe.Length, bytes.Length);
                var head = new byte[HeadProbe.Length + take];
                HeadProbe.CopyTo(head, 0);
                bytes[..take].CopyTo(head.AsSpan(HeadProbe.Length));
                HeadProbe = head;
            }

            var tailLength = Math.Min(ProbeSize, checked(TailProbe.Length + bytes.Length));
            var combined = new byte[TailProbe.Length + bytes.Length];
            TailProbe.CopyTo(combined, 0);
            bytes.CopyTo(combined.AsSpan(TailProbe.Length));
            TailProbe = combined[^tailLength..];
            Offset += bytes.Length;
        }

        public void MarkMissing()
        {
            IsPresent = false;
            Offset = 0;
            Identity = default;
            HeadProbe = [];
            TailProbe = [];
        }
    }

    private readonly record struct SourceIdentity(ulong Volume, ulong FileIndex, long CreationTicks)
    {
        public static SourceIdentity Read(FileStream stream, string path)
        {
            if (OperatingSystem.IsWindows()
                && NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            {
                var file = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
                return new(information.VolumeSerialNumber, file, 0);
            }

            return new(0, 0, global::System.IO.File.GetCreationTimeUtc(path).Ticks);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
