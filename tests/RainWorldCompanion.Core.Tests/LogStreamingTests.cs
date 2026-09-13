using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.Tests;
using System.Text.Json;

namespace RainWorldCompanion.Core.Tests;

public class LogStreamSenderTests
{
    private const ulong SenderId = 76561198000000001;
    private static readonly TimeProvider Clock = new FixedClock(
        new DateTimeOffset(2026, 9, 13, 12, 34, 56, TimeSpan.Zero));

    [Fact]
    public void Allowlist_is_fixed_to_the_three_log_bundle_files()
    {
        Assert.Equal(
            ["consoleLog.txt", "exceptionLog.txt", "BepInEx/LogOutput.log"],
            LogStreamFileCatalog.Files.Select(file => file.Id));
        Assert.False(LogStreamFileCatalog.TryGet("../sav", out _));
        Assert.False(LogStreamFileCatalog.TryGet("bepinex/logoutput.log", out _));
    }

    [Fact]
    public void Initial_poll_captures_complete_raw_files_then_tails_new_bytes()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        var first = Enumerable.Range(0, 9_000).Select(index => (byte)(index % 251)).ToArray();
        files.WriteBytes("Rain World/consoleLog.txt", first);
        var sender = CreateSender(install);

        var initial = sender.Poll();
        sender.AddReceiver("receiver-1");
        var initialChunks = sender.GetPendingChunks("receiver-1", 8);

        Assert.Equal(9_000, initial.BytesAdded);
        Assert.Equal([8_192, 808], initialChunks.Select(chunk => chunk.Length));
        Assert.Equal([0L, 8_192L], initialChunks.Select(chunk => chunk.Offset));
        Assert.Equal(first, initialChunks.SelectMany(chunk => chunk.CopyData()).ToArray());

        var tail = new byte[] { 0, 255, 13, 10, 0, 128 };
        using (var output = new FileStream(
            Path.Combine(install, "consoleLog.txt"),
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
            output.Write(tail);
        }

        var update = sender.Poll();
        var chunks = sender.GetPendingChunks("receiver-1", 8);

        Assert.Equal(tail.Length, update.BytesAdded);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(9_000, chunks[2].Offset);
        Assert.Equal(tail, chunks[2].CopyData());
        Assert.Equal(1, chunks[2].Generation);
    }

    [Fact]
    public void Receivers_acknowledge_the_same_spool_independently()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        files.WriteBytes("Rain World/consoleLog.txt", new byte[9_000]);
        var sender = CreateSender(install);
        sender.Poll();
        sender.AddReceiver("alice");
        sender.AddReceiver("bob");
        var chunks = sender.GetPendingChunks("alice");

        Assert.Equal(LogStreamAcknowledgeStatus.OutOfOrder,
            sender.Acknowledge("alice", Ack(chunks[1])).Status);
        Assert.Equal(LogStreamAcknowledgeStatus.HashMismatch,
            sender.Acknowledge("alice", Ack(chunks[0]) with { Sha256 = new string('0', 64) }).Status);
        Assert.Equal(LogStreamAcknowledgeStatus.Accepted,
            sender.Acknowledge("alice", Ack(chunks[0])).Status);
        Assert.Equal(LogStreamAcknowledgeStatus.Duplicate,
            sender.Acknowledge("alice", Ack(chunks[0])).Status);

