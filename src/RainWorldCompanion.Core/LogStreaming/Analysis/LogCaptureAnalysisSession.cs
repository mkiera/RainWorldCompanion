using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Core.LogStreaming.Analysis;

public sealed partial class LogCaptureAnalysisSession
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const int MaximumLineBytes = 1024 * 1024;
    private const int MaximumFiles = 32_768;
    private const int MaximumMoments = 500_000;
    private const int MaximumWarnings = 200;
    private static readonly TimeSpan IncidentWindow = TimeSpan.FromSeconds(2);
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };
    private static readonly IReadOnlyList<LogStreamFile> AllowedFiles =
        [.. LogStreamFileCatalog.Files, .. LogStreamFileCatalog.Generated];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private FileSignature? _journalSignature;
    private JournalData? _journalCache;
    private CaptureAnalysisSnapshot _last = CaptureAnalysisSnapshot.Empty();

    public LogCaptureAnalysisSession(string captureFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureFolder);
        CaptureFolder = Path.GetFullPath(captureFolder);
    }

    public string CaptureFolder { get; }

    public async Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _last = await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    private CaptureAnalysisSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoot();
        var warnings = new WarningCollector();
        CaptureMetadata metadata = ReadCaptureMetadata(warnings);
        IReadOnlyList<SessionMetadata> sessions = ReadSessions(warnings, cancellationToken);
        var names = sessions.GroupBy(item => item.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().SenderName, StringComparer.Ordinal);
        JournalData journal = ReadJournal(names, warnings, cancellationToken);
        var parsed = new List<ParsedFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;

        foreach (SessionMetadata session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (GenerationFolder generation in ReadGenerations(session, warnings))
            {
                foreach (LogStreamFile file in AllowedFiles)
                {
                    string path = Path.Combine(generation.Path, file.RelativePath);
                    if (!File.Exists(path)) continue;
                    if (++fileCount > MaximumFiles)
                    {
                        warnings.Add("The capture contains more files than the analysis limit. Remaining files were skipped.");
                        break;
                    }
                    if (!IsSafeFile(path, generation.Path, warnings)) continue;
                    string fullPath = Path.GetFullPath(path);
                    seenPaths.Add(fullPath);
                    FileSignature signature = Signature(fullPath);
                    string journalKey = JournalKey(session.SenderId, session.SourceSessionId, file.Id, generation.Number);
                    bool remapArrival = journal.SignatureChanged && journal.ChangedKeys.Contains(journalKey);
                    if (_cache.TryGetValue(fullPath, out CachedFile? cached)
                        && cached.Signature == signature && !remapArrival)
                    {
                        parsed.Add(cached.Parsed);
                        continue;
                    }

                    ParsedFile result;
                    try
                    {
                        if (cached is not null && signature.Length > cached.Signature.Length
                            && EndsWithNewline(fullPath, cached.Signature.Length))
                        {
                            long start = file.IsGenerated
                                ? cached.Signature.Length
                                : FindNextLineStart(fullPath, Math.Max(0, cached.Signature.Length - 64 * 1024));
                            ParsedFile delta = file.IsGenerated
                                ? ParseStructuredFile(fullPath, file.Id, generation.Number, session, journal, warnings,
                                    cancellationToken, start)
                                : ParseRawLog(fullPath, file.Id, generation.Number, session, journal, warnings,
                                    cancellationToken, start);
                            result = MergeParsed(cached.Parsed, delta, file.IsGenerated ? null : start);
                        }
                        else
                        {
                            result = file.IsGenerated
                                ? ParseStructuredFile(fullPath, file.Id, generation.Number, session, journal, warnings, cancellationToken)
                                : ParseRawLog(fullPath, file.Id, generation.Number, session, journal, warnings, cancellationToken);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"Could not read {RelativeSource(fullPath)}: {error.Message}");
                        continue;
                    }
                    _cache[fullPath] = new(signature, result);
                    parsed.Add(result);
                }
            }
        }

        foreach (string stale in _cache.Keys.Where(path => !seenPaths.Contains(path)).ToArray()) _cache.Remove(stale);
        return BuildSnapshot(metadata, sessions, journal, parsed, warnings);
    }

    private void ValidateRoot()
    {
        if (!Directory.Exists(CaptureFolder))
            throw new DirectoryNotFoundException("The selected log capture folder no longer exists.");
        if ((File.GetAttributes(CaptureFolder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked folders cannot be opened as log captures.");
        string metadataPath = Path.Combine(CaptureFolder, "capture.json");
        if (!File.Exists(metadataPath))
            throw new InvalidDataException("The selected folder does not contain capture.json.");
        if ((File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A linked capture.json file cannot be opened.");
    }

    private CaptureMetadata ReadCaptureMetadata(WarningCollector warnings)
    {
        string path = Path.Combine(CaptureFolder, "capture.json");
        try
        {
            using JsonDocument document = ReadJsonFile(path, MaximumMetadataBytes);
            JsonElement root = document.RootElement;
            int schema = Integer(root, "schemaVersion") ?? 0;
            if (schema != 1) warnings.Add($"Capture metadata schema {schema} is newer or unsupported. Known fields were loaded.");
            return new(
                Text(root, "captureId") ?? Path.GetFileName(CaptureFolder),
                Timestamp(root, "createdUtc"),
                Timestamp(root, "endedUtc") ?? Timestamp(root, "completedUtc"),
                Boolean(root, "incomplete") ?? true,
                Boolean(root, "hasGaps") ?? false,
                Long(root, "bytesWritten") ?? 0,
                Text(root, "terminationKind") ?? "",
                Text(root, "terminationReason") ?? "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("capture.json could not be read: " + error.Message, error);
        }
    }

    private IReadOnlyList<SessionMetadata> ReadSessions(WarningCollector warnings, CancellationToken cancellationToken)
    {
        var sessions = new List<SessionMetadata>();
        foreach (string senderDirectory in SafeDirectories(CaptureFolder, warnings).Take(64))
        {
            foreach (string sessionDirectory in SafeDirectories(senderDirectory, warnings).Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string metadataPath = Path.Combine(sessionDirectory, "session.json");
                if (!File.Exists(metadataPath)) continue;
                if (!IsSafeFile(metadataPath, sessionDirectory, warnings)) continue;
                try
                {
                    using JsonDocument document = ReadJsonFile(metadataPath, MaximumMetadataBytes);
                    JsonElement root = document.RootElement;
                    string senderId = Text(root, "senderSteamId") ?? "unknown:" + sessions.Count.ToString(CultureInfo.InvariantCulture);
                    string senderName = CleanName(Text(root, "senderSteamName"));
                    string sourceSession = Text(root, "sourceSessionId") ?? Path.GetFileName(sessionDirectory);
                    string role = Text(root, "initialRole") ?? Text(root, "currentRole") ?? "Client";
                    sessions.Add(new(
                        senderId,
                        senderName,
                        role.Equals("Host", StringComparison.OrdinalIgnoreCase),
                        sourceSession,
                        sessionDirectory,
                        Timestamp(root, "startedUtc"),
                        Timestamp(root, "updatedUtc"),
                        Timestamp(root, "completedUtc"),
                        Boolean(root, "isIncomplete") ?? true,
                        Boolean(root, "hasGaps") ?? false));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    warnings.Add($"Skipped unreadable session metadata in {Path.GetFileName(sessionDirectory)}: {error.Message}");
                }
            }
        }
        return sessions.OrderBy(item => item.StartedUtc).ThenBy(item => item.SenderId, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<GenerationFolder> ReadGenerations(SessionMetadata session, WarningCollector warnings)
    {
        foreach (string path in SafeDirectories(session.Path, warnings).Take(1024))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith("generation-", StringComparison.OrdinalIgnoreCase)) continue;
            int number = int.TryParse(name.AsSpan("generation-".Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
            yield return new(number, path);
        }
    }

    private static IEnumerable<string> SafeDirectories(string parent, WarningCollector warnings)
    {
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateDirectories(parent).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate {Path.GetFileName(parent)}: {error.Message}");
            yield break;
        }

        foreach (string path in paths)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not inspect {Path.GetFileName(path)}: {error.Message}");
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Skipped linked directory {Path.GetFileName(path)}.");
                continue;
            }
            yield return path;
        }
    }

    private static bool IsSafeFile(string path, string generationRoot, WarningCollector warnings)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(generationRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string? current = Path.GetDirectoryName(full);
            while (current is not null && current.Length >= generationRoot.Length)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"Skipped log under linked directory {Path.GetFileName(current)}.");
                    return false;
                }
                if (string.Equals(current, generationRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Skipped linked log file {Path.GetFileName(full)}.");
                return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"Could not inspect {Path.GetFileName(path)}: {error.Message}");
            return false;
        }
    }

    private static JsonDocument ReadJsonFile(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) throw new InvalidDataException("The metadata file exceeds the size limit.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return JsonDocument.Parse(input, JsonOptions);
    }

    private static FileSignature Signature(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static bool EndsWithNewline(string path, long length)
    {
        if (length == 0) return true;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        if (input.Length < length) return false;
        input.Position = length - 1;
        return input.ReadByte() == (byte)'\n';
    }

    private static long FindNextLineStart(string path, long requested)
    {
        if (requested <= 0) return 0;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (requested >= input.Length) return input.Length;
        input.Position = requested - 1;
        if (input.ReadByte() == (byte)'\n') return requested;
        input.Position = requested;
        int value;
        while ((value = input.ReadByte()) >= 0)
            if (value == (byte)'\n') return input.Position;
        return input.Length;
    }

    private static ParsedFile MergeParsed(ParsedFile previous, ParsedFile delta, long? rawCutoff)
    {
        var result = new ParsedFile(previous.IsStructured);
        result.Moments.AddRange(rawCutoff is { } cutoff
            ? previous.Moments.Where(moment => moment.ByteOffset < cutoff)
            : previous.Moments);
        result.Moments.AddRange(delta.Moments);
        result.PlayerObservations.AddRange(previous.PlayerObservations);
        result.PlayerObservations.AddRange(delta.PlayerObservations);
        result.NetworkSamples.AddRange(previous.NetworkSamples);
        result.NetworkSamples.AddRange(delta.NetworkSamples);
        result.PerformanceSamples.AddRange(previous.PerformanceSamples);
        result.PerformanceSamples.AddRange(delta.PerformanceSamples);
        result.ModSnapshots.AddRange(previous.ModSnapshots);
        result.ModSnapshots.AddRange(delta.ModSnapshots);
        result.MapContexts.AddRange(previous.MapContexts);
        result.MapContexts.AddRange(delta.MapContexts);
        result.NativeParticipants.UnionWith(previous.NativeParticipants);
        result.NativeParticipants.UnionWith(delta.NativeParticipants);
        result.Anchors.AddRange(previous.Anchors);
        result.Anchors.AddRange(delta.Anchors);
        return result;
    }

    private JournalData ReadJournal(
        IReadOnlyDictionary<string, string> senderNames,
        WarningCollector warnings,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(CaptureFolder, "events.jsonl");
        if (!File.Exists(path))
        {
            warnings.Add("The receiver event journal is missing. Raw log times are estimates.");
            return new([], new(StringComparer.Ordinal), false, new(StringComparer.Ordinal));
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            warnings.Add("The linked receiver event journal was not read.");
            return new([], new(StringComparer.Ordinal), false, new(StringComparer.Ordinal));
        }

        FileSignature signature = Signature(path);
        if (_journalCache is not null && _journalSignature == signature)
            return _journalCache with { SignatureChanged = false, ChangedKeys = new(StringComparer.Ordinal) };

        var moments = new List<CaptureTimelineMoment>();
        var chunks = new Dictionary<string, List<JournalChunk>>(StringComparer.Ordinal);
        foreach (CapturedLine line in ReadUtf8Lines(path, warnings, cancellationToken))
        {
            if (line.Text.Length == 0) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line.Text, JsonOptions);
                JsonElement root = document.RootElement;
                DateTimeOffset timestamp = Timestamp(root, "timestamp") ?? DateTimeOffset.MinValue;
                string kind = Text(root, "kind") ?? "unknown";
                string senderId = Text(root, "peerId") ?? "";
                string senderName = senderNames.GetValueOrDefault(senderId, senderId.Length == 0 ? "Capture" : "Unknown player");
                string sourceSession = Text(root, "sourceSessionId") ?? "";
                string fileId = Text(root, "fileId") ?? "";
                int generation = Integer(root, "generation") ?? 0;
                long offset = Long(root, "offset") ?? 0;
                int bytes = Integer(root, "byteCount") ?? 0;
                if (kind.Equals("chunkWritten", StringComparison.OrdinalIgnoreCase))
                {
                    if (timestamp != DateTimeOffset.MinValue && senderId.Length > 0
                        && sourceSession.Length > 0 && fileId.Length > 0 && offset >= 0 && bytes > 0
                        && offset <= long.MaxValue - bytes)
                    {
                        string key = JournalKey(senderId, sourceSession, fileId, generation);
                        if (!chunks.TryGetValue(key, out List<JournalChunk>? list)) chunks[key] = list = [];
                        list.Add(new(offset, offset + bytes, timestamp));
                    }
                    continue;
                }

                if (!ShouldShowJournalEvent(kind)) continue;
                string message = Text(root, "message") ?? FriendlyKind(kind);
                CaptureEventSeverity severity = JournalSeverity(kind);
                moments.Add(new(
                    0,
                    timestamp == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : timestamp,
                    null,
                    timestamp == DateTimeOffset.MinValue ? null : timestamp,
                    timestamp == DateTimeOffset.MinValue ? CaptureTimingConfidence.Estimated : CaptureTimingConfidence.Exact,
                    severity,
                    kind.Equals("marker", StringComparison.OrdinalIgnoreCase) ? CaptureEventCategory.Marker : CaptureEventCategory.Capture,
                    kind,
                    senderId,
                    senderName,
                    sourceSession,
                    message,
                    message,
                    "events.jsonl",
                    generation,
                    offset));
            }
            catch (JsonException)
            {
                if (!line.IsFinalPartial) warnings.Add($"Skipped malformed receiver journal line at byte {line.Offset:N0}.");
            }
        }
        foreach (List<JournalChunk> list in chunks.Values) list.Sort((left, right) => left.Start.CompareTo(right.Start));
        var changedKeys = new HashSet<string>(chunks.Keys, StringComparer.Ordinal);
        if (_journalCache is not null)
        {
            changedKeys.Clear();
            foreach (string key in chunks.Keys.Concat(_journalCache.Chunks.Keys).Distinct(StringComparer.Ordinal))
            {
                bool same = chunks.TryGetValue(key, out List<JournalChunk>? current)
                    && _journalCache.Chunks.TryGetValue(key, out List<JournalChunk>? previous)
                    && current.SequenceEqual(previous);
                if (!same) changedKeys.Add(key);
            }
        }
        _journalSignature = signature;
        _journalCache = new(moments, chunks, true, changedKeys);
        return _journalCache;
    }

    private static bool ShouldShowJournalEvent(string kind) => kind is
        "captureStarted" or "sourceRejected" or "spoolFull" or "receiverAdded" or "receiverRemoved"
        or "peerRoleChanged" or "sessionStarted" or "sessionInterrupted" or "duplicateReceived"
        or "chunkRejected" or "captureLimitReached" or "insufficientDiskSpace" or "captureInterrupted"
        or "captureCompleted" or "marker" or "deepTraceStarted" or "deepTraceStopped";

    private static CaptureEventSeverity JournalSeverity(string kind) => kind switch
    {
        "sourceRejected" or "chunkRejected" or "captureLimitReached" or "insufficientDiskSpace"
            or "captureInterrupted" => CaptureEventSeverity.Error,
        "spoolFull" or "sessionInterrupted" or "duplicateReceived" or "deepTraceStarted" or "deepTraceStopped" => CaptureEventSeverity.Warning,
        "marker" => CaptureEventSeverity.Notice,
        _ => CaptureEventSeverity.Info,
    };

    private static string JournalKey(string senderId, string sourceSession, string fileId, int generation)
        => senderId + "\n" + sourceSession + "\n" + fileId + "\n" + generation.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? ArrivalAt(
        JournalData journal,
        string senderId,
        string sourceSession,
        string fileId,
        int generation,
        long offset)
    {
        return ChunkAt(journal, senderId, sourceSession, fileId, generation, offset)?.Timestamp;
    }

    private static JournalChunk? ChunkAt(
        JournalData journal,
        string senderId,
        string sourceSession,
        string fileId,
        int generation,
        long offset)
    {
        if (!journal.Chunks.TryGetValue(JournalKey(senderId, sourceSession, fileId, generation), out List<JournalChunk>? chunks)
            || chunks.Count == 0) return null;
        int low = 0;
        int high = chunks.Count - 1;
        int best = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (chunks[middle].Start <= offset) { best = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return best < 0 ? chunks[0] : chunks[best];
    }

    private static ParsedFile ParseStructuredFile(
        string path,
        string fileId,
        int generation,
        SessionMetadata session,
        JournalData journal,
        WarningCollector warnings,
        CancellationToken cancellationToken,
        long startOffset = 0)
    {
        var parsed = new ParsedFile(true);
        foreach (CapturedLine line in ReadUtf8Lines(path, warnings, cancellationToken, startOffset))
        {
            if (line.Text.Length == 0) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line.Text, JsonOptions);
                JsonElement root = document.RootElement;
                DateTimeOffset? senderTime = Timestamp(root, "timestampUtc");
                DateTimeOffset? arrival = ArrivalAt(journal, session.SenderId, session.SourceSessionId,
                    fileId, generation, line.Offset);
                DateTimeOffset time = senderTime ?? arrival ?? session.StartedUtc ?? File.GetLastWriteTimeUtc(path);
                if (senderTime is not null && arrival is not null)
                    parsed.Anchors.Add(new(session.SourceSessionId, senderTime.Value, arrival.Value));

                if (fileId.Equals("Companion/events.jsonl", StringComparison.Ordinal))
                    ParseCompanionEvent(root, time, senderTime, arrival, fileId, generation, line.Offset, session, parsed);
                else if (fileId.Equals("Companion/meadow-native.jsonl", StringComparison.Ordinal))
                    ParseNativeEvent(root, time, senderTime, arrival, fileId, generation, line.Offset, session, parsed);
                else if (fileId.Equals("Companion/deep-trace.jsonl", StringComparison.Ordinal))
                    ParseDeepTrace(root, time, session, parsed);
                else if (fileId.Equals("Companion/meadow-native-deep.jsonl", StringComparison.Ordinal))
                    ParseNativeDeep(root, time, session, parsed);
            }
            catch (JsonException)
            {
                if (!line.IsFinalPartial) warnings.Add($"Skipped malformed JSON in {RelativeSource(path)} at byte {line.Offset:N0}.");
            }
        }
        return parsed;
    }

    private static void ParseCompanionEvent(
        JsonElement root,
        DateTimeOffset time,
        DateTimeOffset? senderTime,
        DateTimeOffset? arrival,
        string fileId,
        int generation,
        long offset,
        SessionMetadata session,
        ParsedFile parsed)
    {
        string kind = Text(root, "kind") ?? "diagnostic-event";
        JsonElement details = Property(root, "details");
        if (kind == "performance-sample")
        {
            JsonElement trace = Property(details, "trace");
            JsonElement performance = Property(trace, "performance");
            parsed.PerformanceSamples.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                Double(performance, "maximumFrameMilliseconds") ?? SecondsToMilliseconds(Double(trace, "unscaledDeltaSeconds")),
                Long(trace, "managedMemoryBytes"), Integer(trace, "rainTimer"), Integer(trace, "rainCycleLength"),
                Integer(trace, "cycle"), Integer(trace, "karma"), Integer(trace, "karmaCap")));
            return;
        }
        CaptureEventSeverity severity = StructuredSeverity(kind);
        if (kind == "companion-action-result" && Boolean(details, "success") == false)
            severity = CaptureEventSeverity.Error;
        CaptureEventCategory category = StructuredCategory(kind);
        string? playerId = Text(details, "playerId");
        string? playerName = Text(details, "playerName");
        string? room = Text(details, "roomId") ?? Text(Property(details, "details"), "currentRoom");
        string? region = Text(details, "region") ?? Text(Property(details, "details"), "currentRegion");
        bool? dead = Boolean(details, "dead") ?? Boolean(Property(details, "details"), "currentDead");
        string summary = CompanionSummary(kind, details, playerName, room, region);
        string raw = JsonText(details);
        string? fingerprint = severity >= CaptureEventSeverity.Error ? Fingerprint(summary + "\n" + raw) : null;
        parsed.Moments.Add(new(
            0, time, senderTime, arrival,
            senderTime is not null && arrival is not null ? CaptureTimingConfidence.Aligned
                : arrival is not null ? CaptureTimingConfidence.Arrival : CaptureTimingConfidence.Estimated,
            severity, category, kind, session.SenderId, session.SenderName, session.SourceSessionId,
            summary, raw, fileId, generation, offset, playerId, playerName, room, region, dead, fingerprint));

        if (IsPlayerEvent(kind) && playerId is not null)
        {
            parsed.PlayerObservations.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                playerId, playerName ?? playerId, room, region, dead,
                Boolean(details, "isLocal") ?? true, Boolean(details, "isHost") ?? false,
                CaptureObservationAuthority.Direct, session.SenderName, kind != "player-left"));
        }

        if (kind is "session-start" or "campaign-changed")
        {
            string campaign = kind == "session-start"
                ? Text(details, "campaign") ?? ""
                : Text(details, "currentCampaign") ?? "";
            string timeline = kind == "session-start"
                ? Text(details, "timeline") ?? ""
                : Text(details, "currentTimeline") ?? "";
            CaptureMapContext? previousMap = parsed.MapContexts.LastOrDefault();
            bool downpour = kind == "session-start"
                ? Array(details, "enabledExpansions").Any(item =>
                    string.Equals(item.GetString(), "moreslugcats", StringComparison.OrdinalIgnoreCase))
                : previousMap?.DownpourEnabled ?? false;
            parsed.MapContexts.Add(new(time, session.SenderId, session.SourceSessionId, campaign, timeline,
                downpour, kind == "session-start" || previousMap?.DownpourKnown == true));
        }

        if (kind is "session-start" or "active-mods-changed")
        {
            JsonElement mods = kind == "session-start" ? Property(details, "activeMods") : Property(details, "current");
            parsed.ModSnapshots.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                Text(details, "appVersion") ?? "",
                Text(details, "gameVersion") ?? "",
                Text(details, "gameHookVersion") ?? "",
                ParseMods(mods),
                Boolean(details, "activeModsTruncated") ?? false));
        }

        if (kind == "frame-hitch")
        {
            parsed.PerformanceSamples.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                Double(details, "durationMilliseconds"), Long(details, "managedMemoryBytes"),
                null, null, null, null, null));
        }
    }

    private static void ParseNativeEvent(
        JsonElement root,
        DateTimeOffset time,
        DateTimeOffset? senderTime,
        DateTimeOffset? arrival,
        string fileId,
        int generation,
        long offset,
        SessionMetadata session,
        ParsedFile parsed)
    {
        string kind = Text(root, "kind") ?? "meadow-observation";
        JsonElement details = Property(root, "details");
        if (kind == "network-sample")
        {
            ParseNetworkPeers(Property(details, "peers"), time, session, parsed);
            return;
        }

        string? playerId = Text(details, "avatarId") ?? Text(details, "subjectId");
        string? playerName = Text(details, "name") ?? Text(details, "displayName");
        string? room = Text(details, "roomId") ?? Text(details, "currentRoom");
        string? region = Text(details, "region") ?? Text(details, "currentRegion");
        bool? dead = Boolean(details, "dead") ?? Boolean(details, "currentDead");
        CaptureEventSeverity severity = StructuredSeverity(kind);
        if (kind == "avatar-life-state-changed" && dead != true)
            severity = CaptureEventSeverity.Info;
        string summary = NativeSummary(kind, details, playerName, room, region);
        parsed.Moments.Add(new(
            0, time, senderTime, arrival,
            senderTime is not null && arrival is not null ? CaptureTimingConfidence.Aligned
                : arrival is not null ? CaptureTimingConfidence.Arrival : CaptureTimingConfidence.Estimated,
            severity, StructuredCategory(kind), kind, session.SenderId, session.SenderName,
            session.SourceSessionId, summary, JsonText(details), fileId, generation, offset,
            playerId, playerName, room, region, dead,
            severity >= CaptureEventSeverity.Error ? Fingerprint(summary) : null));

        if (kind.StartsWith("avatar-", StringComparison.Ordinal) && playerId is not null)
        {
            parsed.PlayerObservations.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                playerId, playerName ?? playerId, room, region, dead,
                Boolean(details, "isLocal") ?? false, Boolean(details, "isHost") ?? false,
                CaptureObservationAuthority.Native, session.SenderName, kind != "avatar-unavailable"));
            parsed.NativeParticipants.Add(new(playerId, playerName ?? playerId, Boolean(details, "isHost")));
        }
        else if (kind.StartsWith("peer-", StringComparison.Ordinal) && playerId is not null)
        {
            parsed.NativeParticipants.Add(new(playerId, playerName ?? playerId, Boolean(details, "isHost")));
        }
    }

    private static void ParseDeepTrace(
        JsonElement root,
        DateTimeOffset time,
        SessionMetadata session,
        ParsedFile parsed)
    {
        JsonElement trace = Property(root, "trace");
        parsed.PerformanceSamples.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
            SecondsToMilliseconds(Double(trace, "unscaledDeltaSeconds")), Long(trace, "managedMemoryBytes"),
            Integer(trace, "rainTimer"), Integer(trace, "rainCycleLength"), Integer(trace, "cycle"),
            Integer(trace, "karma"), Integer(trace, "karmaCap")));
        foreach (JsonElement player in Array(root, "players"))
        {
            string id = Text(player, "id") ?? "unknown-player";
            parsed.PlayerObservations.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                id, Text(player, "name") ?? id, Text(player, "roomId"), Text(player, "region"),
                Boolean(player, "dead"), Boolean(player, "isLocal") ?? true,
                Boolean(player, "isHost") ?? false, CaptureObservationAuthority.DeepTrace, session.SenderName));
        }
    }

    private static void ParseNativeDeep(
        JsonElement root,
        DateTimeOffset time,
        SessionMetadata session,
        ParsedFile parsed)
    {
        JsonElement details = Property(root, "details");
        ParseNetworkPeers(Property(details, "peers"), time, session, parsed);
        foreach (JsonElement avatar in Array(details, "avatars"))
        {
            string id = Text(avatar, "avatarId") ?? Text(avatar, "subjectId") ?? "unknown-avatar";
            string name = Text(avatar, "name") ?? id;
            parsed.PlayerObservations.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                id, name, Text(avatar, "roomId"), Text(avatar, "region"), Boolean(avatar, "dead"),
                Boolean(avatar, "isLocal") ?? false, Boolean(avatar, "isHost") ?? false,
                CaptureObservationAuthority.Native, session.SenderName));
            parsed.NativeParticipants.Add(new(id, name, Boolean(avatar, "isHost")));
        }
    }

    private static void ParseNetworkPeers(
        JsonElement peers,
        DateTimeOffset time,
        SessionMetadata session,
        ParsedFile parsed)
    {
        if (peers.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement peer in peers.EnumerateArray())
        {
            string peerId = Text(peer, "subjectId") ?? "unknown-peer";
            string peerName = Text(peer, "displayName") ?? peerId;
            JsonElement network = Property(peer, "network");
            if (network.ValueKind == JsonValueKind.Undefined) network = peer;
            JsonElement steam = Property(network, "steamConnection");
            parsed.NetworkSamples.Add(new(time, session.SenderId, session.SenderName, session.SourceSessionId,
                peerId, peerName, Boolean(peer, "isHost") ?? false,
                Double(network, "pingMilliseconds") ?? Double(steam, "pingMilliseconds"),
                Double(steam, "localDeliveryQuality"), Double(steam, "remoteDeliveryQuality"),
                Double(network, "incomingBytesPerSecond") ?? Double(steam, "incomingBytesPerSecond"),
                Double(network, "outgoingBytesPerSecond") ?? Double(steam, "outgoingBytesPerSecond"),
                Long(steam, "pendingReliableBytes"), Long(steam, "unacknowledgedReliableBytes"),
                MicrosecondsToMilliseconds(Double(steam, "queueTimeMicroseconds"))));
            parsed.NativeParticipants.Add(new(peerId, peerName, Boolean(peer, "isHost")));
        }
    }

    private static IReadOnlyList<CaptureModEntry> ParseMods(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(mod => new CaptureModEntry(
                Text(mod, "id") ?? "unknown",
                Text(mod, "displayName") ?? Text(mod, "id") ?? "Unknown mod",
                Text(mod, "version") ?? "",
                Text(mod, "codeFingerprint") ?? "",
                Text(mod, "fingerprintStatus") ?? ""))
            .Where(mod => mod.Id != "unknown")
            .DistinctBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ParsedFile ParseRawLog(
        string path,
        string fileId,
        int generation,
        SessionMetadata session,
        JournalData journal,
        WarningCollector warnings,
        CancellationToken cancellationToken,
        long startOffset = 0)
    {
        var parsed = new ParsedFile(false);
        RawErrorBuilder? active = null;
        foreach (CapturedLine line in ReadUtf8Lines(path, warnings, cancellationToken, startOffset))
        {
            string text = line.Text.TrimEnd();
            if (text.Length == 0)
            {
                FlushRawError(active, parsed, fileId, generation, session);
                active = null;
                continue;
            }

            (CaptureEventSeverity Severity, bool StartsEvent) classification = ClassifyRawLine(fileId, text);
            if (classification.StartsEvent)
            {
                FlushRawError(active, parsed, fileId, generation, session);
                JournalChunk? chunk = ChunkAt(journal, session.SenderId, session.SourceSessionId,
                    fileId, generation, line.Offset);
                DateTimeOffset? arrival = chunk?.Timestamp;
                active = new(classification.Severity, line.Offset,
                    arrival ?? session.UpdatedUtc ?? session.StartedUtc ?? File.GetLastWriteTimeUtc(path), arrival,
                    chunk is { Start: > 0 } ? CaptureTimingConfidence.Arrival : CaptureTimingConfidence.Estimated,
                    text);
                continue;
            }
            if (active is not null && IsRawContinuation(text)) active.Append(text);
        }
        FlushRawError(active, parsed, fileId, generation, session);
        return parsed;
    }

    private static void FlushRawError(
        RawErrorBuilder? error,
        ParsedFile parsed,
        string fileId,
        int generation,
        SessionMetadata session)
    {
        if (error is null) return;
        string details = error.Text;
        string summary = FirstMeaningfulLine(details);
        parsed.Moments.Add(new(
            0, error.Timestamp, null, error.Arrival,
            error.TimingConfidence,
            error.Severity, CaptureEventCategory.Log,
            error.Severity == CaptureEventSeverity.Warning ? "log-warning" : "log-error",
            session.SenderId, session.SenderName, session.SourceSessionId,
            summary, details, fileId, generation, error.Offset,
            Fingerprint: Fingerprint(details)));
    }

    private static (CaptureEventSeverity Severity, bool StartsEvent) ClassifyRawLine(string fileId, string text)
    {
        string trimmed = text.TrimStart();
        if (fileId.Equals("exceptionLog.txt", StringComparison.Ordinal))
        {
            bool start = ExceptionHeader().IsMatch(trimmed)
                || trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Unhandled", StringComparison.OrdinalIgnoreCase)
                || !trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("---", StringComparison.Ordinal);
            return (FatalLine().IsMatch(trimmed) ? CaptureEventSeverity.Critical : CaptureEventSeverity.Error, start);
        }
        if (FatalLine().IsMatch(trimmed)) return (CaptureEventSeverity.Critical, true);
        if (ErrorLine().IsMatch(trimmed) || ExceptionHeader().IsMatch(trimmed)) return (CaptureEventSeverity.Error, true);
        if (WarningLine().IsMatch(trimmed)) return (CaptureEventSeverity.Warning, true);
        return (CaptureEventSeverity.Info, false);
    }

    private static bool IsRawContinuation(string text)
    {
        string value = text.TrimStart();
        return value.StartsWith("at ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("---", StringComparison.Ordinal)
            || value.StartsWith("in ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Caused by", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Inner exception", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[", StringComparison.Ordinal) && value.Contains("stack", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstMeaningfulLine(string text)
    {
        string line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "Log error";
        return line.Length <= 240 ? line : line[..237] + "...";
    }

    [GeneratedRegex(@"(?:^|[\s\]:-])(fatal|critical)(?:[\s\[\]:-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FatalLine();

    [GeneratedRegex(@"(?:^|[\s\[])(error|err)(?:[\s\]::-]|$)|\b(?:assertion failed|crash(?:ed)?|failed to|failure)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"(?:^|[\s\[])(warning|warn)(?:[\s\]::-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarningLine();

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_.+`]*Exception|NullReferenceException|MissingMethodException|TypeLoadException|StackOverflowException)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionHeader();

    [GeneratedRegex(@"0x[0-9a-f]+|\b\d+(?:\.\d+)?\b|[A-F0-9]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolatileErrorValue();

    private static string Fingerprint(string text)
    {
        string normalized = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Take(5).Select(line => VolatileErrorValue().Replace(line.Trim().ToLowerInvariant(), "#")));
        if (normalized.Length > 2048) normalized = normalized[..2048];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];
    }

    private static CaptureEventSeverity StructuredSeverity(string kind) => kind switch
    {
        "companion-action-failed" or "capture-interrupted" or "chunk-rejected" => CaptureEventSeverity.Error,
        "player-died" or "avatar-life-state-changed" or "connection-lost" or "frame-hitch"
            or "player-marked-for-deletion" or "avatar-unavailable" or "session-interrupted"
            => CaptureEventSeverity.Warning,
        "marker" or "gameplay-started" or "gameplay-ended" or "session-start"
            or "lobby-observation-started" or "lobby-observation-ended" => CaptureEventSeverity.Notice,
        _ => CaptureEventSeverity.Info,
    };

    private static CaptureEventCategory StructuredCategory(string kind)
    {
        if (kind.Contains("room", StringComparison.Ordinal) || kind.StartsWith("avatar-location", StringComparison.Ordinal))
            return CaptureEventCategory.Location;
        if (kind.StartsWith("player-", StringComparison.Ordinal) || kind.StartsWith("avatar-", StringComparison.Ordinal))
            return CaptureEventCategory.Player;
        if (kind.Contains("connection", StringComparison.Ordinal) || kind.StartsWith("peer-", StringComparison.Ordinal))
            return CaptureEventCategory.Connection;
        if (kind.Contains("mod", StringComparison.Ordinal) || kind.Contains("expansion", StringComparison.Ordinal))
            return CaptureEventCategory.Mods;
        if (kind.StartsWith("companion-action", StringComparison.Ordinal) || kind.Contains("host-action", StringComparison.Ordinal))
            return CaptureEventCategory.Action;
        if (kind.Contains("hitch", StringComparison.Ordinal)) return CaptureEventCategory.Performance;
        if (kind.Contains("session", StringComparison.Ordinal) || kind.Contains("gameplay", StringComparison.Ordinal)
            || kind.Contains("campaign", StringComparison.Ordinal) || kind.Contains("process", StringComparison.Ordinal)
            || kind.Contains("game-state", StringComparison.Ordinal)) return CaptureEventCategory.Session;
        return CaptureEventCategory.Capture;
    }

    private static bool IsPlayerEvent(string kind) => kind is
        "player-present" or "player-joined" or "player-left" or "room-changed" or "player-died"
        or "player-revived" or "player-life-state-unknown" or "player-host-role-changed"
        or "player-realized" or "player-unrealized" or "shortcut-entered" or "shortcut-exited"
        or "player-marked-for-deletion";

    private static string CompanionSummary(
        string kind,
        JsonElement details,
        string? playerName,
        string? room,
        string? region)
    {
        string player = playerName ?? "Player";
        JsonElement nested = Property(details, "details");
        return kind switch
        {
            "session-start" => $"Session started: {Text(details, "campaign") ?? "unknown campaign"}",
            "gameplay-started" => "Gameplay started",
            "gameplay-ended" => "Gameplay ended",
            "connection-lost" => "Companion lost the Game Hook connection",
            "connection-restored" => "Game Hook connection restored",
            "room-changed" => $"{player}: {Text(nested, "previousRoom") ?? "unknown"} to {room ?? "unknown"}",
            "player-present" => $"{player} present in {room ?? region ?? "an unknown room"}",
            "player-joined" => $"{player} joined in {room ?? region ?? "an unknown room"}",
            "player-left" => $"{player} left",
            "player-died" => $"{player} died in {room ?? region ?? "an unknown room"}",
            "player-revived" => $"{player} revived in {room ?? region ?? "an unknown room"}",
            "frame-hitch" => $"Frame hitch: {Double(details, "durationMilliseconds")?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} ms",
            "companion-action-result" => $"Companion action: {Text(details, "message") ?? "result received"}",
            "rain-meadow-host-action" => Text(details, "message") ?? "Rain Meadow host action",
            _ when kind.StartsWith("companion-action-", StringComparison.Ordinal) =>
                $"{FriendlyKind(kind)}: {Text(details, "action") ?? "action"}",
            _ => FriendlyKind(kind),
        };
    }

    private static string NativeSummary(
        string kind,
        JsonElement details,
        string? playerName,
        string? room,
        string? region)
    {
        string player = playerName ?? "Player";
        return kind switch
        {
            "avatar-present" or "avatar-available" => $"{player} observed in {room ?? region ?? "an unknown room"}",
            "avatar-unavailable" => $"{player} became unavailable",
            "avatar-location-changed" => $"{player}: {Text(details, "previousRoom") ?? "unknown"} to {room ?? "unknown"}",
            "avatar-life-state-changed" => Boolean(details, "currentDead") == true ? $"{player} died" : $"{player} revived",
            "peer-joined" => $"{player} joined the lobby",
            "peer-left" => $"{player} left the lobby",
            "lobby-observation-started" => "Rain Meadow lobby observation started",
            "lobby-observation-ended" => "Rain Meadow lobby observation ended",
            _ => FriendlyKind(kind),
        };
    }

    private static string FriendlyKind(string kind)
    {
        if (kind.Length == 0) return "Event";
        string value = kind.Replace('-', ' ');
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private CaptureAnalysisSnapshot BuildSnapshot(
        CaptureMetadata metadata,
        IReadOnlyList<SessionMetadata> sessions,
        JournalData journal,
        IReadOnlyList<ParsedFile> files,
        WarningCollector warnings)
    {
        foreach (SessionMetadata session in sessions.Where(item => item.HasGaps))
            warnings.Add($"{session.SenderName}'s session {ShortId(session.SourceSessionId)} contains gaps.");
        foreach (SessionMetadata session in sessions.Where(item => item.Incomplete && item.CompletedUtc is null))
            warnings.Add($"{session.SenderName}'s session {ShortId(session.SourceSessionId)} ended without a complete handoff.");

        Dictionary<string, TimeSpan> alignment = BuildClockAlignment(files.SelectMany(file => file.Anchors));
        var moments = journal.Moments.Concat(files.SelectMany(file => file.Moments))
            .Select(moment => Align(moment, alignment))
            .OrderBy(moment => moment.Timestamp)
            .ThenBy(moment => moment.SenderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(moment => moment.SourceFile, StringComparer.Ordinal)
            .ThenBy(moment => moment.ByteOffset)
            .ToList();
        moments = DeduplicateErrors(moments);
        if (moments.Count > MaximumMoments)
        {
            warnings.Add($"The capture has more than {MaximumMoments:N0} timeline events. Low-severity events were sampled to keep the view responsive.");
            moments = LimitMoments(moments);
        }
        CaptureTimelineMoment[] sequenced = moments.Select((moment, index) => moment with { Sequence = index + 1L }).ToArray();

        CapturePlayerObservation[] observations = files.SelectMany(file => file.PlayerObservations)
            .Select(item => item with { Timestamp = Align(item.Timestamp, item.SourceSessionId, alignment) })
            .OrderBy(item => item.Timestamp).ToArray();
        CaptureNetworkSample[] network = files.SelectMany(file => file.NetworkSamples)
            .Select(item => item with { Timestamp = Align(item.Timestamp, item.SourceSessionId, alignment) })
            .OrderBy(item => item.Timestamp).ToArray();
        CapturePerformanceSample[] performance = files.SelectMany(file => file.PerformanceSamples)
            .Select(item => item with { Timestamp = Align(item.Timestamp, item.SourceSessionId, alignment) })
            .OrderBy(item => item.Timestamp).ToArray();
        CaptureModSnapshot[] mods = files.SelectMany(file => file.ModSnapshots)
            .Select(item => item with { Timestamp = Align(item.Timestamp, item.SourceSessionId, alignment) })
            .OrderBy(item => item.Timestamp).ToArray();
        CaptureMapContext[] maps = FillMapExpansionState(files.SelectMany(file => file.MapContexts)
            .Select(item => item with { Timestamp = Align(item.Timestamp, item.SourceSessionId, alignment) })
            .OrderBy(item => item.Timestamp).ToArray());
        CaptureParticipant[] participants = BuildParticipants(sessions, files);
        CaptureIncident[] incidents = BuildIncidents(sequenced, participants, mods);

        DateTimeOffset? started = metadata.StartedUtc
            ?? sequenced.Select(item => (DateTimeOffset?)item.Timestamp).FirstOrDefault()
            ?? observations.Select(item => (DateTimeOffset?)item.Timestamp).FirstOrDefault();
        DateTimeOffset? latest = new[]
        {
            sequenced.Select(item => (DateTimeOffset?)item.Timestamp).LastOrDefault(),
            observations.Select(item => (DateTimeOffset?)item.Timestamp).LastOrDefault(),
            network.Select(item => (DateTimeOffset?)item.Timestamp).LastOrDefault(),
            performance.Select(item => (DateTimeOffset?)item.Timestamp).LastOrDefault(),
        }.Where(item => item is not null).Max();
        DateTimeOffset? ended = metadata.EndedUtc ?? latest;
        bool incomplete = metadata.Incomplete || sessions.Any(session => session.Incomplete && session.CompletedUtc is null);
        bool gaps = metadata.HasGaps || sessions.Any(session => session.HasGaps);
        string termination = BuildTermination(metadata, incomplete, gaps);
        string status = sequenced.Length == 0 && observations.Length == 0
            ? "No structured events or recognized log errors were found."
            : incomplete ? "Loaded an active or interrupted capture. New data will appear while live follow is enabled."
            : "Capture loaded.";

        return new()
        {
            CaptureFolder = CaptureFolder,
            CaptureId = metadata.CaptureId,
            StartedUtc = started,
            EndedUtc = ended,
            IsComplete = !incomplete && !gaps,
            IsIncomplete = incomplete,
            HasGaps = gaps,
            BytesWritten = metadata.BytesWritten,
            TerminationText = termination,
            StatusText = status,
            Warnings = warnings.Items,
            Participants = participants,
            Moments = sequenced,
            Incidents = incidents,
            PlayerObservations = observations,
            NetworkSamples = network,
            PerformanceSamples = performance,
            ModSnapshots = mods,
            MapContexts = maps,
        };
    }

    private static Dictionary<string, TimeSpan> BuildClockAlignment(IEnumerable<ClockAnchor> anchors)
    {
        var result = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (IGrouping<string, ClockAnchor> group in anchors
                     .Where(anchor => Math.Abs((anchor.Arrival - anchor.Sender).TotalDays) <= 7)
                     .GroupBy(anchor => anchor.SourceSessionId, StringComparer.Ordinal))
        {
            double[] candidates = group.Select(anchor => (anchor.Arrival - anchor.Sender).TotalMilliseconds)
                .Order().ToArray();
            if (candidates.Length == 0) continue;
            int percentile = Math.Min(candidates.Length - 1, candidates.Length / 10);
            result[group.Key] = TimeSpan.FromMilliseconds(candidates[percentile]);
        }
        return result;
    }

    private static CaptureMapContext[] FillMapExpansionState(IReadOnlyList<CaptureMapContext> contexts)
    {
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new CaptureMapContext[contexts.Count];
        for (int index = 0; index < contexts.Count; index++)
        {
            CaptureMapContext context = contexts[index];
            if (context.DownpourKnown) known[context.SenderId] = context.DownpourEnabled;
            else if (known.TryGetValue(context.SenderId, out bool downpour))
                context = context with { DownpourEnabled = downpour, DownpourKnown = true };
            result[index] = context;
        }
        return result;
    }

    private static CaptureTimelineMoment Align(
        CaptureTimelineMoment moment,
        IReadOnlyDictionary<string, TimeSpan> alignment)
    {
        if (moment.SenderTimestamp is { } sender
            && alignment.TryGetValue(moment.SourceSessionId, out TimeSpan offset))
            return moment with { Timestamp = sender + offset, TimingConfidence = CaptureTimingConfidence.Aligned };
        if (moment.TimingConfidence == CaptureTimingConfidence.Estimated) return moment;
        if (moment.ArrivalTimestamp is { } arrival)
            return moment with { Timestamp = arrival, TimingConfidence = CaptureTimingConfidence.Arrival };
        return moment;
    }

    private static DateTimeOffset Align(
        DateTimeOffset timestamp,
        string sourceSessionId,
        IReadOnlyDictionary<string, TimeSpan> alignment)
        => alignment.TryGetValue(sourceSessionId, out TimeSpan offset) ? timestamp + offset : timestamp;

    private static List<CaptureTimelineMoment> DeduplicateErrors(IReadOnlyList<CaptureTimelineMoment> moments)
    {
        var result = new List<CaptureTimelineMoment>(moments.Count);
        var latest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CaptureTimelineMoment moment in moments)
        {
            if (moment.Fingerprint is null)
            {
                result.Add(moment);
                continue;
            }
            string key = moment.SenderId + "\n" + moment.SourceSessionId + "\n" + moment.Fingerprint;
            if (latest.TryGetValue(key, out int index)
                && Math.Abs((moment.Timestamp - result[index].Timestamp).TotalSeconds) <= 1)
            {
                CaptureTimelineMoment existing = result[index];
                string source = existing.SourceFile.Contains(moment.SourceFile, StringComparison.Ordinal)
                    ? existing.SourceFile : existing.SourceFile + ", " + moment.SourceFile;
                string details = existing.Details.Length >= moment.Details.Length ? existing.Details : moment.Details;
                result[index] = existing with
                {
                    SourceFile = source,
                    Details = details,
                    DuplicateCount = existing.DuplicateCount + moment.DuplicateCount,
                    Severity = (CaptureEventSeverity)Math.Max((int)existing.Severity, (int)moment.Severity),
                };
                continue;
            }
            latest[key] = result.Count;
            result.Add(moment);
        }
        return result;
    }

    private static List<CaptureTimelineMoment> LimitMoments(IReadOnlyList<CaptureTimelineMoment> moments)
    {
        CaptureTimelineMoment[] important = moments.Where(moment => moment.Severity >= CaptureEventSeverity.Warning
            || moment.Category is CaptureEventCategory.Marker or CaptureEventCategory.Action).ToArray();
        if (important.Length >= MaximumMoments)
        {
            int importantStride = Math.Max(1, (int)Math.Ceiling(important.Length / (double)MaximumMoments));
            return important.Where((_, index) => index % importantStride == 0).Take(MaximumMoments)
                .OrderBy(moment => moment.Timestamp)
                .ThenBy(moment => moment.SenderName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        int remaining = Math.Max(0, MaximumMoments - important.Length);
        CaptureTimelineMoment[] ordinary = moments.Where(moment => moment.Severity < CaptureEventSeverity.Warning
            && moment.Category is not (CaptureEventCategory.Marker or CaptureEventCategory.Action)).ToArray();
        int stride = remaining == 0 ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(ordinary.Length / (double)remaining));
        return important.Concat(ordinary.Where((_, index) => index % stride == 0).Take(remaining))
            .OrderBy(moment => moment.Timestamp).ThenBy(moment => moment.SenderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static CaptureParticipant[] BuildParticipants(
        IReadOnlyList<SessionMetadata> sessions,
        IReadOnlyList<ParsedFile> files)
    {
        var participants = sessions.GroupBy(session => session.SenderId, StringComparer.Ordinal)
            .Select(group =>
            {
                SessionMetadata latest = group.OrderBy(session => session.UpdatedUtc).Last();
                bool native = files.SelectMany(file => file.NativeParticipants).Any(item =>
                    item.Name.Equals(latest.SenderName, StringComparison.CurrentCultureIgnoreCase));
                return new CaptureParticipant(group.Key, latest.SenderName, latest.IsHost, true, native,
                    native ? "Shared logs and Meadow observations" : "Shared logs");
            }).ToList();
        var names = participants.Select(item => item.DisplayName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (IGrouping<string, NativeParticipant> group in files.SelectMany(file => file.NativeParticipants)
                     .Where(item => !names.Contains(item.Name))
                     .GroupBy(item => item.Id + "\n" + item.Name, StringComparer.Ordinal))
        {
            NativeParticipant latest = group.Last();
            participants.Add(new("native:" + latest.Id, latest.Name, latest.IsHost, false, true,
                "Meadow observations only. Logs unavailable."));
            names.Add(latest.Name);
        }
        return participants.OrderByDescending(item => item.IsHost == true)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static CaptureIncident[] BuildIncidents(
        IReadOnlyList<CaptureTimelineMoment> moments,
        IReadOnlyList<CaptureParticipant> participants,
        IReadOnlyList<CaptureModSnapshot> mods)
    {
        CaptureTimelineMoment[] errors = moments.Where(moment => moment.Severity >= CaptureEventSeverity.Error).ToArray();
        IEnumerable<CaptureTimelineMoment[]> clusters = ClusterTimedErrors(errors.Where(moment =>
                moment.TimingConfidence != CaptureTimingConfidence.Estimated).ToArray())
            .Concat(errors.Where(moment => moment.TimingConfidence == CaptureTimingConfidence.Estimated)
                .GroupBy(moment => moment.SenderId, StringComparer.Ordinal)
                .SelectMany(group => ClusterTimedErrors(group.OrderBy(moment => moment.Timestamp).ToArray())));
        var incidents = new List<CaptureIncident>();
        foreach (CaptureTimelineMoment[] cluster in clusters)
        {
            string[] ids = cluster.Select(item => item.SenderId).Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
            string[] names = cluster.Select(item => item.SenderName).Where(name => name.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
            bool crossPlayer = ids.Length > 1;
            CaptureEventSeverity severity = (CaptureEventSeverity)cluster.Max(item => (int)item.Severity);
            string summary = crossPlayer
                ? $"{ids.Length} players reported {cluster.Length} errors together"
                : cluster.Length > 1 ? $"{cluster.Length} related errors from {names.FirstOrDefault() ?? "one player"}"
                : cluster[0].Summary;
            string id = "incident-" + cluster[0].Sequence.ToString(CultureInfo.InvariantCulture);
            incidents.Add(new(id, cluster[0].Timestamp, cluster[^1].Timestamp, severity, summary,
                ids, names, cluster.Select(item => item.Sequence).ToArray(), crossPlayer,
                CommonValue(cluster.Select(item => item.RoomId)), CommonValue(cluster.Select(item => item.Region)),
                BuildPossibleCauses(cluster, participants, mods)));
        }
        return incidents.OrderByDescending(item => item.Started).ToArray();
    }

    private static IEnumerable<CaptureTimelineMoment[]> ClusterTimedErrors(
        IReadOnlyList<CaptureTimelineMoment> errors)
    {
        for (int start = 0; start < errors.Count;)
        {
            int end = start + 1;
            while (end < errors.Count && errors[end].Timestamp - errors[start].Timestamp <= IncidentWindow) end++;
            yield return errors.Skip(start).Take(end - start).ToArray();
            start = end;
        }
    }

    private static IReadOnlyList<CaptureCauseCandidate> BuildPossibleCauses(
        IReadOnlyList<CaptureTimelineMoment> incident,
        IReadOnlyList<CaptureParticipant> participants,
        IReadOnlyList<CaptureModSnapshot> snapshots)
    {
        string combined = string.Join("\n", incident.Select(item => item.Summary + "\n" + item.Details));
        string normalizedCombined = NormalizeEvidence(combined);
        string[] affected = incident.Select(item => item.SenderId).Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        string[] covered = participants.Where(item => item.HasSharedLogs).Select(item => item.Id).ToArray();
        var latest = snapshots.Where(snapshot => snapshot.Timestamp <= incident[^1].Timestamp)
            .GroupBy(snapshot => snapshot.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.Timestamp).Last(), StringComparer.Ordinal);
        bool hasCompleteInventoryCoverage = covered.All(id =>
            latest.TryGetValue(id, out CaptureModSnapshot? snapshot) && !snapshot.Truncated);
        var candidates = latest.Values.SelectMany(snapshot => snapshot.Mods)
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<CaptureCauseCandidate>();
        foreach (IGrouping<string, CaptureModEntry> group in candidates)
        {
            CaptureModEntry representative = group.First();
            var evidence = new List<string>();
            double score = 0;
            string[] tokens = new[] { representative.Id, representative.DisplayName }
                .Select(NormalizeEvidence).Where(token => token.Length >= 4)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (tokens.Any(normalizedCombined.Contains))
            {
                score += 0.6;
                evidence.Add("The error or stack trace names this mod or its identifier.");
            }

            int affectedWithMod = affected.Count(id => latest.TryGetValue(id, out CaptureModSnapshot? snapshot)
                && snapshot.Mods.Any(mod => mod.Id.Equals(representative.Id, StringComparison.OrdinalIgnoreCase)));
            int coveredWithMod = covered.Count(id => latest.TryGetValue(id, out CaptureModSnapshot? snapshot)
                && snapshot.Mods.Any(mod => mod.Id.Equals(representative.Id, StringComparison.OrdinalIgnoreCase)));
            if (hasCompleteInventoryCoverage && affected.Length > 0
                && affectedWithMod == affected.Length && coveredWithMod < covered.Length)
            {
                score += 0.2;
                evidence.Add("Every affected log-sharing player had this mod while at least one unaffected player did not.");
            }

            CaptureModEntry[] reportedMods = latest.Values.SelectMany(snapshot => snapshot.Mods
                    .Where(mod => mod.Id.Equals(representative.Id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            string[] completeFingerprints = reportedMods
                .Where(IsCompleteFingerprint)
                .Select(mod => mod.CodeFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!EnabledModsFile.BuiltIn.Contains(representative.Id)
                && reportedMods.Length > 0 && reportedMods.All(IsCompleteFingerprint)
                && completeFingerprints.Length > 1)
            {
                score += 0.15;
                evidence.Add("Players reported different complete code fingerprints for this mod.");
            }
            if (score <= 0) continue;
            score = Math.Min(0.95, Math.Round(score, 2));
            string confidence = score >= 0.75 ? "Strong lead" : score >= 0.45 ? "Possible lead" : "Weak lead";
            result.Add(new(representative.Id, representative.DisplayName, score, confidence, evidence));
        }
        return result.OrderByDescending(item => item.Likelihood)
            .ThenBy(item => item.ModName, StringComparer.CurrentCultureIgnoreCase).Take(5).ToArray();
    }

    private static string? CommonValue(IEnumerable<string?> values)
    {
        string[] present = values.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return present.Length == 1 ? present[0] : null;
    }

    private static string NormalizeEvidence(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsCompleteFingerprint(CaptureModEntry mod)
        => mod.CodeFingerprint.Length > 0 && mod.FingerprintStatus.Equals("complete", StringComparison.OrdinalIgnoreCase);

    private static string BuildTermination(CaptureMetadata metadata, bool incomplete, bool gaps)
    {
        if (metadata.TerminationReason.Length > 0) return metadata.TerminationReason;
        if (gaps) return "Some streamed data is missing.";
        if (incomplete) return "Capture is active or ended unexpectedly.";
        return "Capture completed cleanly.";
    }

    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12];

    private static IEnumerable<CapturedLine> ReadUtf8Lines(
        string path,
        WarningCollector warnings,
        CancellationToken cancellationToken,
        long startOffset = 0)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        startOffset = Math.Clamp(startOffset, 0, input.Length);
        input.Position = startOffset;
        var line = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long absolute = startOffset;
        long lineOffset = startOffset;
        int lineCount = 0;
        bool oversized = false;
        bool warnedOversized = false;
        bool lastWasNewline = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (int index = 0; index < read; index++)
            {
                byte value = buffer[index];
                absolute++;
                lastWasNewline = value == (byte)'\n';
                if (value == (byte)'\n')
                {
                    if (!oversized)
                    {
                        if (++lineCount > MaximumMoments * 4)
                        {
                            warnings.Add($"Stopped reading {RelativeSource(path)} after {lineCount - 1:N0} lines.");
                            yield break;
                        }
                        yield return new(lineOffset, DecodeLine(line), false);
                    }
                    else if (!warnedOversized)
                    {
                        warnings.Add($"Skipped a line larger than {MaximumLineBytes / 1024:N0} KiB in {RelativeSource(path)}.");
                        warnedOversized = true;
                    }
                    line.SetLength(0);
                    oversized = false;
                    lineOffset = absolute;
                    continue;
                }
                if (oversized) continue;
                if (line.Length >= MaximumLineBytes)
                {
                    oversized = true;
                    continue;
                }
                line.WriteByte(value);
            }
        }
        if (!lastWasNewline && !oversized && line.Length > 0)
            yield return new(lineOffset, DecodeLine(line), true);
        else if (oversized && !warnedOversized)
            warnings.Add($"Skipped a line larger than {MaximumLineBytes / 1024:N0} KiB in {RelativeSource(path)}.");
    }

    private static string DecodeLine(MemoryStream line)
    {
        ReadOnlySpan<byte> bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonElement Property(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
            ? value : default;

    private static string? Text(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out DateTimeOffset timestamp))
            return timestamp.ToUniversalTime();
        return null;
    }

    private static bool? Boolean(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? Integer(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    }

    private static long? Long(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    }

    private static double? Double(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            return null;
        return number;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
    {
        JsonElement value = Property(root, name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    }

    private static string JsonText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return "";
        string text = value.GetRawText();
        return text.Length <= 16_384 ? text : text[..16_381] + "...";
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown player";
        string text = new(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        return text.Length <= 128 ? text : text[..128];
    }

    private static string RelativeSource(string path)
    {
        string file = Path.GetFileName(path);
        string? parent = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(parent) ? file : parent + "/" + file;
    }

    private static double? SecondsToMilliseconds(double? seconds)
        => seconds is { } value && value >= 0 ? value * 1000 : null;

    private static double? MicrosecondsToMilliseconds(double? microseconds)
        => microseconds is { } value && value >= 0 ? value / 1000 : null;

    private sealed record JournalChunk(long Start, long End, DateTimeOffset Timestamp);
    private sealed record JournalData(
        IReadOnlyList<CaptureTimelineMoment> Moments,
        Dictionary<string, List<JournalChunk>> Chunks,
        bool SignatureChanged,
        HashSet<string> ChangedKeys);
    private sealed record ClockAnchor(string SourceSessionId, DateTimeOffset Sender, DateTimeOffset Arrival);
    private sealed record CapturedLine(long Offset, string Text, bool IsFinalPartial);
    private sealed record NativeParticipant(string Id, string Name, bool? IsHost);

    private sealed class ParsedFile(bool isStructured)
    {
        public bool IsStructured { get; } = isStructured;
        public List<CaptureTimelineMoment> Moments { get; } = [];
        public List<CapturePlayerObservation> PlayerObservations { get; } = [];
        public List<CaptureNetworkSample> NetworkSamples { get; } = [];
        public List<CapturePerformanceSample> PerformanceSamples { get; } = [];
        public List<CaptureModSnapshot> ModSnapshots { get; } = [];
        public List<CaptureMapContext> MapContexts { get; } = [];
        public HashSet<NativeParticipant> NativeParticipants { get; } = [];
        public List<ClockAnchor> Anchors { get; } = [];
    }

    private sealed class RawErrorBuilder(
        CaptureEventSeverity severity,
        long offset,
        DateTimeOffset timestamp,
        DateTimeOffset? arrival,
        CaptureTimingConfidence timingConfidence,
        string firstLine)
    {
        private readonly StringBuilder _text = new(firstLine);
        public CaptureEventSeverity Severity { get; } = severity;
        public long Offset { get; } = offset;
        public DateTimeOffset Timestamp { get; } = timestamp;
        public DateTimeOffset? Arrival { get; } = arrival;
        public CaptureTimingConfidence TimingConfidence { get; } = timingConfidence;
        public string Text => _text.ToString();

        public void Append(string value)
        {
            if (_text.Length >= 16_384) return;
            _text.AppendLine();
            int remaining = 16_384 - _text.Length;
            _text.Append(value.AsSpan(0, Math.Min(value.Length, Math.Max(0, remaining))));
        }
    }

    private sealed class WarningCollector
    {
        private readonly List<string> _items = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Items => _items;

        public void Add(string value)
        {
            if (_items.Count >= MaximumWarnings || !_seen.Add(value)) return;
            _items.Add(value);
        }
    }

    private sealed record CaptureMetadata(
        string CaptureId,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? EndedUtc,
        bool Incomplete,
        bool HasGaps,
        long BytesWritten,
        string TerminationKind,
        string TerminationReason);

    private sealed record SessionMetadata(
        string SenderId,
        string SenderName,
        bool IsHost,
        string SourceSessionId,
        string Path,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? UpdatedUtc,
        DateTimeOffset? CompletedUtc,
        bool Incomplete,
        bool HasGaps);

    private sealed record GenerationFolder(int Number, string Path);
    private sealed record FileSignature(long Length, long LastWriteTicks);
    private sealed record CachedFile(FileSignature Signature, ParsedFile Parsed);
}
