using System.Text;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.Tests;

namespace RainWorldCompanion.Core.Tests;

public class LogStreamGeneratedSenderTests
{
    private const ulong SenderId = 76561198000000001;

    [Fact]
    public void Generated_diagnostics_are_chunked_without_becoming_source_files()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        byte[] record = Enumerable.Range(0, 9_000).Select(index => (byte)(index % 251)).ToArray();
        var sender = CreateSender(install);
        sender.AddReceiver("receiver");

        Assert.True(sender.TryAppendGenerated("Companion/events.jsonl", record));

        var chunks = sender.GetPendingChunks("receiver");
        Assert.Equal([5_120, 3_880], chunks.Select(chunk => chunk.Length));
        Assert.Equal([0L, 5_120L], chunks.Select(chunk => chunk.Offset));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("Companion/events.jsonl", chunk.FileId);
            Assert.Equal(1, chunk.Generation);
            Assert.True(chunk.HasValidHash());
        });
        Assert.Equal(record, chunks.SelectMany(chunk => chunk.CopyData()).ToArray());
        Assert.Throws<ArgumentException>(() => LogStreamFileCatalog.ResolveSourcePath(
            install, "Companion/events.jsonl"));
    }

    [Fact]
    public void Default_chunks_fit_the_network_packet_envelope()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install);
        sender.AddReceiver("receiver");
        Assert.True(sender.TryAppendGenerated("Companion/events.jsonl",
            Enumerable.Repeat(byte.MaxValue, LogStreamSenderOptions.DefaultChunkSize).ToArray()));
        LogStreamChunk chunk = Assert.Single(sender.GetPendingChunks("receiver"));
        var message = new LogStreamNetworkMessage
        {
            Kind = LogStreamKinds.Chunk,
            LobbyId = new string('l', 64),
            SenderSteamId = SenderId.ToString(),
            ReceiverSteamId = new string('2', 32),
            CaptureId = new string('c', 96),
            CaptureToken = new string('c', 192),
            TransferId = new string('t', 96),
            ConsentToken = new string('t', 192),
            LogSessionId = new string('s', 128),
            FileId = chunk.FileId,
            Generation = int.MaxValue,
            Offset = long.MaxValue,
            Sequence = long.MaxValue,
            Data = chunk.CopyData(),
            Hash = chunk.Sha256
        };
        byte[] payload = Encoding.UTF8.GetBytes(LiveJson.Serialize(message));

        Assert.True(payload.Length <= ProtocolInfo.MaximumLogPacketLength,
            $"A default chunk serialized to {payload.Length:N0} bytes.");
    }

    [Fact]
    public void Receiver_joining_a_trace_channel_starts_at_the_current_tail()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install);
        Assert.True(sender.TryAppendGenerated("Companion/deep-trace.jsonl", "before\n"u8));

        sender.AddReceiver("receiver", startAtCurrentEnd: true);

        Assert.Empty(sender.GetPendingChunks("receiver"));
        Assert.True(sender.TryAppendGenerated("Companion/deep-trace.jsonl", "after\n"u8));
        var chunk = Assert.Single(sender.GetPendingChunks("receiver"));
        Assert.Equal("after\n"u8.ToArray(), chunk.CopyData());
        Assert.Equal(LogStreamAcknowledgeStatus.Accepted, sender.Acknowledge(
            "receiver",
            new(chunk.CaptureId, chunk.SourceSessionId, chunk.Sequence, chunk.Sha256)).Status);
        var receiver = Assert.Single(sender.GetSnapshot().Receivers);
        Assert.Equal("after\n"u8.Length, receiver.BytesAcknowledged);
        Assert.Equal(0, receiver.BacklogBytes);
    }

    [Fact]
    public void A_generated_record_is_never_partially_spooled()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install, maxSpoolBytes: 8);
        sender.AddReceiver("receiver");

        Assert.False(sender.TryAppendGenerated("Companion/events.jsonl", new byte[9]));

        Assert.Empty(sender.GetPendingChunks("receiver"));
        Assert.Equal(0, sender.GetSnapshot().SpoolBytes);
        Assert.True(sender.GetSnapshot().IsSpoolFull);
        Assert.Contains(sender.DrainEvents(), item => item.Kind == LogStreamEventKind.SpoolFull);
    }

    [Fact]
    public void Trace_only_sender_does_not_read_the_raw_game_logs()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        files.WriteText("Rain World/consoleLog.txt", "private raw log");
        var sender = CreateSender(install, pollSourceLogs: false);
        sender.AddReceiver("receiver");

        Assert.Equal(0, sender.Poll().BytesAdded);
        Assert.Empty(sender.GetPendingChunks("receiver"));
    }

    [Fact]
    public void Trace_sender_compacts_acknowledged_chunks_across_repeated_spool_capacities()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install, maxSpoolBytes: 10_240, compactAcknowledgedChunks: true);
        Assert.True(sender.AddReceiver("receiver"));

        for (int batch = 0; batch < 5; batch++)
        {
            Assert.True(sender.TryAppendGenerated("Companion/deep-trace.jsonl", new byte[10_240]));
            LogStreamChunk[] chunks = sender.GetPendingChunks("receiver").ToArray();
            Assert.Equal([batch * 2L + 1, batch * 2L + 2], chunks.Select(chunk => chunk.Sequence));

            foreach (LogStreamChunk chunk in chunks)
            {
                Assert.Equal(LogStreamAcknowledgeStatus.Accepted, sender.Acknowledge(
                    "receiver",
                    new(chunk.CaptureId, chunk.SourceSessionId, chunk.Sequence, chunk.Sha256)).Status);
            }

            LogStreamSenderSnapshot snapshot = sender.GetSnapshot();
            Assert.Equal(0, snapshot.SpoolBytes);
            Assert.Equal(0, snapshot.ChunkCount);
            Assert.False(snapshot.IsSpoolFull);
            Assert.Equal((batch + 1) * 10_240L, Assert.Single(snapshot.Receivers).BytesAcknowledged);
        }
    }

    [Fact]
    public void Ordinary_generated_sender_keeps_acknowledged_history_by_default()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install, maxSpoolBytes: 10_240);
        sender.AddReceiver("receiver");
        Assert.True(sender.TryAppendGenerated("Companion/events.jsonl", new byte[5_120]));
        LogStreamChunk chunk = Assert.Single(sender.GetPendingChunks("receiver"));

        Assert.Equal(LogStreamAcknowledgeStatus.Accepted, sender.Acknowledge(
            "receiver",
            new(chunk.CaptureId, chunk.SourceSessionId, chunk.Sequence, chunk.Sha256)).Status);

        Assert.Equal(5_120, sender.GetSnapshot().SpoolBytes);
        Assert.Equal(1, sender.GetSnapshot().ChunkCount);
        Assert.True(sender.AddReceiver("later"));
        Assert.Equal(chunk.Sequence, Assert.Single(sender.GetPendingChunks("later")).Sequence);
    }

    [Fact]
    public void Compacting_sender_accepts_one_receiver()
    {
        using var files = new TempDirectory("log-stream-generated");
        string install = files.CreateSubdirectory("Rain World");
        var sender = CreateSender(install, compactAcknowledgedChunks: true);

        Assert.True(sender.AddReceiver("receiver"));
        Assert.False(sender.AddReceiver("other"));
        Assert.True(sender.RemoveReceiver("receiver"));
        Assert.False(sender.AddReceiver("replacement"));
    }

    private static LogStreamSenderSession CreateSender(
        string install,
        long maxSpoolBytes = LogStreamSenderOptions.DefaultMaxSpoolBytes,
        bool pollSourceLogs = false,
        bool compactAcknowledgedChunks = false)
        => new(
            install,
            "capture-generated",
            "game-session-generated",
            SenderId,
            new LogStreamSenderOptions
            {
                MaxSpoolBytes = maxSpoolBytes,
                PollSourceLogs = pollSourceLogs,
                CompactAcknowledgedChunks = compactAcknowledgedChunks,
                TimeProvider = new FixedClock(DateTimeOffset.UnixEpoch),
            });
}