        var snapshot = sender.GetSnapshot();
        var alice = Assert.Single(snapshot.Receivers, receiver => receiver.ReceiverId == "alice");
        var bob = Assert.Single(snapshot.Receivers, receiver => receiver.ReceiverId == "bob");
        Assert.Equal(8_192, alice.BytesAcknowledged);
        Assert.Equal(808, alice.BacklogBytes);
        Assert.Equal(0, bob.BytesAcknowledged);
        Assert.Equal(9_000, bob.BacklogBytes);
        Assert.Equal(2, sender.GetPendingChunks("bob").Count);
        Assert.Single(sender.GetPendingChunks("alice"));
    }

    [Fact]
    public void Truncating_and_rewriting_a_file_starts_a_new_generation_at_zero()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        var path = files.WriteBytes("Rain World/consoleLog.txt", [1, 2, 3, 4, 5, 6]);
        var sender = CreateSender(install);
        sender.Poll();
        sender.AddReceiver("receiver");

        File.WriteAllBytes(path, [9, 8, 7, 6, 5, 4]);
        sender.Poll();
        var chunks = sender.GetPendingChunks("receiver");

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].Generation);
        Assert.Equal(2, chunks[1].Generation);
        Assert.Equal(0, chunks[1].Offset);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5, 4 }, chunks[1].CopyData());
    }

    [Fact]
    public void Deleting_and_recreating_a_file_starts_a_new_generation()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        var path = files.WriteBytes("Rain World/exceptionLog.txt", [1, 2]);
        var sender = CreateSender(install);
        sender.Poll();
        File.Delete(path);

        var missing = sender.Poll();
        File.WriteAllBytes(path, [3, 4]);
        sender.Poll();
        sender.AddReceiver("receiver");
        var chunks = sender.GetPendingChunks("receiver");

        Assert.Contains(missing.Events, item => item.Kind == LogStreamEventKind.FileDisappeared);
        Assert.Equal([1, 2], chunks.Select(chunk => chunk.Generation));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks.SelectMany(chunk => chunk.CopyData()).ToArray());
    }

    [Fact]
    public void Shared_spool_stops_exactly_at_its_configured_limit()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        files.WriteBytes("Rain World/consoleLog.txt", Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        var sender = CreateSender(install, maxSpoolBytes: 10);

        var result = sender.Poll();
        sender.AddReceiver("late-receiver");
        var chunk = Assert.Single(sender.GetPendingChunks("late-receiver"));

        Assert.True(result.IsSpoolFull);
        Assert.Equal(10, result.BytesAdded);
        Assert.Equal(10, chunk.Length);
        Assert.Equal(10, sender.GetSnapshot().SpoolBytes);
        Assert.Contains(result.Events, item => item.Kind == LogStreamEventKind.SpoolFull);
        Assert.Equal(0, sender.Poll().BytesAdded);
    }

    [Fact]
    public void Removing_and_readding_a_receiver_restarts_it_from_the_full_spool()
    {
        using var files = new TempDirectory("log-stream-sender");
        var install = files.CreateSubdirectory("Rain World");
        files.WriteBytes("Rain World/consoleLog.txt", [1, 2, 3]);
        var sender = CreateSender(install);
        sender.Poll();
        sender.AddReceiver("receiver");
        var chunk = Assert.Single(sender.GetPendingChunks("receiver"));
        sender.Acknowledge("receiver", Ack(chunk));

        Assert.True(sender.RemoveReceiver("receiver"));
        Assert.True(sender.AddReceiver("receiver"));

        Assert.Equal(chunk.Sequence, Assert.Single(sender.GetPendingChunks("receiver")).Sequence);
        Assert.Equal(0, Assert.Single(sender.GetSnapshot().Receivers).BytesAcknowledged);
    }

    [Fact]
    public void Chunk_bytes_are_immutable_to_callers()
    {
        var source = new byte[] { 1, 2, 3 };
        var chunk = new LogStreamChunk("capture", "session", SenderId, 1, "consoleLog.txt", 1, 0, source);
        source[0] = 9;
        var copy = chunk.CopyData();
        copy[1] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, chunk.CopyData());
    }

    private static LogStreamSenderSession CreateSender(string install, long? maxSpoolBytes = null)
        => new(
            install,
            "capture-1",
            "game-session-1",
            SenderId,
            new LogStreamSenderOptions
            {
                MaxSpoolBytes = maxSpoolBytes ?? LogStreamSenderOptions.DefaultMaxSpoolBytes,
                TimeProvider = Clock,
            });

    private static LogStreamAcknowledgement Ack(LogStreamChunk chunk)
        => new(chunk.CaptureId, chunk.SourceSessionId, chunk.Sequence, chunk.Sha256);
}

public class LogStreamCaptureWriterTests
{
    private const ulong SenderId = 76561198000000001;
    private static readonly LogStreamPeerIdentity Sender = new(SenderId, "Kiera/CON?", true);
    private static readonly TimeProvider Clock = new FixedClock(
        new DateTimeOffset(2026, 9, 13, 12, 34, 56, TimeSpan.Zero));

