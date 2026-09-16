using RainWorldCompanion.Core.Tests;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Tests;

public sealed class LogStreamingCoordinatorTests
{
    [Fact]
    public void Capture_destination_changes_only_between_captures_and_the_next_capture_uses_it()
    {
        using var gameFiles = new TempDirectory("rwc-log-source");
        using var initialDestination = new TempDirectory("rwc-log-initial");
        using var selectedDestination = new TempDirectory("rwc-log-selected");
        using var laterDestination = new TempDirectory("rwc-log-later");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(gameFiles.Path, initialDestination.Path, clock);
        var receiver = Coordinator(gameFiles.Path, initialDestination.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();

        receiver.SetReceiverAvailability(true);
        receiver.SetDestinationRoot(selectedDestination.Path);
        Assert.Equal(Path.GetFullPath(selectedDestination.Path), receiver.Snapshot().DestinationRoot);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        string firstCapture = receiver.Snapshot().CaptureFolder;
        Assert.StartsWith(Path.GetFullPath(selectedDestination.Path), firstCapture, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => receiver.SetDestinationRoot(laterDestination.Path));

        receiver.SetCaptureMode(LogStreamingCaptureMode.Paused);
        Assert.Throws<InvalidOperationException>(() => receiver.SetDestinationRoot(laterDestination.Path));
        Assert.Equal(firstCapture, receiver.Snapshot().CaptureFolder);

        receiver.SetCaptureMode(LogStreamingCaptureMode.Stopped);
        receiver.SetDestinationRoot(laterDestination.Path);
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        Assert.StartsWith(Path.GetFullPath(laterDestination.Path), receiver.Snapshot().CaptureFolder,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_consent_streams_existing_and_appended_bytes_then_acknowledges_them()
    {
        using var senderFiles = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        senderFiles.WriteText("consoleLog.txt", "before\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock);
        var receiver = Coordinator(senderFiles.Path, receiverDownloads.Path, clock);
        var session = new Pair(sender, receiver, clock);

        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);

        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 7);
        string capture = receiver.Snapshot().CaptureFolder;
        Assert.Equal("before\n", ReadOnlyLog(capture, "consoleLog.txt"));

        File.AppendAllText(senderFiles.Resolve("consoleLog.txt"), "after\n");
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 13);
        Assert.Equal("before\nafter\n", ReadOnlyLog(capture, "consoleLog.txt"));
        Assert.Contains(receiver.Snapshot().Lines, line => line.Text == "before");
        Assert.Contains(receiver.Snapshot().Lines, line => line.Text == "after");
    }

    [Fact]
    public void Sustained_raw_logs_do_not_make_structured_events_fall_behind()
    {
        using var senderFiles = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        const int originalChunkBytes = 8 * 1024;
        senderFiles.WriteText("consoleLog.txt", new string('s', originalChunkBytes - 1) + "\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock,
            maximumOutgoingBytesPerSecondPerPeer: 64 * 1024,
            maximumIncomingBytesPerSecondPerPeer: 64 * 1024);
        var receiver = Coordinator(senderFiles.Path, receiverDownloads.Path, clock,
            maximumIncomingBytesPerSecondPerPeer: 64 * 1024);
        var session = new Pair(sender, receiver, clock);

        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        string capture = receiver.Snapshot().CaptureFolder;
        sender.RecordCompanionAction("progress", "structured-baseline", null, null, null, true, null);
        session.TickUntil(() => SenderFiles(capture, "events.jsonl", Pair.SenderId)
                                .Any(path => File.ReadAllText(path).Contains(
                                    "structured-baseline", StringComparison.Ordinal))
                            && Outgoing(sender, Pair.ReceiverId) is
                                { BacklogBytes: 0, AcknowledgementAge: null });
        const int rawBatchBytes = 3 * 1024;
        string rawBatch = new string('r', rawBatchBytes - 1) + "\n";

        for (int index = 1; index <= 24; index++)
        {
            File.AppendAllText(senderFiles.Resolve("consoleLog.txt"), rawBatch);
            sender.RecordCompanionAction("progress", $"structured-{index:D2}", null, null, null, true, null);
            session.Tick();
        }

        string raw = Assert.Single(SenderFiles(capture, "consoleLog.txt", Pair.SenderId));
        string events = string.Concat(SenderFiles(capture, "events.jsonl", Pair.SenderId)
            .Select(File.ReadAllText));
        Assert.Contains("structured-24", events, StringComparison.Ordinal);
        Assert.Equal(originalChunkBytes + 24L * rawBatchBytes, new FileInfo(raw).Length);
    }

    [Theory]
    [InlineData(32 * 1024, 250)]
    [InlineData(64 * 1024, 250)]
    [InlineData(32 * 1024, 100)]
    public void Receiver_limits_keep_room_events_current_during_raw_log_streaming(
        long incomingBytesPerSecond, int exchangeMilliseconds)
    {
        using var senderFiles = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        senderFiles.WriteText("consoleLog.txt", new string('s', 16 * 1024));
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-15T16:15:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock);
        var receiver = Coordinator(senderFiles.Path, receiverDownloads.Path, clock,
            maximumIncomingBytesPerSecondPerPeer: incomingBytesPerSecond);
        var session = new Pair(sender, receiver, clock) { ExchangeInterval = TimeSpan.FromMilliseconds(exchangeMilliseconds) };
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        string capture = receiver.Snapshot().CaptureFolder;

        int exchanges = 60_000 / exchangeMilliseconds;
        int exchangesPerSecond = 1_000 / exchangeMilliseconds;
        int rawBatchBytes = 3 * exchangeMilliseconds;
        for (int index = 0; index < exchanges; index++)
        {
            File.AppendAllText(senderFiles.Resolve("consoleLog.txt"), new string('r', rawBatchBytes));
            if (index % exchangesPerSecond == 0)
            {
                var snapshot = GameSnapshot("sender-live", $"SU_A{index / exchangesPerSecond:D2}", index);
                snapshot.Meadow = new()
                {
                    LobbyId = "123456789",
                    ObserverSteamId = Pair.SenderId,
                    Peers =
                    [
                        new() { SteamId = Pair.SenderId, DisplayName = "Sender", IsLocal = true, IsHost = true },
                        new()
                        {
                            SteamId = Pair.ReceiverId, DisplayName = "Receiver", PingMilliseconds = 100,
                            IncomingBytesPerSecond = 10000, OutgoingBytesPerSecond = 10000,
                        },
                    ],
                };
                sender.ObserveLiveSnapshot(snapshot);
            }
            session.Tick();
        }
        session.Tick(3 * exchangesPerSecond);

        string events = string.Concat(SenderFiles(capture, "events.jsonl", Pair.SenderId).Select(File.ReadAllText));
        Assert.Contains("SU_A59", events, StringComparison.Ordinal);
        Assert.Equal(16 * 1024 + (long)exchanges * rawBatchBytes,
            new FileInfo(Assert.Single(SenderFiles(capture, "consoleLog.txt", Pair.SenderId))).Length);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json")));
        Assert.False(metadata.RootElement.GetProperty("hasGaps").GetBoolean());
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    public void Receiver_keeps_every_sender_current_in_large_busy_lobbies(int senderCount, bool deepTrace)
    {
        using var receiverFiles = new TempDirectory("rwc-many-receiver-source");
        using var receiverDownloads = new TempDirectory("rwc-many-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-16T18:00:00Z"));
        var receiver = Coordinator(receiverFiles.Path, receiverDownloads.Path, clock);
        var senderFiles = Enumerable.Range(0, senderCount)
            .Select(index => new TempDirectory("rwc-many-source-" + index)).ToArray();
        try
        {
            const int originalBytes = 8 * 1024;
            const int batchBytes = 750;
            var expectedRaw = Enumerable.Range(0, senderCount).Select(index =>
                new StringBuilder(new string((char)('a' + index), originalBytes))).ToArray();
            for (int index = 0; index < senderCount; index++)
                senderFiles[index].WriteText("consoleLog.txt", new string((char)('a' + index), originalBytes));
            var senders = senderFiles.Select(files => Coordinator(files.Path, files.Path, clock)).ToArray();
            var session = new ManyToOne(senders, receiver, clock);
            session.Tick();
            receiver.SetReceiverAvailability(true);
            receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
            receiver.SetDeepTraceEnabled(deepTrace);
            session.Tick(2);
            foreach (var sender in senders) sender.PrepareSharing([ManyToOne.ReceiverId]);
            session.Tick(40);
            string capture = receiver.Snapshot().CaptureFolder;

            for (int exchange = 0; exchange < 120; exchange++)
            {
                for (int sender = 0; sender < senderCount; sender++)
                {
                    var random = new Random(sender * 1000 + exchange);
                    var lines = new StringBuilder();
                    while (lines.Length < batchBytes)
                        lines.Append($"[Info : Rain Meadow] 18:00:{exchange / 4:D2}.{exchange % 4 * 250:D3} "
                            + $"Player {sender} entered SU_A{exchange / 4:D2}, entity={random.Next():X8}, "
                            + $"state={random.Next():X8}, tick={exchange}, pending={random.Next(5)}\n");
                    string batch = lines.ToString(0, batchBytes);
                    expectedRaw[sender].Append(batch);
                    File.AppendAllText(senderFiles[sender].Resolve("consoleLog.txt"), batch);
                }
                if (exchange % 4 == 0)
                {
                    for (int sender = 0; sender < senderCount; sender++)
                        senders[sender].RecordCompanionAction("progress", $"sender-{sender:D2}-room-{exchange / 4:D2}",
                            null, null, null, true, null);
                }
                session.Tick();
                if (exchange >= 20 && exchange % 20 == 0)
                {
                    for (int sender = 0; sender < senderCount; sender++)
                    {
                        string observed = string.Concat(SenderFiles(capture, "events.jsonl", ManyToOne.SenderId(sender))
                            .Select(File.ReadAllText));
                        Assert.True(observed.Contains($"sender-{sender:D2}-room-{exchange / 4 - 3:D2}", StringComparison.Ordinal),
                            $"Sender {sender}, second {exchange / 4}, deep {deepTrace}: "
                            + $"{Outgoing(senders[sender], ManyToOne.ReceiverId)}, dropped {session.DroppedPackets}, "
                            + $"queue latency {session.MaximumQueueLatency}. Latest events: {observed[^Math.Min(observed.Length, 500)..]}");
                    }
                }
            }
            session.Tick(24);

            for (int sender = 0; sender < senderCount; sender++)
            {
                string steamId = ManyToOne.SenderId(sender);
                string events = string.Concat(SenderFiles(capture, "events.jsonl", steamId).Select(File.ReadAllText));
                Assert.True(events.Contains($"sender-{sender:D2}-room-29", StringComparison.Ordinal),
                    $"Sender {sender}, count {senderCount}: {Outgoing(senders[sender], ManyToOne.ReceiverId)}. Latest events: {events[^Math.Min(events.Length, 500)..]}");
                for (int action = 0; action < 30; action++)
                    Assert.Contains($"sender-{sender:D2}-room-{action:D2}", events, StringComparison.Ordinal);
                string raw = Assert.Single(SenderFiles(capture, "consoleLog.txt", steamId));
                Assert.Equal(originalBytes + 120L * batchBytes, new FileInfo(raw).Length);
                Assert.Equal(expectedRaw[sender].ToString(), File.ReadAllText(raw));
                if (deepTrace)
                {
                    string trace = string.Concat(SenderFiles(capture, "deep-trace.jsonl", steamId).Select(File.ReadAllText));
                    using var latest = JsonDocument.Parse(trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
                    Assert.InRange((clock.GetUtcNow() - latest.RootElement.GetProperty("timestampUtc").GetDateTimeOffset())
                        .TotalSeconds, 0, 10);
                    Assert.Contains("deep-trace-paced", events, StringComparison.Ordinal);
                }
            }
            Assert.Equal(0, session.DroppedPackets);
            Assert.InRange(session.MaximumQueueLatency.TotalMilliseconds, 0, 1000);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json")));
            Assert.False(metadata.RootElement.GetProperty("hasGaps").GetBoolean());
        }
        finally
        {
            foreach (var files in senderFiles) files.Dispose();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Automatic_trace_targets_affected_sender_and_host_then_expires_without_changing_manual_mode(
        bool receiverAffected, bool hostSharing)
    {
        using var source = new TempDirectory("auto-trace-source");
        using var first = new TempDirectory("auto-trace-first");
        using var second = new TempDirectory("auto-trace-second");
        using var third = new TempDirectory("auto-trace-third");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-16T12:00:00Z"));
        var affected = Coordinator(source.Path, first.Path, clock);
        var receiver = Coordinator(source.Path, second.Path, clock);
        var host = Coordinator(source.Path, third.Path, clock);
        var session = new ThreeParty(affected, receiver, host, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        affected.PrepareSharing([ThreeParty.FastId]);
        if (hostSharing) host.PrepareSharing([ThreeParty.FastId]);
        session.Tick(8);
        for (int secondIndex = 0; secondIndex < 32; secondIndex++)
        {
            var snapshot = AutomaticTraceDetectorTests.Sample(secondIndex, secondIndex >= 20);
            affected.ObserveLiveSnapshot(receiverAffected ? AutomaticTraceDetectorTests.Sample(secondIndex) : snapshot);
            host.ObserveLiveSnapshot(AutomaticTraceDetectorTests.Sample(secondIndex));
            receiver.ObserveLiveSnapshot(receiverAffected ? snapshot : AutomaticTraceDetectorTests.Sample(secondIndex));
            session.Tick(4);
        }
        string capture = receiver.Snapshot().CaptureFolder;
        Assert.False(receiver.Snapshot().DeepTraceEnabled);
        Assert.Contains(receiverAffected ? "Fast" : "Sender", receiver.Snapshot().AutomaticDeepTraceStatus);
        if (hostSharing)
        {
            Assert.Contains("Slow", receiver.Snapshot().AutomaticDeepTraceStatus);
            Assert.NotEmpty(SenderFiles(capture, "deep-trace.jsonl", ThreeParty.SlowId));
        }
        else Assert.Empty(SenderFiles(capture, "deep-trace.jsonl", ThreeParty.SlowId));
        Assert.NotEmpty(SenderFiles(capture, "deep-trace.jsonl", receiverAffected ? ThreeParty.FastId : ThreeParty.SenderId));
        Assert.Empty(SenderFiles(capture, "deep-trace.jsonl", receiverAffected ? ThreeParty.SenderId : ThreeParty.FastId));
        session.Tick(260);
        Assert.DoesNotContain("active:", receiver.Snapshot().AutomaticDeepTraceStatus);
        string journal = File.ReadAllText(Path.Combine(capture, "events.jsonl"));
        Assert.Contains("deepTraceStarted", journal);
        Assert.Contains("deepTraceStopped", journal);
        Assert.Contains("60-second limit", journal);
        var analysis = new RainWorldCompanion.Core.LogStreaming.Analysis.LogCaptureAnalysisSession(capture);
        var loaded = await analysis.RefreshAsync();
        Assert.Contains(loaded.Moments, moment => moment.Kind == "deepTraceStarted"
            && moment.Severity == RainWorldCompanion.Core.LogStreaming.Analysis.CaptureEventSeverity.Warning);
        Assert.NotEmpty(loaded.PerformanceSamples);
        Assert.DoesNotContain(loaded.Moments, moment => moment.Kind == "performance-sample");
        receiver.SetDeepTraceEnabled(true);
        session.Tick(4);
        Assert.True(receiver.Snapshot().DeepTraceEnabled);
        receiver.SetAutomaticDeepTraceEnabled(false);
        Assert.True(receiver.Snapshot().DeepTraceEnabled);
    }

    [Fact]
    public void Deep_trace_can_toggle_after_one_approval_while_normal_logs_and_events_continue()
    {
        using var senderFiles = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        senderFiles.WriteText("consoleLog.txt", "before\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock);
        var receiver = Coordinator(senderFiles.Path, receiverDownloads.Path, clock);
        var session = new Pair(sender, receiver, clock);

        session.Tick();
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A01", 1));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        string capture = receiver.Snapshot().CaptureFolder;
        session.TickUntil(() => SenderFiles(capture, "consoleLog.txt", Pair.SenderId).Length == 1
                                && SenderFiles(capture, "events.jsonl", Pair.SenderId).Length == 1);
        Assert.Empty(SenderFiles(capture, "deep-trace.jsonl", Pair.SenderId));

        receiver.SetDeepTraceEnabled(true);
        session.TickUntil(() => sender.Snapshot().Peers.Single().ReceiverDeepTraceEnabled);
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A02", 2));
        session.TickUntil(() => SenderFiles(capture, "deep-trace.jsonl", Pair.SenderId).Length == 1);

        string firstTrace = Assert.Single(SenderFiles(capture, "deep-trace.jsonl", Pair.SenderId));
        long firstTraceLength = new FileInfo(firstTrace).Length;
        receiver.SetDeepTraceEnabled(false);
        session.TickUntil(() => !sender.Snapshot().Peers.Single().ReceiverDeepTraceEnabled);
        File.AppendAllText(senderFiles.Resolve("consoleLog.txt"), "while off\n");
        sender.RecordCompanionAction("completed", "teleport", "player-1", "SU_A03", "SU", true,
            "event while trace is off");
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A03", 3));
        session.TickUntil(() => File.ReadAllText(Assert.Single(
                                    SenderFiles(capture, "consoleLog.txt", Pair.SenderId))) == "before\nwhile off\n"
                                && File.ReadAllText(Assert.Single(
                                    SenderFiles(capture, "events.jsonl", Pair.SenderId)))
                                    .Contains("event while trace is off", StringComparison.Ordinal));
        session.Tick(4);
        Assert.Equal(firstTraceLength, new FileInfo(firstTrace).Length);
        Assert.Single(SenderFiles(capture, "deep-trace.jsonl", Pair.SenderId));

        receiver.SetDeepTraceEnabled(true);
        session.TickUntil(() => sender.Snapshot().Peers.Single().ReceiverDeepTraceEnabled);
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A04", 4));
        session.TickUntil(() => new FileInfo(firstTrace).Length > firstTraceLength);

        string[] traceFiles = SenderFiles(capture, "deep-trace.jsonl", Pair.SenderId);
        Assert.Single(traceFiles);
        Assert.All(traceFiles, path =>
        {
            string[] lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
            Assert.NotEmpty(lines);
            Assert.All(lines, line =>
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("deep-trace", document.RootElement.GetProperty("kind").GetString());
            });
        });
        string[] traceSessionIds = traceFiles.Select(path =>
        {
            string sessionDirectory = Directory.GetParent(path)!.Parent!.Parent!.FullName;
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDirectory, "session.json")));
            Assert.False(metadata.RootElement.GetProperty("hasGaps").GetBoolean());
            return metadata.RootElement.GetProperty("sourceSessionId").GetString()!;
        }).ToArray();
        Assert.Single(traceSessionIds.Distinct(StringComparer.Ordinal));
        string traceText = File.ReadAllText(firstTrace);
        Assert.DoesNotContain("SU_A03", traceText, StringComparison.Ordinal);
        Assert.Contains("SU_A04", traceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_receivers_do_not_allocate_deep_trace_buffers()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var firstDownloads = new TempDirectory("rwc-log-first");
        using var secondDownloads = new TempDirectory("rwc-log-second");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, senderDownloads.Path, clock);
        var first = Coordinator(source.Path, firstDownloads.Path, clock);
        var second = Coordinator(source.Path, secondDownloads.Path, clock);
        var session = new ThreeParty(sender, first, second, clock);

        session.Tick();
        first.SetReceiverAvailability(true);
        first.SetDeepTraceEnabled(true);
        first.SetCaptureMode(LogStreamingCaptureMode.Paused);
        second.SetReceiverAvailability(true);
        second.SetDeepTraceEnabled(true);
        second.SetCaptureMode(LogStreamingCaptureMode.Paused);
        session.Tick(3);
        sender.PrepareSharing([ThreeParty.FastId, ThreeParty.SlowId]);
        session.Tick(12);
        for (int frame = 1; frame <= 20; frame++)
        {
            sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A01", frame));
            session.Tick(2);
        }

        Assert.Equal((0, 0L), sender.DeepTraceResourceUsage());
        Assert.All(sender.Snapshot().Peers, peer =>
            Assert.Equal(LogStreamingPeerMode.ReceiverPaused, peer.Outgoing.State));
    }

    [Fact]
    public void Local_deep_trace_stops_while_remote_deep_trace_continues()
    {
        using var senderFiles = new TempDirectory("rwc-log-sender-source");
        using var receiverFiles = new TempDirectory("rwc-log-receiver-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock);
        var receiver = Coordinator(receiverFiles.Path, receiverDownloads.Path, clock);
        var session = new Pair(sender, receiver, clock);

        session.Tick();
        sender.SetReceiverAvailability(true);
        sender.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        sender.SetDeepTraceEnabled(true);
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        receiver.SetDeepTraceEnabled(true);
        session.Tick(3);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.Tick(3);
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A01", 1));
        session.TickUntil(() =>
            SenderFiles(sender.Snapshot().CaptureFolder, "deep-trace.jsonl", Pair.SenderId).Length == 1
            && SenderFiles(receiver.Snapshot().CaptureFolder, "deep-trace.jsonl", Pair.SenderId).Length == 1);

        string localTrace = Assert.Single(SenderFiles(
            sender.Snapshot().CaptureFolder, "deep-trace.jsonl", Pair.SenderId));
        string remoteTrace = Assert.Single(SenderFiles(
            receiver.Snapshot().CaptureFolder, "deep-trace.jsonl", Pair.SenderId));
        long localLength = new FileInfo(localTrace).Length;
        long remoteLength = new FileInfo(remoteTrace).Length;

        sender.SetDeepTraceEnabled(false);
        session.Tick(3);
        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A02", 2));
        session.TickUntil(() => new FileInfo(remoteTrace).Length > remoteLength);

        Assert.Equal(localLength, new FileInfo(localTrace).Length);
        Assert.Contains("SU_A02", File.ReadAllText(remoteTrace), StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_trace_reaches_only_receivers_that_enable_it()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var traceDownloads = new TempDirectory("rwc-log-trace");
        using var normalDownloads = new TempDirectory("rwc-log-normal");
        source.WriteText("consoleLog.txt", "shared\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, senderDownloads.Path, clock);
        var traceReceiver = Coordinator(source.Path, traceDownloads.Path, clock);
        var normalReceiver = Coordinator(source.Path, normalDownloads.Path, clock);
        var session = new ThreeParty(sender, traceReceiver, normalReceiver, clock);

        session.Tick();
        traceReceiver.SetReceiverAvailability(true);
        traceReceiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        traceReceiver.SetDeepTraceEnabled(true);
        normalReceiver.SetReceiverAvailability(true);
        normalReceiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(3);
        sender.PrepareSharing([ThreeParty.FastId, ThreeParty.SlowId]);
        string traceCapture = traceReceiver.Snapshot().CaptureFolder;
        string normalCapture = normalReceiver.Snapshot().CaptureFolder;
        session.TickUntil(() => SenderFiles(traceCapture, "consoleLog.txt", ThreeParty.SenderId).Length == 1
                                && SenderFiles(normalCapture, "consoleLog.txt", ThreeParty.SenderId).Length == 1
                                && sender.Snapshot().Peers.Single(peer => peer.SteamId == ThreeParty.FastId)
                                    .ReceiverDeepTraceEnabled
                                && !sender.Snapshot().Peers.Single(peer => peer.SteamId == ThreeParty.SlowId)
                                    .ReceiverDeepTraceEnabled);

        sender.ObserveLiveSnapshot(GameSnapshot("sender-live", "SU_A05", 5));
        session.TickUntil(() => SenderFiles(traceCapture, "deep-trace.jsonl", ThreeParty.SenderId).Length == 1);
        session.Tick(8);

        Assert.Single(SenderFiles(traceCapture, "deep-trace.jsonl", ThreeParty.SenderId));
        Assert.Empty(SenderFiles(normalCapture, "deep-trace.jsonl", ThreeParty.SenderId));
    }

    [Fact]
    public void Receiver_captures_its_own_logs_events_mod_fingerprints_and_trace_with_local_identity()
    {
        using var senderFiles = new TempDirectory("rwc-log-sender-source");
        using var receiverFiles = new TempDirectory("rwc-log-receiver-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var receiverDownloads = new TempDirectory("rwc-log-receiver");
        senderFiles.WriteText("consoleLog.txt", "sender only\n");
        receiverFiles.WriteText("consoleLog.txt", "receiver local\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(senderFiles.Path, senderDownloads.Path, clock);
        var receiver = Coordinator(receiverFiles.Path, receiverDownloads.Path, clock);
        var session = new Pair(sender, receiver, clock)
        {
            SenderIsHost = false,
            ReceiverDisplayName = "Local Tester",
            ReceiverIsHost = true,
        };

        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        receiver.SetDeepTraceEnabled(true);
        var receiverSnapshot = GameSnapshot("receiver-live", "SU_A06", 6,
        [
            new()
            {
                Id = "devourment",
                DisplayName = "Devourment",
                Version = "0.1.0",
                CodeFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                FingerprintStatus = "complete",
            }
        ]);
        receiverSnapshot.Players[0].MeadowSteamId = Pair.ReceiverId;
        receiverSnapshot.Players[0].MeadowPeerId = 2;
        receiverSnapshot.Players[0].MeadowAvatarId = "receiver-avatar";
        receiverSnapshot.Players[0].NativeEntityAvailable = true;
        receiverSnapshot.Players[0].NativeLocationAvailability = "available";
        receiverSnapshot.Players =
        [
            receiverSnapshot.Players[0],
            new()
            {
                Id = "meadow:1:remote-avatar",
                Name = "Nonsharing Player",
                MeadowSteamId = Pair.SenderId,
                MeadowPeerId = 1,
                MeadowAvatarId = "remote-avatar",
                RoomId = "SU_A07",
                Region = "SU",
                IsLocal = false,
                NativeEntityAvailable = true,
                NativeLocationAvailability = "available",
                Trace = new() { Realized = true, PositionX = 9876.5f, Input = new() { Jump = true } },
            }
        ];
        receiverSnapshot.Meadow = new()
        {
            LobbyId = "123456789",
            ObserverSteamId = Pair.ReceiverId,
            GameMode = "Story",
            Timeline = "White",
            Peers =
            [
                new()
                {
                    SteamId = Pair.SenderId, LobbyPeerId = 1, DisplayName = "Nonsharing Player",
                    InGame = true, AvatarCount = 1, AvatarIds = ["remote-avatar"], PingMilliseconds = 45,
                },
                new()
                {
                    SteamId = Pair.ReceiverId, LobbyPeerId = 2, DisplayName = "Local Tester",
                    IsLocal = true, IsHost = true, SupportsGameHookPackets = true,
                    InGame = true, AvatarCount = 1, AvatarIds = ["receiver-avatar"],
                }
            ]
        };
        receiver.ObserveLiveSnapshot(receiverSnapshot);
        string capture = receiver.Snapshot().CaptureFolder;
        session.TickUntil(() => SenderFiles(capture, "consoleLog.txt", Pair.ReceiverId).Length == 1
                                && SenderFiles(capture, "events.jsonl", Pair.ReceiverId).Length == 1
                                && SenderFiles(capture, "meadow-native.jsonl", Pair.ReceiverId).Length == 1
                                && SenderFiles(capture, "deep-trace.jsonl", Pair.ReceiverId).Length == 1
                                && SenderFiles(capture, "meadow-native-deep.jsonl", Pair.ReceiverId).Length == 1);

        string raw = Assert.Single(SenderFiles(capture, "consoleLog.txt", Pair.ReceiverId));
        string events = Assert.Single(SenderFiles(capture, "events.jsonl", Pair.ReceiverId));
        string native = Assert.Single(SenderFiles(capture, "meadow-native.jsonl", Pair.ReceiverId));
        string trace = Assert.Single(SenderFiles(capture, "deep-trace.jsonl", Pair.ReceiverId));
        string nativeDeep = Assert.Single(SenderFiles(capture, "meadow-native-deep.jsonl", Pair.ReceiverId));
        Assert.Equal("receiver local\n", File.ReadAllText(raw));
        Assert.Contains("devourment", File.ReadAllText(events), StringComparison.Ordinal);
        Assert.Contains("0.1.0", File.ReadAllText(events), StringComparison.Ordinal);
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            File.ReadAllText(events), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"deep-trace\"", File.ReadAllText(trace), StringComparison.Ordinal);
        Assert.Contains("Nonsharing Player", File.ReadAllText(native), StringComparison.Ordinal);
        Assert.DoesNotContain(Pair.SenderId, File.ReadAllText(native), StringComparison.Ordinal);
        Assert.DoesNotContain("9876.5", File.ReadAllText(nativeDeep), StringComparison.Ordinal);
        Assert.Empty(SenderFiles(capture, "consoleLog.txt", Pair.SenderId));
        Assert.All(new[] { raw, events, native, trace, nativeDeep }, path =>
        {
            Assert.StartsWith(Path.GetFullPath(capture) + Path.DirectorySeparatorChar, Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries),
                segment => segment == $"Local Tester [Host] {Pair.ReceiverId}");
        });

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json")));
        JsonElement[] localSessions = metadata.RootElement.GetProperty("sessions").EnumerateArray()
            .Where(item => item.GetProperty("senderSteamId").GetString() == Pair.ReceiverId).ToArray();
        Assert.Equal(2, localSessions.Length);
        Assert.All(localSessions, item =>
        {
            Assert.Equal("Local Tester", item.GetProperty("senderSteamName").GetString());
            Assert.Equal("Host", item.GetProperty("initialRole").GetString());
            Assert.Equal("Host", item.GetProperty("currentRole").GetString());
            Assert.False(item.GetProperty("hasGaps").GetBoolean());
        });
    }

    [Fact]
    public void Receiver_stop_invalidates_capture_and_requires_fresh_sender_approval()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes > 0);

        string capture = receiver.Snapshot().CaptureFolder;
        receiver.SetCaptureMode(LogStreamingCaptureMode.Stopped);
        session.Tick(4);
        var outgoing = sender.Snapshot().Peers.Single().Outgoing;
        Assert.Equal(LogStreamingPeerMode.NotSharing, outgoing.State);
        Assert.Contains("Select them again", outgoing.Detail);
        Assert.False(receiver.Snapshot().ReceiverAdvertised);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("completedUtc").ValueKind);
        Assert.Equal("receiverStopped", metadata.RootElement.GetProperty("terminationKind").GetString());
        Assert.Equal("Capture stopped. Existing approvals were cleared.",
            metadata.RootElement.GetProperty("terminationReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, metadata.RootElement.GetProperty("endedUtc").ValueKind);
        var capturedSession = metadata.RootElement.GetProperty("sessions")[0];
        Assert.True(capturedSession.GetProperty("isIncomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, capturedSession.GetProperty("completedUtc").ValueKind);
    }

    [Fact]
    public void Lobby_disconnect_classifies_an_active_capture_as_context_interrupted()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        receiver.Exchange(ReceiverUpstream(1));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        string capture = receiver.Snapshot().CaptureFolder;

        receiver.Exchange(new()
        {
            Sequence = 2,
            GameSessionId = "receiver-game",
            Lobby = new() { IsConnected = false },
        });

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.False(metadata.RootElement.GetProperty("hasGaps").GetBoolean());
        Assert.Equal("contextInterrupted", metadata.RootElement.GetProperty("terminationKind").GetString());
        Assert.Equal("Join a Steam Rain Meadow lobby to stream logs.",
            metadata.RootElement.GetProperty("terminationReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, metadata.RootElement.GetProperty("endedUtc").ValueKind);
    }

    [Fact]
    public void Revoking_the_last_receiver_preserves_the_authenticated_game_session_and_spool()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);
        string firstSession = sender.Snapshot().Peers.Single().Outgoing.LogSession;

        sender.RevokeAllSharing();
        session.Tick(2);
        File.AppendAllText(source.Resolve("consoleLog.txt"), "after\n");
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 12);

        string secondSession = sender.Snapshot().Peers.Single().Outgoing.LogSession;
        Assert.Equal("sender-game", firstSession);
        Assert.Equal(firstSession, secondSession);
        Assert.Equal("entry\nafter\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Authenticated_game_session_change_starts_a_new_sender_spool_identity()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);

        sender.RevokeAllSharing();
        session.Tick(2);
        File.AppendAllText(source.Resolve("consoleLog.txt"), "after\n");
        session.SenderGameSession = "sender-game-next";
        session.Tick();
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 12);

        Assert.Equal("sender-game-next", sender.Snapshot().Peers.Single().Outgoing.LogSession);
        string[] captures = Directory.GetFiles(receiver.Snapshot().CaptureFolder,
            "consoleLog.txt", SearchOption.AllDirectories)
            .Where(IsRemoteSenderPath).ToArray();
        Assert.Equal(2, captures.Length);
        Assert.Contains(captures, path => File.ReadAllText(path) == "entry\n");
        Assert.Contains(captures, path => File.ReadAllText(path) == "entry\nafter\n");
    }

    [Fact]
    public void Brief_advertisement_loss_preserves_approval_and_resumes_without_another_click()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "before\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 7);

        session.ReceiverAdvertisementFresh = false;
        File.AppendAllText(source.Resolve("consoleLog.txt"), "after\n");
        session.Tick(12);
        Assert.Equal(LogStreamingPeerMode.Reconnecting, sender.Snapshot().Peers.Single().Outgoing.State);
        Assert.Equal(7, sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes);

        session.ReceiverAdvertisementFresh = true;
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 13);
        Assert.Equal("before\nafter\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Incoming_approval_expires_when_sender_companion_stops_but_game_hook_stays_present()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock, reconnectGrace: TimeSpan.FromSeconds(10));
        var receiver = Coordinator(source.Path, right.Path, clock, reconnectGrace: TimeSpan.FromSeconds(10));
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);

        sender.Shutdown();
        session.Tick(24);

        Assert.Equal(LogStreamingPeerMode.Reconnecting,
            receiver.Snapshot().Peers.Single().Incoming.State);

        session.Tick(24);

        Assert.Equal(LogStreamingPeerMode.NotSharing,
            receiver.Snapshot().Peers.Single().Incoming.State);
    }

    [Fact]
    public void Unapproved_player_is_forgotten_after_leaving_for_the_reconnect_grace()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock, reconnectGrace: TimeSpan.FromSeconds(1));
        long sequence = 0;