    [Fact]
    public void Validated_bytes_are_flushed_under_safe_sender_session_generation_folders()
    {
        using var files = new TempDirectory("log-stream-writer");
        var root = files.CreateSubdirectory("Downloads/Rain World streamed logs");
        var writer = CreateWriter(root);
        var first = Chunk(1, "BepInEx/LogOutput.log", 1, 0, [0, 255, 1]);
        var second = Chunk(2, "BepInEx/LogOutput.log", 1, 3, [2, 3]);

        var firstResult = writer.Write(Sender, first);
        var secondResult = writer.Write(Sender, second);

        Assert.Equal(LogStreamWriteStatus.Written, firstResult.Status);
        Assert.True(firstResult.ShouldAcknowledge);
        Assert.Equal(first.Sequence, firstResult.Acknowledgement!.Sequence);
        Assert.Equal(LogStreamWriteStatus.Written, secondResult.Status);
        Assert.Equal(new byte[] { 0, 255, 1, 2, 3 }, File.ReadAllBytes(secondResult.OutputPath!));
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, writer.CaptureDirectory);
        Assert.Contains("Kiera_CON_ [Host] " + SenderId, secondResult.OutputPath);
        Assert.Contains("session-001 2026-09-13 12-34-56", secondResult.OutputPath);
        Assert.Contains("generation-001", secondResult.OutputPath);
        Assert.EndsWith(Path.Combine("BepInEx", "LogOutput.log"), secondResult.OutputPath);
    }

    [JunctionFact]
    public void Linked_capture_root_is_rejected_before_a_capture_folder_is_created()
    {
        using var target = new TempDirectory("log-stream-target");
        using var parent = new TempDirectory("log-stream-link");
        string link = parent.Resolve("Rain World streamed logs");
        Assert.True(Links.TryCreateDirectoryJunction(link, target.Path));

        Assert.Throws<IOException>(() => CreateWriter(link));

        Assert.Empty(Directory.GetFileSystemEntries(target.Path));
    }

    [Fact]
    public void Sender_name_truncation_preserves_the_utf16_scalar_boundary()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        string prefix = new('a', 59);
        var sender = Sender with { SteamName = prefix + "😀tail" };

        var result = writer.Write(sender, Chunk(1, "consoleLog.txt", 1, 0, [1]));

        Assert.Equal(LogStreamWriteStatus.Written, result.Status);
        string senderFolder = Path.GetFileName(Assert.Single(Directory.GetDirectories(writer.CaptureDirectory)));
        Assert.Equal(prefix + " [Host] " + SenderId, senderFolder);
        Assert.DoesNotContain(senderFolder, char.IsSurrogate);
    }

    [Fact]
    public void Exact_duplicates_are_acknowledged_again_but_conflicts_are_rejected()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var original = Chunk(1, "consoleLog.txt", 1, 0, [1, 2, 3]);
        var written = writer.Write(Sender, original);

        var duplicate = writer.Write(Sender, original);
        var conflict = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [7, 8, 9]));

        Assert.Equal(LogStreamWriteStatus.Duplicate, duplicate.Status);
        Assert.True(duplicate.ShouldAcknowledge);
        Assert.Equal(LogStreamWriteStatus.ConflictingDuplicate, conflict.Status);
        Assert.False(conflict.ShouldAcknowledge);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(written.OutputPath!));
        Assert.Equal(3, writer.GetSnapshot().BytesWritten);
    }

    [Fact]
    public void Sequence_and_offset_gaps_are_rejected_without_writing_or_acknowledging()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));

        var sequenceGap = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 0, [1]));
        var offsetGap = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 4, [1]));

        Assert.Equal(LogStreamWriteStatus.SequenceGap, sequenceGap.Status);
        Assert.Equal(LogStreamWriteStatus.OffsetGap, offsetGap.Status);
        Assert.False(sequenceGap.ShouldAcknowledge);
        Assert.False(offsetGap.ShouldAcknowledge);
        Assert.Equal(0, writer.GetSnapshot().BytesWritten);
    }

    [Fact]
    public void New_file_generations_are_kept_separate_and_begin_at_zero()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var first = writer.Write(Sender, Chunk(1, "exceptionLog.txt", 1, 0, [1, 2]));
        var second = writer.Write(Sender, Chunk(2, "exceptionLog.txt", 2, 0, [3, 4]));

        Assert.Equal(LogStreamWriteStatus.Written, second.Status);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(first.OutputPath!));
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(second.OutputPath!));
        Assert.Contains("generation-002", second.OutputPath);
    }

    [Fact]
    public void Arbitrary_files_wrong_senders_and_bad_hashes_are_rejected()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var arbitrary = new LogStreamChunk("capture-1", "game-session-1", SenderId, 1,
            "../save.txt", 1, 0, new byte[] { 1 });
        var wrongSender = Chunk(1, "consoleLog.txt", 1, 0, [1]);
        var badHash = new LogStreamChunk("capture-1", "game-session-1", SenderId, 1,
            "consoleLog.txt", 1, 0, new byte[] { 1 }, new string('0', 64));

        Assert.Equal(LogStreamWriteStatus.InvalidFile, writer.Write(Sender, arbitrary).Status);
        Assert.Equal(LogStreamWriteStatus.WrongSender,
            writer.Write(Sender with { SteamId = SenderId + 1 }, wrongSender).Status);
        Assert.Equal(LogStreamWriteStatus.HashMismatch, writer.Write(Sender, badHash).Status);
        Assert.DoesNotContain(
            Directory.GetDirectories(writer.CaptureDirectory, "generation-*", SearchOption.AllDirectories),
            _ => true);
    }

    [Fact]
    public void Capture_limit_stops_before_partial_data_is_written()
    {
        using var files = new TempDirectory("log-stream-writer");
        const long maxBytes = 5_000;
        var writer = CreateWriter(files.CreateSubdirectory("captures"), maxBytes: maxBytes);
        var first = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1, 2, 3]));
        long storedBefore = StoredFileBytes(writer.CaptureDirectory);
        int remaining = checked((int)(maxBytes - storedBefore));
        Assert.InRange(remaining, 0, 8_191);
        var blocked = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 3, new byte[remaining + 1]));

        Assert.Equal(LogStreamWriteStatus.CaptureLimitReached, blocked.Status);
        Assert.False(blocked.ShouldAcknowledge);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first.OutputPath!));
        Assert.Equal(3, writer.GetSnapshot().BytesWritten);
        Assert.True(writer.GetSnapshot().IsBlocked);
        Assert.True(StoredFileBytes(writer.CaptureDirectory) <= maxBytes);
    }

    [Fact]
    public void Free_space_guard_stops_before_crossing_the_reserve_and_can_recover()
    {
        using var files = new TempDirectory("log-stream-writer");
        long available = long.MaxValue;
        var writer = CreateWriter(
            files.CreateSubdirectory("captures"),
            reservedBytes: 10,
            availableSpace: _ => available);

        available = 11;
        var blocked = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1, 2]));
        available = long.MaxValue;
        var written = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1, 2]));

        Assert.Equal(LogStreamWriteStatus.InsufficientDiskSpace, blocked.Status);
        Assert.Equal(LogStreamWriteStatus.Written, written.Status);
        Assert.False(writer.GetSnapshot().IsBlocked);
    }

    [Fact]
    public void Rejected_chunk_journal_growth_cannot_cross_the_capture_limit()
    {
        using var baselineFiles = new TempDirectory("log-stream-baseline");
        var baseline = CreateWriter(baselineFiles.CreateSubdirectory("captures"));
        long maxBytes = StoredFileBytes(baseline.CaptureDirectory) + 2_048;
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"), maxBytes: maxBytes);

        for (ulong index = 1; index <= 256; index++)
        {
            var sender = Sender with { SteamId = SenderId + index };
            writer.Write(sender, Chunk(1, "consoleLog.txt", 1, 0, [1]));
        }

        Assert.True(writer.GetSnapshot().IsBlocked);
        Assert.True(StoredFileBytes(writer.CaptureDirectory) <= maxBytes);
        Assert.Equal(0, writer.GetSnapshot().BytesWritten);
    }

    [Fact]
    public void Repeated_identical_rejections_share_one_journal_entry_and_a_bounded_counter()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var badHash = new LogStreamChunk("capture-1", "game-session-1", SenderId, 1,
            "consoleLog.txt", 1, 0, new byte[] { 1 }, new string('0', 64));

        for (int index = 0; index < 2_048; index++)
            Assert.Equal(LogStreamWriteStatus.HashMismatch, writer.Write(Sender, badHash).Status);
        writer.MarkInterrupted("The capture ended after the rejection test.");

        string[] journal = File.ReadAllLines(Path.Combine(writer.CaptureDirectory, "events.jsonl"));
        Assert.Single(journal, line => line.Contains("\"kind\":\"chunkRejected\"", StringComparison.Ordinal));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(writer.CaptureDirectory, "capture.json")));
        Assert.Equal(2_047, metadata.RootElement.GetProperty("suppressedRejectionEvents").GetInt64());
    }

    [Fact]
    public void Metadata_and_journal_writes_honor_the_free_space_reserve()
    {
        using var files = new TempDirectory("log-stream-writer");
        long available = long.MaxValue;
        const long reserve = 1_024;
        var writer = CreateWriter(
            files.CreateSubdirectory("captures"),
            reservedBytes: reserve,
            availableSpace: _ => available);
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        long storedBefore = StoredFileBytes(writer.CaptureDirectory);

        available = reserve;
        writer.ObservePeer(Sender with { SteamName = new string('b', 100), IsHost = false });

        var snapshot = writer.GetSnapshot();
        Assert.False(snapshot.MetadataHealthy);
        Assert.True(snapshot.IsBlocked);
        Assert.Equal(storedBefore, StoredFileBytes(writer.CaptureDirectory));
    }

    [Fact]
    public void Metadata_and_journal_growth_cannot_cross_the_capture_limit()
    {
        using var baselineFiles = new TempDirectory("log-stream-baseline");
        var baseline = CreateWriter(baselineFiles.CreateSubdirectory("captures"), maxBytes: 5_000);
        Assert.True(baseline.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        long maxBytes = StoredFileBytes(baseline.CaptureDirectory);
        Assert.InRange(maxBytes, 1_000, 4_999);
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"), maxBytes: maxBytes);
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        Assert.Equal(maxBytes, StoredFileBytes(writer.CaptureDirectory));

        writer.ObservePeer(Sender with { SteamName = new string('b', 100), IsHost = false });

        var snapshot = writer.GetSnapshot();
        Assert.False(snapshot.MetadataHealthy);
        Assert.True(snapshot.IsBlocked);
        Assert.Equal(maxBytes, StoredFileBytes(writer.CaptureDirectory));
    }

    [Fact]
    public void Senders_and_game_sessions_get_distinct_stable_directories()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var alice = new LogStreamPeerIdentity(SenderId, "Same Name", true);
        var bob = new LogStreamPeerIdentity(SenderId + 1, "Same Name", false);
        var aliceOne = writer.Write(alice, Chunk(1, "consoleLog.txt", 1, 0, [1], senderId: SenderId));
        var aliceTwo = writer.Write(alice, Chunk(1, "consoleLog.txt", 1, 0, [2],
            sourceSession: "game-session-2", senderId: SenderId));
        var bobOne = writer.Write(bob, Chunk(1, "consoleLog.txt", 1, 0, [3], senderId: SenderId + 1));

        Assert.Contains("Same Name [Host] " + SenderId, aliceOne.OutputPath);
        Assert.Contains("session-002", aliceTwo.OutputPath);
        Assert.Contains("Same Name [Client] " + (SenderId + 1), bobOne.OutputPath);
        Assert.Equal(2, writer.GetSnapshot().SenderCount);
        Assert.Equal(3, writer.GetSnapshot().SessionCount);
    }

    [Fact]
    public void A_second_writer_never_reuses_an_existing_capture_directory()
    {
        using var files = new TempDirectory("log-stream-writer");
        var root = files.CreateSubdirectory("captures");
        var first = CreateWriter(root);
        var second = CreateWriter(root);

        Assert.NotEqual(first.CaptureDirectory, second.CaptureDirectory);
        Assert.True(Directory.Exists(first.CaptureDirectory));
        Assert.True(Directory.Exists(second.CaptureDirectory));
    }

    [Fact]
    public void User_markers_and_storage_events_are_returned_as_structured_metadata()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        writer.MarkEvent("Gate stopped opening");
        writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1]));

        var events = writer.DrainEvents();

        Assert.Contains(events, item => item.Kind == LogStreamEventKind.Marker
            && item.Message == "Gate stopped opening");
        Assert.Contains(events, item => item.Kind == LogStreamEventKind.SessionStarted
            && item.SourceSessionId == "game-session-1");
        Assert.Contains(events, item => item.Kind == LogStreamEventKind.ChunkWritten
            && item.FileId == "consoleLog.txt" && item.Offset == 0 && item.ByteCount == 1);
    }

    [Fact]
    public void Capture_and_session_manifests_remain_incomplete_without_authenticated_final_watermarks()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"), lobbyId: "lobby-42");
        writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1, 2, 3]));
        writer.MarkEvent("before gate");
        writer.MarkInterrupted("The capture stopped without final watermarks.");

        Assert.False(writer.GetSnapshot().IsComplete);
        Assert.Equal(LogStreamWriteStatus.CaptureComplete,
            writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 3, [4])).Status);

        using var capture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(writer.CaptureDirectory, "capture.json")));
        var captureRoot = capture.RootElement;
        Assert.Equal("capture-1", captureRoot.GetProperty("captureId").GetString());
        Assert.Equal("lobby-42", captureRoot.GetProperty("lobbyId").GetString());
        Assert.Equal(3, captureRoot.GetProperty("bytesWritten").GetInt64());
        Assert.True(captureRoot.GetProperty("incomplete").GetBoolean());
        Assert.False(captureRoot.GetProperty("hasGaps").GetBoolean());
        Assert.Equal(TimeSpan.Zero,
            captureRoot.GetProperty("createdUtc").GetDateTimeOffset().Offset);
        Assert.Equal(JsonValueKind.Null, captureRoot.GetProperty("completedUtc").ValueKind);

        var sessionPath = Directory.GetFiles(writer.CaptureDirectory, "session.json", SearchOption.AllDirectories).Single();
        using var session = JsonDocument.Parse(File.ReadAllText(sessionPath));
        var sessionRoot = session.RootElement;
        Assert.Equal(SenderId.ToString(), sessionRoot.GetProperty("senderSteamId").GetString());
        Assert.Equal("Kiera/CON?", sessionRoot.GetProperty("senderSteamName").GetString());
        Assert.Equal("Host", sessionRoot.GetProperty("initialRole").GetString());
        Assert.Equal("Host", sessionRoot.GetProperty("currentRole").GetString());
        Assert.Equal("game-session-1", sessionRoot.GetProperty("sourceSessionId").GetString());
        Assert.Equal(3, sessionRoot.GetProperty("bytesWritten").GetInt64());
        Assert.True(sessionRoot.GetProperty("isIncomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sessionRoot.GetProperty("completedUtc").ValueKind);
        var generation = Assert.Single(sessionRoot.GetProperty("generations").EnumerateArray());
        Assert.Equal("consoleLog.txt", generation.GetProperty("fileId").GetString());
        Assert.Equal(1, generation.GetProperty("generation").GetInt32());
        Assert.Equal(3, generation.GetProperty("bytesWritten").GetInt64());

        var eventLines = File.ReadAllLines(Path.Combine(writer.CaptureDirectory, "events.jsonl"));
        Assert.Contains(eventLines, line => line.Contains("\"kind\":\"marker\"", StringComparison.Ordinal)
            && line.Contains("before gate", StringComparison.Ordinal));
        Assert.Contains(eventLines, line => line.Contains("\"kind\":\"captureInterrupted\"", StringComparison.Ordinal));
        Assert.Equal("consoleLog.txt", Assert.Single(writer.GetRecentChunks()).FileId);
    }

    [Fact]
    public void Interrupting_an_unknown_session_does_not_create_capture_state_or_folders()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));

        writer.MarkSessionInterrupted(Sender, "empty-game-session", "The sender closed without final watermarks.");

        var snapshot = writer.GetSnapshot();
        Assert.Empty(snapshot.Sessions);
        Assert.False(snapshot.IsComplete);
        Assert.Empty(Directory.GetFiles(writer.CaptureDirectory, "session.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(writer.CaptureDirectory, "session-*", SearchOption.AllDirectories));
        using var metadata = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(writer.CaptureDirectory, "capture.json")));
        Assert.Empty(metadata.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1],
            sourceSession: "later-game-session")).ShouldAcknowledge);
    }

    [Fact]
    public void Rejected_gaps_remain_visible_as_incomplete_metadata()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 0, [1]));
        writer.MarkInterrupted("The capture stopped without final watermarks.");

        var snapshot = writer.GetSnapshot();
        var session = Assert.Single(snapshot.Sessions);
        Assert.True(snapshot.HasGaps);
        Assert.True(session.HasGaps);
        Assert.True(session.IsIncomplete);
        using var metadata = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(writer.CaptureDirectory, "capture.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("hasGaps").GetBoolean());
    }

    [Fact]
    public void A_transient_out_of_order_chunk_clears_the_gap_but_does_not_prove_finality()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var first = Chunk(1, "consoleLog.txt", 1, 0, [1]);
        var second = Chunk(2, "consoleLog.txt", 1, 1, [2]);

        Assert.Equal(LogStreamWriteStatus.SequenceGap, writer.Write(Sender, second).Status);
        Assert.Equal(LogStreamWriteStatus.Written, writer.Write(Sender, first).Status);
        Assert.Equal(LogStreamWriteStatus.Written, writer.Write(Sender, second).Status);
        writer.MarkInterrupted("The capture stopped without final watermarks.");

        Assert.False(writer.GetSnapshot().HasGaps);
        Assert.True(Assert.Single(writer.GetSnapshot().Sessions).IsIncomplete);
    }

    [Theory]
    [InlineData("capture.json")]
    [InlineData("session.json")]
    [InlineData("events.jsonl")]
    public void Metadata_failure_withholds_acknowledgement_until_the_file_is_current(string failedFile)
    {
        using var files = new TempDirectory("log-stream-writer");
        bool fail = false;
        int failedAttempts = 0;
        var writer = CreateWriter(
            files.CreateSubdirectory("captures"),
            metadataWriteFailure: path =>
            {
                if (!fail || !string.Equals(Path.GetFileName(path), failedFile, StringComparison.Ordinal))
                    return null;
                failedAttempts++;
                return new IOException("Injected metadata failure.");
            });
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        fail = true;

        var failed = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2]));

        Assert.Equal(LogStreamWriteStatus.IoError, failed.Status);
        Assert.False(failed.ShouldAcknowledge);
        Assert.InRange(failedAttempts, 1, 3);
        var unhealthy = writer.GetSnapshot();
        Assert.False(unhealthy.MetadataHealthy);
        Assert.NotNull(unhealthy.MetadataError);
        Assert.InRange(failedAttempts, 2, 6);
        fail = false;

        var recovered = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2]));

        Assert.Equal(LogStreamWriteStatus.Written, recovered.Status);
        Assert.True(recovered.ShouldAcknowledge);
        Assert.True(writer.GetSnapshot().MetadataHealthy);
        Assert.Null(writer.GetSnapshot().MetadataError);
        Assert.Equal(LogStreamWriteStatus.Duplicate,
            writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2])).Status);
        using var capture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(writer.CaptureDirectory, "capture.json")));
        Assert.Equal(2, capture.RootElement.GetProperty("bytesWritten").GetInt64());
        using var session = JsonDocument.Parse(File.ReadAllText(
            Directory.GetFiles(writer.CaptureDirectory, "session.json", SearchOption.AllDirectories).Single()));
        Assert.Equal(2, session.RootElement.GetProperty("bytesWritten").GetInt64());
        Assert.Contains(File.ReadAllLines(Path.Combine(writer.CaptureDirectory, "events.jsonl")),
            line => line.Contains("\"kind\":\"chunkWritten\"", StringComparison.Ordinal)
                && line.Contains("\"offset\":1", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_health_recovers_only_after_every_required_file_is_current()
    {
        using var files = new TempDirectory("log-stream-writer");
        var blocked = new HashSet<string>(StringComparer.Ordinal)
        {
            "capture.json",
            "session.json",
        };
        bool fail = false;
        var writer = CreateWriter(
            files.CreateSubdirectory("captures"),
            metadataWriteFailure: path => fail && blocked.Contains(Path.GetFileName(path))
                ? new IOException("Injected metadata failure.")
                : null);
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        fail = true;
        Assert.False(writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2])).ShouldAcknowledge);

        blocked.Remove("capture.json");
        var partiallyRecovered = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2]));

        Assert.Equal(LogStreamWriteStatus.IoError, partiallyRecovered.Status);
        Assert.False(partiallyRecovered.ShouldAcknowledge);
        Assert.False(writer.GetSnapshot().MetadataHealthy);
        blocked.Clear();

        var recovered = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2]));

        Assert.Equal(LogStreamWriteStatus.Written, recovered.Status);
        Assert.True(recovered.ShouldAcknowledge);
        Assert.True(writer.GetSnapshot().MetadataHealthy);
    }

    [Fact]
    public void A_single_transient_metadata_failure_is_retried_before_acknowledgement()
    {
        using var files = new TempDirectory("log-stream-writer");
        bool injectFailure = false;
        int failuresRemaining = 1;
        var writer = CreateWriter(
            files.CreateSubdirectory("captures"),
            metadataWriteFailure: path =>
            {
                if (!injectFailure || Path.GetFileName(path) != "session.json" || failuresRemaining == 0)
                    return null;
                failuresRemaining--;
                return new IOException("Injected transient metadata failure.");
            });
        Assert.True(writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1])).ShouldAcknowledge);
        injectFailure = true;

        var result = writer.Write(Sender, Chunk(2, "consoleLog.txt", 1, 1, [2]));

        Assert.Equal(LogStreamWriteStatus.Written, result.Status);
        Assert.True(result.ShouldAcknowledge);
        Assert.Equal(0, failuresRemaining);
        Assert.True(writer.GetSnapshot().MetadataHealthy);
        using var session = JsonDocument.Parse(File.ReadAllText(
            Directory.GetFiles(writer.CaptureDirectory, "session.json", SearchOption.AllDirectories).Single()));
        Assert.Equal(2, session.RootElement.GetProperty("bytesWritten").GetInt64());
    }

    [Fact]
    public void Oversized_chunks_are_rejected_before_creating_log_output()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var oversized = Chunk(1, "consoleLog.txt", 1, 0, new byte[8_193]);

        Assert.Equal(LogStreamWriteStatus.InvalidChunk, writer.Write(Sender, oversized).Status);
        Assert.Empty(Directory.GetDirectories(writer.CaptureDirectory, "generation-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Viewer_tail_is_bounded_filterable_and_copies_its_bytes()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1, 2]));
        writer.Write(Sender, Chunk(2, "exceptionLog.txt", 1, 0, [3, 4]));

        var viewed = Assert.Single(writer.GetRecentChunks(SenderId, "exceptionLog.txt"));
        var copy = viewed.CopyData();
        copy[0] = 9;

        Assert.Equal(new byte[] { 3, 4 }, viewed.CopyData());
        Assert.Equal("exceptionLog.txt", viewed.FileId);
        Assert.Equal(SenderId, viewed.Sender.SteamId);
    }

    [Fact]
    public void Host_migration_updates_metadata_without_renaming_the_sender_folder()
    {
        using var files = new TempDirectory("log-stream-writer");
        var writer = CreateWriter(files.CreateSubdirectory("captures"));
        var initial = writer.Write(Sender, Chunk(1, "consoleLog.txt", 1, 0, [1]));
        writer.ObservePeer(Sender with { IsHost = false });

        Assert.Contains("[Host]", initial.OutputPath);
        Assert.Contains("[Host]", Assert.Single(Directory.GetDirectories(writer.CaptureDirectory)));
        var session = Assert.Single(writer.GetSnapshot().Sessions);
        Assert.True(session.InitiallyHost);
        Assert.False(session.IsCurrentlyHost);
        using var metadata = JsonDocument.Parse(File.ReadAllText(
            Directory.GetFiles(writer.CaptureDirectory, "session.json", SearchOption.AllDirectories).Single()));
        Assert.Equal("Host", metadata.RootElement.GetProperty("initialRole").GetString());
        Assert.Equal("Client", metadata.RootElement.GetProperty("currentRole").GetString());
        Assert.Contains(writer.DrainEvents(), item => item.Kind == LogStreamEventKind.PeerRoleChanged);
    }

    private static LogStreamCaptureWriter CreateWriter(
        string root,
        long maxBytes = LogStreamCaptureOptions.DefaultMaxCaptureBytes,
        long reservedBytes = 0,
        Func<string, long>? availableSpace = null,
        string? lobbyId = null,
        Func<string, IOException?>? metadataWriteFailure = null)
        => new(
            root,
            "capture-1",
            new LogStreamCaptureOptions
            {
                MaxCaptureBytes = maxBytes,
                ReservedFreeSpaceBytes = reservedBytes,
                AvailableFreeSpace = availableSpace ?? (_ => long.MaxValue),
                LobbyId = lobbyId,
                TimeProvider = Clock,
                MetadataWriteFailure = metadataWriteFailure,
            });

    private static long StoredFileBytes(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    private static LogStreamChunk Chunk(
        long sequence,
        string file,
        int generation,
        long offset,
        byte[] data,
        string sourceSession = "game-session-1",
        ulong senderId = SenderId)
        => new("capture-1", sourceSession, senderId, sequence, file, generation, offset, data);
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => now;
}