        receiver.Exchange(new()
        {
            Sequence = ++sequence,
            GameSessionId = "receiver-game",
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "123456789",
                LocalSteamId = Pair.ReceiverId,
                Peers =
                [
                    new()
                    {
                        SteamId = Pair.SenderId,
                        DisplayName = "Temporary player",
                        SupportsLogStreaming = true,
                        ProtocolVersion = ProtocolInfo.LogStreamingVersion,
                        LastSeenUtcTicks = 1
                    }
                ]
            }
        });
        Assert.Equal("Temporary player", receiver.Snapshot().Peers.Single().DisplayName);

        clock.Advance(TimeSpan.FromSeconds(2));
        receiver.Exchange(new()
        {
            Sequence = ++sequence,
            GameSessionId = "receiver-game",
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "123456789",
                LocalSteamId = Pair.ReceiverId,
                Peers = []
            }
        });

        Assert.Empty(receiver.Snapshot().Peers);
    }

    [Fact]
    public void Returning_after_reconnect_grace_expires_both_authorizations_before_refreshing_presence()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "before\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock, reconnectGrace: TimeSpan.FromSeconds(1));
        var receiver = Coordinator(source.Path, right.Path, clock, reconnectGrace: TimeSpan.FromSeconds(1));
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 7);

        session.ReceiverAdvertisementFresh = false;
        session.SenderAdvertisementFresh = false;
        session.Tick();
        clock.Advance(TimeSpan.FromSeconds(2));
        session.ReceiverAdvertisementFresh = true;
        session.SenderAdvertisementFresh = true;
        session.Tick();

        Assert.Equal(LogStreamingPeerMode.NotSharing, sender.Snapshot().Peers.Single().Outgoing.State);
        Assert.Equal(LogStreamingPeerMode.NotSharing, receiver.Snapshot().Peers.Single().Incoming.State);
        File.AppendAllText(source.Resolve("consoleLog.txt"), "after\n");
        session.Tick(12);
        Assert.Equal("before\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));

        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 13);
        Assert.Equal("before\nafter\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Expired_incoming_open_cannot_recreate_old_consent_but_a_new_approval_can()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock, reconnectGrace: TimeSpan.FromSeconds(1));
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        var expiredOpen = NetworkMessage(LogStreamKinds.Open, advertisement,
            "transfer-expired", "consent-expired", "sender-game");
        var accepted = receiver.Exchange(ReceiverUpstream(++sequence, expiredOpen));
        Assert.Equal(LogStreamKinds.OpenAccepted, Decode(Assert.Single(accepted.OutgoingPackets)).Kind);

        clock.Advance(TimeSpan.FromSeconds(2));
        var rejected = receiver.Exchange(ReceiverUpstream(++sequence, expiredOpen));

        Assert.DoesNotContain(rejected.OutgoingPackets, packet => Decode(packet).Kind == LogStreamKinds.OpenAccepted);
        Assert.Equal(LogStreamingPeerMode.NotSharing, receiver.Snapshot().Peers.Single().Incoming.State);
        using (var metadata = JsonDocument.Parse(File.ReadAllText(
                   Path.Combine(receiver.Snapshot().CaptureFolder, "capture.json"))))
            Assert.Empty(metadata.RootElement.GetProperty("sessions").EnumerateArray());

        var freshOpen = NetworkMessage(LogStreamKinds.Open, advertisement,
            "transfer-fresh", "consent-fresh", "sender-game");
        var fresh = receiver.Exchange(ReceiverUpstream(++sequence, freshOpen));
        Assert.Contains(fresh.OutgoingPackets, packet => Decode(packet).Kind == LogStreamKinds.OpenAccepted);
    }

    [Fact]
    public void Replacement_open_flood_is_rate_limited_without_creating_capture_sessions_or_folders()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        var initial = NetworkMessage(LogStreamKinds.Open, advertisement,
            "transfer-000", "consent-000", "sender-session-000");
        var initialReply = receiver.Exchange(ReceiverUpstream(++sequence, initial));
        Assert.Equal(LogStreamKinds.OpenAccepted, Decode(Assert.Single(initialReply.OutgoingPackets)).Kind);

        var flood = Enumerable.Range(1, 64).Select(index => NetworkMessage(
            LogStreamKinds.Open,
            advertisement,
            $"transfer-{index:D3}",
            $"consent-{index:D3}",
            $"sender-session-{index:D3}"))
            .ToArray();
        var replies = new List<LogStreamNetworkMessage>();
        foreach (var batch in flood.Chunk(ProtocolInfo.MaximumLogPacketsPerBridgeExchange))
        {
            var reply = receiver.Exchange(ReceiverUpstream(++sequence, batch));
            Assert.InRange(reply.OutgoingPackets.Length, 0, ProtocolInfo.MaximumLogPacketsPerBridgeExchange);
            replies.AddRange(reply.OutgoingPackets.Select(Decode));
        }
        for (int index = 0; index < 4; index++)
            replies.AddRange(receiver.Exchange(ReceiverUpstream(++sequence)).OutgoingPackets.Select(Decode));

        Assert.Equal(7, replies.Count(message => message.Kind == LogStreamKinds.OpenAccepted));
        Assert.Single(replies, message => message.Kind == LogStreamKinds.Error);
        string capture = receiver.Snapshot().CaptureFolder;
        Assert.Empty(Directory.GetFiles(capture, "session.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(capture, "session-*", SearchOption.AllDirectories));
        using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "capture.json"))))
            Assert.Empty(metadata.RootElement.GetProperty("sessions").EnumerateArray());

        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = receiver.Exchange(ReceiverUpstream(++sequence,
            NetworkMessage(LogStreamKinds.Open, advertisement,
                "transfer-recovered", "consent-recovered", "sender-session-recovered")));
        Assert.Contains(recovered.OutgoingPackets, packet => Decode(packet).Kind == LogStreamKinds.OpenAccepted);
    }

    [Fact]
    public void Explicit_reprepare_clears_a_recoverable_receiver_storage_failure()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        long available = 0;
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock,
            availableFreeSpace: _ => available);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.State == LogStreamingPeerMode.StorageLimited);

        available = long.MaxValue;
        sender.PrepareSharing([Pair.ReceiverId]);
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);

        Assert.NotEqual(LogStreamingPeerMode.StorageLimited, receiver.Snapshot().Peers.Single().Incoming.State);
        Assert.Equal("entry\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Incoming_metadata_failure_withholds_ack_until_a_bounded_retry_is_durable()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);

        string metadataPath = Path.Combine(receiver.Snapshot().CaptureFolder, "capture.json");
        using (File.Open(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sender.PrepareSharing([Pair.ReceiverId]);
            session.Tick(12);
            Assert.Equal(0, sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes);
        }

        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);
        Assert.Equal("entry\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Incoming_peer_rate_limit_withholds_excess_chunk_before_writing()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock,
            maximumBytesPerSecond: 64 * 1024, maximumIncomingBytesPerSecondPerPeer: 28 * 1024);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        var open = NetworkMessage(LogStreamKinds.Open, advertisement, "transfer", "consent", "sender-game");
        receiver.Exchange(ReceiverUpstream(++sequence, open));
        byte[] firstData = Enumerable.Repeat((byte)'a', 8 * 1024).ToArray();
        byte[] secondData = Enumerable.Repeat((byte)'b', 8 * 1024).ToArray();
        var first = NetworkMessage(LogStreamKinds.Chunk, advertisement, "transfer", "consent", "sender-game",
            data: firstData, hash: Convert.ToHexString(SHA256.HashData(firstData)), packetSequence: 1);
        var second = NetworkMessage(LogStreamKinds.Chunk, advertisement, "transfer", "consent", "sender-game",
            data: secondData, hash: Convert.ToHexString(SHA256.HashData(secondData)), offset: firstData.Length, packetSequence: 2);

        var limited = receiver.Exchange(ReceiverUpstream(++sequence, first, second));

        Assert.Equal(1, Decode(Assert.Single(limited.OutgoingPackets)).Sequence);
        Assert.Equal(firstData.Length, new FileInfo(Assert.Single(Directory.GetFiles(
            receiver.Snapshot().CaptureFolder, "consoleLog.txt", SearchOption.AllDirectories))).Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        var retried = receiver.Exchange(ReceiverUpstream(++sequence, second));
        Assert.Equal(2, Decode(Assert.Single(retried.OutgoingPackets)).Sequence);
    }

    [Fact]
    public void Incoming_aggregate_rate_limit_applies_across_senders_before_writing()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock,
            maximumIncomingBytesPerSecond: 28 * 1024, maximumIncomingBytesPerSecondPerPeer: 28 * 1024);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        const string otherSender = "76561198000000003";
        var firstOpen = NetworkMessage(LogStreamKinds.Open, advertisement, "transfer-a", "consent-a", "sender-a");
        var secondOpen = NetworkMessage(LogStreamKinds.Open, advertisement, "transfer-b", "consent-b", "sender-b",
            senderId: otherSender);
        receiver.Exchange(ReceiverUpstream(++sequence, firstOpen, secondOpen));
        byte[] firstData = Enumerable.Repeat((byte)'a', 8 * 1024).ToArray();
        byte[] secondData = Enumerable.Repeat((byte)'b', 8 * 1024).ToArray();
        var first = NetworkMessage(LogStreamKinds.Chunk, advertisement, "transfer-a", "consent-a", "sender-a",
            data: firstData, hash: Convert.ToHexString(SHA256.HashData(firstData)), packetSequence: 1);
        var second = NetworkMessage(LogStreamKinds.Chunk, advertisement, "transfer-b", "consent-b", "sender-b",
            data: secondData, hash: Convert.ToHexString(SHA256.HashData(secondData)), packetSequence: 1,
            senderId: otherSender);

        var limited = receiver.Exchange(ReceiverUpstream(++sequence, first, second));

        Assert.Equal(Pair.SenderId, Decode(Assert.Single(limited.OutgoingPackets)).ReceiverSteamId);
        Assert.Single(Directory.GetFiles(receiver.Snapshot().CaptureFolder, "consoleLog.txt", SearchOption.AllDirectories));
        clock.Advance(TimeSpan.FromSeconds(1));
        var retried = receiver.Exchange(ReceiverUpstream(++sequence, second));
        Assert.Equal(otherSender, Decode(Assert.Single(retried.OutgoingPackets)).ReceiverSteamId);
        Assert.Equal(2, Directory.GetFiles(receiver.Snapshot().CaptureFolder,
            "consoleLog.txt", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Incoming_packet_bucket_bounds_tiny_chunk_disk_work()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        const string transfer = "transfer";
        const string consent = "consent";
        const string session = "sender-game";
        receiver.Exchange(ReceiverUpstream(++sequence,
            NetworkMessage(LogStreamKinds.Open, advertisement, transfer, consent, session)));
        var chunks = Enumerable.Range(0, 20).Select(index =>
        {
            byte[] data = [(byte)('a' + index)];
            return NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent, session,
                data: data, hash: Convert.ToHexString(SHA256.HashData(data)),
                offset: index, packetSequence: index + 1);
        }).ToArray();

        receiver.Exchange(ReceiverUpstream(++sequence, chunks));

        string output = Assert.Single(Directory.GetFiles(
            receiver.Snapshot().CaptureFolder, "consoleLog.txt", SearchOption.AllDirectories));
        Assert.InRange(new FileInfo(output).Length, 1, 11);
    }

    [Fact]
    public void Outgoing_ack_age_tracks_oldest_in_flight_send_and_is_absent_when_caught_up()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var left = new TempDirectory("rwc-log-left");
        using var right = new TempDirectory("rwc-log-right");
        source.WriteText("consoleLog.txt", "entry\n");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, left.Path, clock);
        var receiver = Coordinator(source.Path, right.Path, clock);
        var session = new Pair(sender, receiver, clock);
        session.Tick();
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([Pair.ReceiverId]);
        session.Tick(2);
        session.ForwardReceiverPackets = false;
        session.Tick(4);
        TimeSpan firstAge = Assert.IsType<TimeSpan>(
            sender.Snapshot().Peers.Single().Outgoing.AcknowledgementAge);
        session.Tick(8);
        TimeSpan laterAge = Assert.IsType<TimeSpan>(
            sender.Snapshot().Peers.Single().Outgoing.AcknowledgementAge);

        Assert.True(laterAge > firstAge);
        Assert.True(laterAge >= TimeSpan.FromSeconds(3));
        Assert.Null(receiver.Snapshot().Peers.Single().Incoming.AcknowledgementAge);

        session.ForwardReceiverPackets = true;
        session.TickUntil(() => sender.Snapshot().Peers.Single().Outgoing.AcknowledgedBytes == 6);
        Assert.Null(sender.Snapshot().Peers.Single().Outgoing.AcknowledgementAge);
    }

    [Fact]
    public void One_slow_receiver_does_not_block_another_and_catches_up_after_reconnecting()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var fastDownloads = new TempDirectory("rwc-log-fast");
        using var slowDownloads = new TempDirectory("rwc-log-slow");
        string content = string.Join('\n', Enumerable.Range(1, 2_000).Select(index => $"line {index:D4}")) + "\n";
        source.WriteText("consoleLog.txt", content);
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, senderDownloads.Path, clock);
        var fast = Coordinator(source.Path, fastDownloads.Path, clock);
        var slow = Coordinator(source.Path, slowDownloads.Path, clock);
        var session = new ThreeParty(sender, fast, slow, clock);

        session.Tick();
        fast.SetReceiverAvailability(true);
        fast.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        slow.SetReceiverAvailability(true);
        slow.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([ThreeParty.FastId, ThreeParty.SlowId]);
        session.SlowConnected = false;

        session.TickUntil(() => Outgoing(sender, ThreeParty.FastId).AcknowledgedBytes == content.Length);
        Assert.Equal(content, ReadOnlyLog(fast.Snapshot().CaptureFolder, "consoleLog.txt"));
        Assert.Equal(0, Outgoing(sender, ThreeParty.SlowId).AcknowledgedBytes);
        Assert.DoesNotContain(Directory.GetFiles(slow.Snapshot().CaptureFolder,
            "consoleLog.txt", SearchOption.AllDirectories), IsRemoteSenderPath);

        session.SlowConnected = true;
        session.TickUntil(() => Outgoing(sender, ThreeParty.SlowId).AcknowledgedBytes == content.Length);
        Assert.Equal(content, ReadOnlyLog(slow.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Full_bridge_batches_share_capacity_between_receivers()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var senderDownloads = new TempDirectory("rwc-log-sender");
        using var firstDownloads = new TempDirectory("rwc-log-first");
        using var secondDownloads = new TempDirectory("rwc-log-second");
        source.WriteText("consoleLog.txt", new string('x', 12 * LogStreamSenderOptions.DefaultChunkSize));
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var sender = Coordinator(source.Path, senderDownloads.Path, clock,
            maximumBytesPerSecond: 256 * 1024, maximumIncomingBytesPerSecondPerPeer: 256 * 1024);
        var first = Coordinator(source.Path, firstDownloads.Path, clock,
            maximumBytesPerSecond: 256 * 1024, maximumIncomingBytesPerSecondPerPeer: 256 * 1024);
        var second = Coordinator(source.Path, secondDownloads.Path, clock,
            maximumBytesPerSecond: 256 * 1024, maximumIncomingBytesPerSecondPerPeer: 256 * 1024);
        var session = new ThreeParty(sender, first, second, clock);

        session.Tick();
        first.SetReceiverAvailability(true);
        first.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        second.SetReceiverAvailability(true);
        second.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        session.Tick(2);
        sender.PrepareSharing([ThreeParty.FastId, ThreeParty.SlowId]);
        session.Tick(4);

        Assert.True(first.Snapshot().Peers.Single(peer => peer.SteamId == ThreeParty.SenderId)
            .Incoming.AcknowledgedBytes > 0);
        Assert.True(second.Snapshot().Peers.Single(peer => peer.SteamId == ThreeParty.SenderId)
            .Incoming.AcknowledgedBytes > 0);
    }

    [Fact]
    public void Wrong_lobby_capture_or_consent_token_is_rejected_without_writing_or_acknowledging()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        const string transferId = "transfer-current";
        const string consentToken = "consent-current";
        const string logSession = "sender-session";
        var open = NetworkMessage(LogStreamKinds.Open, advertisement, transferId, consentToken, logSession);

        var accepted = receiver.Exchange(ReceiverUpstream(++sequence, open));
        Assert.Equal(LogStreamKinds.OpenAccepted, Decode(Assert.Single(accepted.OutgoingPackets)).Kind);

        byte[] payload = Encoding.UTF8.GetBytes("safe\n");
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        var corruptPackets = new[]
        {
            NetworkMessage(LogStreamKinds.Chunk, advertisement, transferId, consentToken, logSession,
                lobbyId: "wrong-lobby", data: payload, hash: hash),
            NetworkMessage(LogStreamKinds.Chunk,
                new() { Available = true, CaptureActive = true, CaptureId = "capture-stale", CaptureToken = advertisement.CaptureToken },
                transferId, consentToken, logSession, data: payload, hash: hash),
            NetworkMessage(LogStreamKinds.Chunk, advertisement, transferId, "consent-stale", logSession,
                data: payload, hash: hash),
            NetworkMessage(LogStreamKinds.Chunk, advertisement, transferId, consentToken, "session-stale",
                data: payload, hash: hash)
        };

        foreach (var packet in corruptPackets)
        {
            var rejected = receiver.Exchange(ReceiverUpstream(++sequence, packet));
            Assert.Empty(rejected.OutgoingPackets);
        }
        Assert.Empty(Directory.GetFiles(receiver.Snapshot().CaptureFolder, "consoleLog.txt", SearchOption.AllDirectories));

        var valid = NetworkMessage(LogStreamKinds.Chunk, advertisement, transferId, consentToken, logSession,
            data: payload, hash: hash);
        var reply = receiver.Exchange(ReceiverUpstream(++sequence, valid));
        Assert.Equal(LogStreamKinds.Acknowledgement, Decode(Assert.Single(reply.OutgoingPackets)).Kind);
        Assert.Equal("safe\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Empty_chunks_and_cross_channel_file_ids_are_rejected_before_capture_work()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        long sequence = 0;
        receiver.Exchange(ReceiverUpstream(++sequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++sequence)).Advertisement;
        const string transfer = "transfer-current";
        const string consent = "consent-current";
        const string session = "sender-session";
        var open = NetworkMessage(LogStreamKinds.Open, advertisement, transfer, consent, session);
        Assert.Equal(LogStreamKinds.OpenAccepted, Decode(Assert.Single(
            receiver.Exchange(ReceiverUpstream(++sequence, open)).OutgoingPackets)).Kind);

        byte[] trace = Encoding.UTF8.GetBytes("{\"kind\":\"deep-trace\"}\n");
        var traceInBaseSession = NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent, session,
            data: trace, hash: Convert.ToHexString(SHA256.HashData(trace)),
            fileId: "Companion/deep-trace.jsonl");
        Assert.Empty(receiver.Exchange(ReceiverUpstream(++sequence, traceInBaseSession)).OutgoingPackets);

        receiver.SetDeepTraceEnabled(true);
        byte[] raw = Encoding.UTF8.GetBytes("raw\n");
        var rawInDeepSession = NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent,
            "deep-attacker", data: raw, hash: Convert.ToHexString(SHA256.HashData(raw)));
        Assert.Empty(receiver.Exchange(ReceiverUpstream(++sequence, rawInDeepSession)).OutgoingPackets);

        var empty = NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent, session,
            data: [], hash: Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
        Assert.Empty(receiver.Exchange(ReceiverUpstream(++sequence, empty)).OutgoingPackets);
        Assert.Empty(SenderFiles(receiver.Snapshot().CaptureFolder, "consoleLog.txt", Pair.SenderId));
        Assert.Empty(SenderFiles(receiver.Snapshot().CaptureFolder, "deep-trace.jsonl", Pair.SenderId));

        var valid = NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent, session,
            data: raw, hash: Convert.ToHexString(SHA256.HashData(raw)));
        Assert.Equal(LogStreamKinds.Acknowledgement, Decode(Assert.Single(
            receiver.Exchange(ReceiverUpstream(++sequence, valid)).OutgoingPackets)).Kind);
        Assert.Equal("raw\n", ReadOnlyLog(receiver.Snapshot().CaptureFolder, "consoleLog.txt"));
    }

    [Fact]
    public void Viewer_truncates_long_lines_and_bounds_text_and_decoder_state()
    {
        using var source = new TempDirectory("rwc-log-source");
        using var downloads = new TempDirectory("rwc-log-receiver");
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        var receiver = Coordinator(source.Path, downloads.Path, clock);
        long bridgeSequence = 0;
        receiver.Exchange(ReceiverUpstream(++bridgeSequence));
        receiver.SetReceiverAvailability(true);
        receiver.SetCaptureMode(LogStreamingCaptureMode.Capturing);
        var advertisement = receiver.Exchange(ReceiverUpstream(++bridgeSequence)).Advertisement;
        const string transfer = "transfer-current";
        const string consent = "consent-current";
        const string session = "sender-session";
        var open = NetworkMessage(LogStreamKinds.Open, advertisement, transfer, consent, session);
        receiver.Exchange(ReceiverUpstream(++bridgeSequence, open));
        byte[] data = Encoding.UTF8.GetBytes(new string('x', 5_000));
        string hash = Convert.ToHexString(SHA256.HashData(data));

        for (int generation = 1; generation <= 80; generation++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var chunk = NetworkMessage(LogStreamKinds.Chunk, advertisement, transfer, consent, session,
                data: data, hash: hash, packetSequence: generation, generation: generation);
            var reply = receiver.Exchange(ReceiverUpstream(++bridgeSequence, chunk));
            Assert.Equal(LogStreamKinds.Acknowledgement, Decode(Assert.Single(reply.OutgoingPackets)).Kind);
        }

        var snapshot = receiver.Snapshot();
        var resources = receiver.ViewerResourceUsage();
        Assert.NotEmpty(snapshot.Lines);
        Assert.All(snapshot.Lines, line => Assert.InRange(line.Text.Length, 0, 4 * 1024));
        Assert.InRange(resources.Characters, 0, 256 * 1024);
        Assert.InRange(resources.Streams, 0, 16);
    }

    private static LogStreamingCoordinator Coordinator(string install, string downloads, TimeProvider clock,
        TimeSpan? reconnectGrace = null, Func<string, long>? availableFreeSpace = null,
        long maximumBytesPerSecond = 128 * 1024, long maximumIncomingBytesPerSecondPerPeer = 32 * 1024,
        long maximumOutgoingBytesPerSecondPerPeer = 24 * 1024, long maximumIncomingBytesPerSecond = 256 * 1024)
        => new(new()
        {
            GameInstallPath = () => install,
            DestinationRoot = downloads,
            TimeProvider = clock,
            MaximumBytesPerSecond = maximumBytesPerSecond,
            MaximumIncomingBytesPerSecond = maximumIncomingBytesPerSecond,
            MaximumOutgoingBytesPerSecondPerPeer = maximumOutgoingBytesPerSecondPerPeer,
            MaximumIncomingBytesPerSecondPerPeer = maximumIncomingBytesPerSecondPerPeer,
            ReconnectGrace = reconnectGrace ?? TimeSpan.FromMinutes(2),
            SenderOptions = new() { TimeProvider = clock },
            CaptureOptions = new()
            {
                TimeProvider = clock,
                ReservedFreeSpaceBytes = 0,
                AvailableFreeSpace = availableFreeSpace ?? (_ => long.MaxValue)
            }
        });

    private static string ReadOnlyLog(string root, string fileName)
    {
        string[] matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .Where(IsRemoteSenderPath).ToArray();
        Assert.Single(matches);
        return File.ReadAllText(matches[0]);
    }

    private static bool IsRemoteSenderPath(string path)
        => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith("Sender [", StringComparison.Ordinal));

    private static string[] SenderFiles(string root, string fileName, string senderSteamId)
        => Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .Where(path => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.EndsWith(" " + senderSteamId, StringComparison.Ordinal)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static LiveSnapshot GameSnapshot(
        string sessionId,
        string roomId,
        int frame,
        LiveModInfo[]? activeMods = null)
        => new()
        {
            SessionId = sessionId,
            GameplayId = sessionId + "-gameplay",
            State = "gameplay",
            Campaign = "White",
            Timeline = "White",
            GameVersion = "v1.11.8",
            ModVersion = "1.0.11",
            IsOnline = true,
            EnabledExpansions = ["moreslugcats"],
            ActiveMods = activeMods ?? [],
            Trace = new()
            {
                Process = "RainWorldGame",
                Frame = frame,
                UnscaledDeltaSeconds = 1f / 40f,
                TimeScale = 1f,
                ManagedMemoryBytes = 123_456_789,
                Cycle = 7,
                Karma = 4,
                KarmaCap = 5,
                RainTimer = 1_000,
                RainCycleLength = 12_000,
            },
            Players =
            [
                new()
                {
                    Id = "player-1",
                    Name = "Player 1",
                    RoomId = roomId,
                    Region = "SU",
                    IsLocal = true,
                    Dead = false,
                    Trace = new()
                    {
                        Realized = true,
                        PositionX = 320 + frame,
                        PositionY = 240,
                        VelocityX = 1,
                        VelocityY = -1,
                        AbstractX = 12,
                        AbstractY = 8,
                        AbstractNode = 1,
                        AirInLungs = 1,
                        FoodInStomach = 4,
                        Input = new() { X = 1, Jump = true },
                    }
                }
            ]
        };

    private static LogStreamingDirectionSnapshot Outgoing(LogStreamingCoordinator coordinator, string peerId)
        => coordinator.Snapshot().Peers.Single(peer => peer.SteamId == peerId).Outgoing;

    private static LogBridgeUpstream ReceiverUpstream(long sequence, params LogStreamNetworkMessage[] messages) => new()
    {
        Sequence = sequence,
        GameSessionId = "receiver-game",
        Lobby = new()
        {
            IsConnected = true,
            IsSteam = true,
            LobbyId = "123456789",
            LocalSteamId = Pair.ReceiverId,
            Peers =
            messages.Select(message => message.SenderSteamId).DefaultIfEmpty(Pair.SenderId)
                .Distinct(StringComparer.Ordinal).Select(senderId => new LogLobbyPeer
                {
                    SteamId = senderId,
                    DisplayName = senderId == Pair.SenderId ? "Sender" : "Other sender",
                    SupportsLogStreaming = true,
                    ProtocolVersion = ProtocolInfo.LogStreamingVersion,
                    LastSeenUtcTicks = 1
                }).ToArray()
        },
        ReceivedPackets = messages.Select(message => new LogRelayPacket
        {
            PeerSteamId = message.SenderSteamId,
            Payload = Encoding.UTF8.GetBytes(LiveJson.Serialize(message))
        }).ToArray()
    };

    private static LogStreamNetworkMessage NetworkMessage(string kind, LogReceiverAdvertisement advertisement,
        string transferId, string consentToken, string logSession, string lobbyId = "123456789",
        byte[]? data = null, string hash = "", long offset = 0, long packetSequence = 1,
        string senderId = Pair.SenderId, string fileId = "consoleLog.txt", int generation = 1) => new()
    {
        Kind = kind,
        LobbyId = lobbyId,
        SenderSteamId = senderId,
        ReceiverSteamId = Pair.ReceiverId,
        CaptureId = advertisement.CaptureId,
        CaptureToken = advertisement.CaptureToken,
        TransferId = transferId,
        ConsentToken = consentToken,
        LogSessionId = logSession,
        FileId = data is null ? "" : fileId,
        Generation = data is null ? 0 : generation,
        Offset = offset,
        Sequence = data is null ? 0 : packetSequence,
        Data = data ?? [],
        Hash = hash
    };

    private static LogStreamNetworkMessage Decode(LogRelayPacket packet)
        => LiveJson.Deserialize<LogStreamNetworkMessage>(Encoding.UTF8.GetString(packet.Payload));

    private sealed class Pair
    {
        internal const string SenderId = "76561198000000001";
        internal const string ReceiverId = "76561198000000002";
        private readonly LogStreamingCoordinator _sender;
        private readonly LogStreamingCoordinator _receiver;
        private readonly TestClock _clock;
        private LogReceiverAdvertisement _senderAdvertisement = new();
        private LogReceiverAdvertisement _receiverAdvertisement = new();
        private LogRelayPacket[] _toSender = [];
        private LogRelayPacket[] _toReceiver = [];
        private long _senderSequence;
        private long _receiverSequence;
        internal bool ReceiverAdvertisementFresh { get; set; } = true;
        internal bool SenderAdvertisementFresh { get; set; } = true;
        internal bool ForwardReceiverPackets { get; set; } = true;
        internal string SenderGameSession { get; set; } = "sender-game";
        internal string SenderDisplayName { get; set; } = "Sender";
        internal string ReceiverDisplayName { get; set; } = "Receiver";
        internal bool SenderIsHost { get; set; } = true;
        internal bool ReceiverIsHost { get; set; }
        internal TimeSpan ExchangeInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        internal Pair(LogStreamingCoordinator sender, LogStreamingCoordinator receiver, TestClock clock)
        {
            _sender = sender;
            _receiver = receiver;
            _clock = clock;
        }

        internal void Tick(int count = 1)
        {
            for (int index = 0; index < count; index++)
            {
                var senderReply = _sender.Exchange(Upstream(SenderId, SenderDisplayName, SenderIsHost,
                    ReceiverId, ReceiverDisplayName, ReceiverIsHost,
                    _receiverAdvertisement, ForwardReceiverPackets ? _toSender : [], ++_senderSequence,
                    SenderGameSession, ReceiverAdvertisementFresh));
                _senderAdvertisement = senderReply.Advertisement;
                _toReceiver = senderReply.OutgoingPackets;
                var receiverReply = _receiver.Exchange(Upstream(ReceiverId, ReceiverDisplayName, ReceiverIsHost,
                    SenderId, SenderDisplayName, SenderIsHost,
                    _senderAdvertisement, _toReceiver, ++_receiverSequence, "receiver-game", SenderAdvertisementFresh));
                _receiverAdvertisement = receiverReply.Advertisement;
                _toSender = receiverReply.OutgoingPackets;
                _clock.Advance(ExchangeInterval);
            }
        }

        internal void TickUntil(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 100 && !condition(); attempt++) Tick();
            Assert.True(condition(), "Log stream did not reach the expected state.");
        }

        private static LogBridgeUpstream Upstream(string localId, string localName, bool localHost,
            string peerId, string peerName, bool peerHost, LogReceiverAdvertisement peerAdvertisement,
            LogRelayPacket[] packets, long sequence, string gameSession, bool advertisementFresh = true) => new()
        {
            Sequence = sequence,
            GameSessionId = gameSession,
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "123456789",
                LocalSteamId = localId,
                LocalDisplayName = localName,
                LocalIsHost = localHost,
                Peers =
                [
                    new()
                    {
                        SteamId = peerId,
                        DisplayName = peerName,
                        IsHost = peerHost,
                        SupportsLogStreaming = advertisementFresh,
                        ProtocolVersion = advertisementFresh ? ProtocolInfo.LogStreamingVersion : 0,
                        ReceiverAvailable = peerAdvertisement.Available,
                        CaptureActive = peerAdvertisement.CaptureActive,
                        CapturePaused = peerAdvertisement.CapturePaused,
                        DeepTraceEnabled = peerAdvertisement.DeepTraceEnabled || peerAdvertisement.DeepTracePeerIds.Contains(localId),
                        CaptureId = peerAdvertisement.CaptureId,
                        CaptureToken = peerAdvertisement.CaptureToken,
                        LastSeenUtcTicks = advertisementFresh ? 1 : 0
                    }
                ]
            },
            ReceivedPackets = packets.Select(packet => new LogRelayPacket
            {
                PeerSteamId = peerId,
                Payload = packet.Payload
            }).ToArray()
        };
    }

    private sealed class ThreeParty
    {
        internal const string SenderId = Pair.SenderId;
        internal const string FastId = Pair.ReceiverId;
        internal const string SlowId = "76561198000000003";
        private readonly Node[] _nodes;
        private readonly TestClock _clock;

        internal ThreeParty(LogStreamingCoordinator sender, LogStreamingCoordinator fast,
            LogStreamingCoordinator slow, TestClock clock)
        {
            _nodes =
            [
                new(SenderId, "Sender", sender, false, "sender-game"),
                new(FastId, "Fast", fast, false, "fast-game"),
                new(SlowId, "Slow", slow, true, "slow-game")
            ];
            _clock = clock;
        }

        internal bool SlowConnected
        {
            get => _nodes.Single(node => node.Id == SlowId).Connected;
            set => _nodes.Single(node => node.Id == SlowId).Connected = value;
        }

        internal void Tick(int count = 1)
        {
            for (int iteration = 0; iteration < count; iteration++)
            {
                var emitted = new List<(string SenderId, LogRelayPacket Packet)>();
                foreach (var node in _nodes.Where(node => node.Connected))
                {
                    var peers = _nodes.Where(peer => peer != node && peer.Connected).Select(peer => new LogLobbyPeer
                    {
                        SteamId = peer.Id,
                        DisplayName = peer.Name,
                        IsHost = peer.IsHost,
                        SupportsLogStreaming = true,
                        ProtocolVersion = ProtocolInfo.LogStreamingVersion,
                        ReceiverAvailable = peer.Advertisement.Available,
                        CaptureActive = peer.Advertisement.CaptureActive,
                        CapturePaused = peer.Advertisement.CapturePaused,
                        DeepTraceEnabled = peer.Advertisement.DeepTraceEnabled || peer.Advertisement.DeepTracePeerIds.Contains(node.Id),
                        CaptureId = peer.Advertisement.CaptureId,
                        CaptureToken = peer.Advertisement.CaptureToken,
                        LastSeenUtcTicks = 1
                    }).ToArray();
                    var reply = node.Coordinator.Exchange(new()
                    {
                        Sequence = ++node.Sequence,
                        GameSessionId = node.GameSession,
                        Lobby = new()
                        {
                            IsConnected = true,
                            IsSteam = true,
                            LobbyId = "123456789",
                            LocalSteamId = node.Id,
                            LocalDisplayName = node.Name,
                            LocalIsHost = node.IsHost,
                            Peers = peers
                        },
                        ReceivedPackets = node.Inbox.ToArray()
                    });
                    node.Inbox.Clear();
                    node.Advertisement = reply.Advertisement;
                    emitted.AddRange(reply.OutgoingPackets.Select(packet => (node.Id, packet)));
                }
                foreach (var item in emitted)
                {
                    var target = _nodes.SingleOrDefault(node => node.Id == item.Packet.PeerSteamId && node.Connected);
                    if (target is not null)
                        target.Inbox.Add(new() { PeerSteamId = item.SenderId, Payload = item.Packet.Payload });
                }
                _clock.Advance(TimeSpan.FromMilliseconds(250));
            }
        }

        internal void TickUntil(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 200 && !condition(); attempt++) Tick();
            Assert.True(condition(), "Log stream did not reach the expected state.");
        }

        private sealed class Node(string id, string name, LogStreamingCoordinator coordinator,
            bool isHost, string gameSession)
        {
            internal string Id { get; } = id;
            internal string Name { get; } = name;
            internal LogStreamingCoordinator Coordinator { get; } = coordinator;
            internal bool IsHost { get; } = isHost;
            internal string GameSession { get; } = gameSession;
            internal bool Connected { get; set; } = true;
            internal long Sequence { get; set; }
            internal LogReceiverAdvertisement Advertisement { get; set; } = new();
            internal List<LogRelayPacket> Inbox { get; } = [];
        }
    }

    private sealed class ManyToOne
    {
        internal const string ReceiverId = "76561198000999999";
        private readonly Node[] _senders;
        private readonly Node _receiver;
        private readonly TestClock _clock;
        private DateTimeOffset _nextSnapshot;
        private int _frame;
        internal int DroppedPackets { get; private set; }
        internal TimeSpan MaximumQueueLatency { get; private set; }

        internal ManyToOne(LogStreamingCoordinator[] senders, LogStreamingCoordinator receiver, TestClock clock)
        {
            _senders = senders.Select((sender, index) =>
                new Node(SenderId(index), "Sender " + index, sender, index == 0, "sender-" + index)).ToArray();
            _receiver = new(ReceiverId, "Receiver", receiver, false, "receiver");
            _clock = clock;
        }

        internal static string SenderId(int index) => (76561198000100000UL + (ulong)index).ToString();

        internal void Tick(int count = 1)
        {
            DateTimeOffset end = _clock.GetUtcNow() + TimeSpan.FromMilliseconds(count * 250);
            while (_clock.GetUtcNow() < end)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                Node[] nodes = [.. _senders, _receiver];
                if (now >= _nextSnapshot)
                {
                    _frame += 20;
                    foreach (var node in nodes) node.Coordinator.ObserveLiveSnapshot(Snapshot(node, nodes, _frame));
                    _nextSnapshot = now + TimeSpan.FromMilliseconds(500);
                }
                foreach (var node in nodes)
                {
                    if (now < node.NextExchange) continue;
                    var packets = new List<LogRelayPacket>();
                    while (packets.Count < ProtocolInfo.MaximumLogPacketsPerBridgeExchange
                        && node.Inbox.TryDequeue(out var incoming))
                    {
                        TimeSpan latency = now - incoming.Arrived;
                        if (latency > MaximumQueueLatency) MaximumQueueLatency = latency;
                        packets.Add(incoming.Packet);
                    }
                    var reply = node.Coordinator.Exchange(Upstream(node, nodes.Where(peer => peer != node), packets));
                    node.Advertisement = reply.Advertisement;
                    foreach (var packet in reply.OutgoingPackets)
                    {
                        Node recipient = nodes.Single(peer => peer.Id == packet.PeerSteamId);
                        if (recipient.Inbox.Count >= 128) DroppedPackets++;
                        else recipient.Inbox.Enqueue((new() { PeerSteamId = node.Id, Payload = packet.Payload }, now));
                    }
                    node.NextExchange = now + TimeSpan.FromMilliseconds(10
                        + LogBridgePacing.GetDelayMilliseconds(packets.Count, reply.OutgoingPackets.Length, node.Inbox.Count));
                }
                _clock.Advance(TimeSpan.FromMilliseconds(5));
            }
        }

        private static LiveSnapshot Snapshot(Node local, Node[] nodes, int frame)
        {
            var snapshot = GameSnapshot(local.GameSession, $"SU_A{frame / 200:D2}", frame);
            snapshot.IsHost = local.IsHost;
            snapshot.Players = nodes.Select((node, index) => new LivePlayer
            {
                Id = "player-" + index,
                Name = node.Name,
                IsLocal = node == local,
                IsHost = node.IsHost,
                MeadowSteamId = node.Id,
                MeadowPeerId = (ushort)index,
                MeadowAvatarId = "avatar-" + index,
                NativeEntityAvailable = true,
                NativeLocationAvailability = "available",
                RoomId = $"SU_A{frame / 200:D2}",
                Region = "SU",
                Dead = false,
                Trace = new() { Realized = true, PositionX = 100 + frame + index, PositionY = 50, AirInLungs = 1 }
            }).ToArray();
            snapshot.Meadow = new()
            {
                LobbyId = "many-player-lobby",
                ObserverSteamId = local.Id,
                GameMode = "Story",
                Timeline = "White",
                Peers = nodes.Select((node, index) => new LiveMeadowPeer
                {
                    SteamId = node.Id,
                    LobbyPeerId = (ushort)index,
                    DisplayName = node.Name,
                    IsLocal = node == local,
                    IsHost = node.IsHost,
                    SupportsGameHookPackets = true,
                    InGame = true,
                    AvatarCount = 1,
                    AvatarIds = ["avatar-" + index],
                    PingMilliseconds = 60 + index,
                    IncomingBytesPerSecond = 20000 + frame,
                    OutgoingBytesPerSecond = 21000 + frame,
                    RemoteTick = (uint)frame,
                    LatestAcknowledgedTick = (uint)Math.Max(0, frame - 2),
                    OutgoingEventCount = 0,
                    OutgoingStateCount = 0,
                    NeedsAcknowledgement = true,
                    Connection = new()
                    {
                        State = "Connected", PingMilliseconds = 60 + index,
                        LocalDeliveryQuality = 1, RemoteDeliveryQuality = 1,
                        IncomingPacketsPerSecond = 80, OutgoingPacketsPerSecond = 80,
                        IncomingBytesPerSecond = 20000 + frame, OutgoingBytesPerSecond = 21000 + frame,
                        EstimatedSendRateBytesPerSecond = 262144,
                        PendingUnreliableBytes = 0, PendingReliableBytes = 0, UnacknowledgedReliableBytes = 0,
                        QueueTimeMicroseconds = 0
                    }
                }).ToArray()
            };
            return snapshot;
        }

        private static LogBridgeUpstream Upstream(Node local, IEnumerable<Node> peers,
            IEnumerable<LogRelayPacket> packets) => new()
        {
            Sequence = ++local.Sequence,
            GameSessionId = local.GameSession,
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "many-player-lobby",
                LocalSteamId = local.Id,
                LocalDisplayName = local.Name,
                LocalIsHost = local.IsHost,
                Peers = peers.Select(peer => new LogLobbyPeer
                {
                    SteamId = peer.Id,
                    DisplayName = peer.Name,
                    IsHost = peer.IsHost,
                    SupportsLogStreaming = true,
                    ProtocolVersion = ProtocolInfo.LogStreamingVersion,
                    ReceiverAvailable = peer.Advertisement.Available,
                    CaptureActive = peer.Advertisement.CaptureActive,
                    CapturePaused = peer.Advertisement.CapturePaused,
                    DeepTraceEnabled = peer.Advertisement.DeepTraceEnabled
                        || peer.Advertisement.DeepTracePeerIds.Contains(local.Id),
                    CaptureId = peer.Advertisement.CaptureId,
                    CaptureToken = peer.Advertisement.CaptureToken,
                    LastSeenUtcTicks = 1
                }).ToArray()
            },
            ReceivedPackets = packets.ToArray()
        };

        private sealed class Node(string id, string name, LogStreamingCoordinator coordinator,
            bool isHost, string gameSession)
        {
            internal string Id { get; } = id;
            internal string Name { get; } = name;
            internal LogStreamingCoordinator Coordinator { get; } = coordinator;
            internal bool IsHost { get; } = isHost;
            internal string GameSession { get; } = gameSession;
            internal long Sequence { get; set; }
            internal LogReceiverAdvertisement Advertisement { get; set; } = new();
            internal Queue<(LogRelayPacket Packet, DateTimeOffset Arrived)> Inbox { get; } = new();
            internal DateTimeOffset NextExchange { get; set; }
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
