using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Tests;

public class LogStreamTelemetryRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Performance_samples_are_small_throttled_and_can_be_suppressed_when_not_capturing()
    {
        var recorder = new LogStreamTelemetryRecorder("test");
        var snapshot = Snapshot();
        snapshot.Trace!.Performance = new() { Ready = true, Sequence = 1, FrameCount = 60,
            DurationSeconds = 1, MaximumFrameMilliseconds = 25, GarbageCollections = 3 };
        Assert.DoesNotContain(Events(recorder.Observe(snapshot, Now, false)), item => Kind(item) == "performance-sample");
        var sample = Assert.Single(Events(recorder.Observe(snapshot, Now.AddSeconds(1))), item => Kind(item) == "performance-sample");
        Assert.True(Encoding.UTF8.GetByteCount(sample.RootElement.GetRawText()) < 2048);
        Assert.Equal(3, sample.RootElement.GetProperty("details").GetProperty("trace").GetProperty("performance").GetProperty("garbageCollections").GetInt32());
        Assert.DoesNotContain(Events(recorder.Observe(snapshot, Now.AddSeconds(1.2))), item => Kind(item) == "performance-sample");
        Assert.Contains(Events(recorder.Observe(snapshot, Now.AddSeconds(2))), item => Kind(item) == "performance-sample");
    }

    [Fact]
    public void First_snapshot_records_session_context_and_each_present_player()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var records = Events(recorder.Observe(Snapshot(), Now));

        var session = Assert.Single(records, record => Kind(record) == "session-start");
        var details = session.RootElement.GetProperty("details");
        Assert.Equal("1.4.0-test", details.GetProperty("appVersion").GetString());
        Assert.Equal("1.0.11", details.GetProperty("gameHookVersion").GetString());
        Assert.Equal("SU", details.GetProperty("process").GetString());
        Assert.Equal(7, details.GetProperty("cycle").GetInt32());
        var mod = details.GetProperty("activeMods")[0];
        Assert.Equal("devourment", mod.GetProperty("id").GetString());
        Assert.Equal("abc123", mod.GetProperty("codeFingerprint").GetString());
        Assert.Single(records, record => Kind(record) == "player-present");
    }

    [Fact]
    public void Changes_become_searchable_room_lifecycle_lobby_and_action_events()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        recorder.Observe(Snapshot(), Now);
        var changed = Snapshot();
        changed.IsHost = true;
        changed.AllowHostControl = false;
        changed.CommandResult = new() { Id = "command-1", Success = true, Message = "Teleported." };
        changed.HostActionText = "Host moved Player 1.";
        changed.ActiveMods =
        [
            new()
            {
                Id = "devourment",
                DisplayName = "Devourment",
                Version = "1.0.0",
                CodeFingerprint = "different-build",
                FingerprintStatus = "complete"
            }
        ];
        changed.Trace = new()
        {
            Process = "SU",
            Cycle = 8,
            Karma = 4,
            KarmaCap = 5,
            UnscaledDeltaSeconds = 0.3f,
            Frame = 200,
        };
        var changedPlayer = Player();
        changedPlayer.RoomId = "SU_A02";
        changedPlayer.Dead = true;
        changedPlayer.IsHost = true;
        changedPlayer.Trace = new() { Realized = true, InShortcut = true };
        changed.Players = [changedPlayer];

        var kinds = Events(recorder.Observe(changed, Now.AddSeconds(1))).Select(Kind).ToHashSet();

        Assert.Contains("room-changed", kinds);
        Assert.Contains("player-died", kinds);
        Assert.Contains("shortcut-entered", kinds);
        Assert.Contains("player-host-role-changed", kinds);
        Assert.Contains("local-host-role-changed", kinds);
        Assert.Contains("host-control-changed", kinds);
        Assert.Contains("progression-changed", kinds);
        Assert.Contains("companion-action-result", kinds);
        Assert.Contains("rain-meadow-host-action", kinds);
        Assert.Contains("frame-hitch", kinds);
        Assert.Contains("active-mods-changed", kinds);
    }

    [Fact]
    public void A_connection_drop_and_return_are_each_recorded_once()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        recorder.Observe(Snapshot(), Now);

        Assert.Equal("connection-lost", Kind(Assert.Single(Events(recorder.Observe(null, Now.AddSeconds(1))))));
        Assert.Empty(recorder.Observe(null, Now.AddSeconds(2)));
        Assert.Contains(Events(recorder.Observe(Snapshot(), Now.AddSeconds(3))),
            record => Kind(record) == "connection-restored");
    }

    [Fact]
    public void Deep_trace_is_bounded_and_contains_player_position_input_and_performance_state()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var snapshot = Snapshot();
        snapshot.Players = Enumerable.Range(0, 40).Select(index => new LivePlayer
            {
                Id = "meadow:" + index,
                Name = "Player " + index,
                IsLocal = true,
                RoomId = "SU_A01",
                Region = "SU",
                Dead = false,
                Trace = new()
                {
                    Realized = true,
                    PositionX = index + 0.5f,
                    PositionY = index + 1.5f,
                    VelocityX = 2,
                    VelocityY = -3,
                    Input = new() { X = 1, Jump = true }
                }
            }).ToArray();

        using var record = Parse(Assert.Single(recorder.DeepTrace(snapshot, Now),
            item => item.FileId == LogStreamTelemetryRecorder.DeepTraceFileId).Data);
        var root = record.RootElement;

        Assert.Equal("deep-trace", root.GetProperty("kind").GetString());
        Assert.Equal(40, root.GetProperty("playerCount").GetInt32());
        Assert.True(root.GetProperty("playersTruncated").GetBoolean());
        Assert.Equal(32, root.GetProperty("players").GetArrayLength());
        var trace = root.GetProperty("players")[0].GetProperty("trace");
        Assert.Equal(0.5, trace.GetProperty("positionX").GetDouble());
        Assert.True(trace.GetProperty("input").GetProperty("jump").GetBoolean());
        Assert.Equal(0.02, root.GetProperty("trace").GetProperty("unscaledDeltaSeconds").GetDouble(), 3);
    }

    [Fact]
    public void Companion_actions_record_request_context_and_result_without_raw_control_text()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        recorder.Observe(Snapshot(), Now);

        using var record = Parse(recorder.CompanionAction(
            Now.AddSeconds(1), "completed", "teleport", "meadow:2", "SU_A03", "SU", true, "Done.").Data);
        var root = record.RootElement;
        var details = root.GetProperty("details");

        Assert.Equal("companion-action-completed", root.GetProperty("kind").GetString());
        Assert.Equal("teleport", details.GetProperty("action").GetString());
        Assert.Equal("SU_A03", details.GetProperty("roomId").GetString());
        Assert.True(details.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Native_meadow_observations_use_separate_files_and_redact_remote_trace_and_raw_ids()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var snapshot = MeadowSnapshot();

        var normal = recorder.Observe(snapshot, Now);
        string events = Lines(normal, LogStreamTelemetryRecorder.EventFileId);
        string native = Lines(normal, LogStreamTelemetryRecorder.MeadowNativeFileId);
        var deep = recorder.DeepTrace(snapshot, Now.AddSeconds(1));
        string localDeep = Lines(deep, LogStreamTelemetryRecorder.DeepTraceFileId);
        string nativeDeep = Lines(deep, LogStreamTelemetryRecorder.MeadowNativeDeepFileId);

        Assert.Contains("Local One", events, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote Two", events, StringComparison.Ordinal);
        Assert.Contains("Remote Two", native, StringComparison.Ordinal);
        Assert.Contains("network-sample", native, StringComparison.Ordinal);
        Assert.Contains("local-client-observed", native, StringComparison.Ordinal);
        Assert.DoesNotContain("76561198000000001", native, StringComparison.Ordinal);
        Assert.DoesNotContain("76561198000000002", native, StringComparison.Ordinal);
        Assert.DoesNotContain("lobby-secret", native, StringComparison.Ordinal);
        Assert.Contains("111.25", localDeep, StringComparison.Ordinal);
        Assert.DoesNotContain("9876.5", localDeep, StringComparison.Ordinal);
        Assert.DoesNotContain("9876.5", nativeDeep, StringComparison.Ordinal);
        Assert.DoesNotContain("\"jump\":true", nativeDeep, StringComparison.Ordinal);
        Assert.Contains("remote-world", nativeDeep, StringComparison.Ordinal);
    }

    [Fact]
    public void Online_deep_trace_excludes_remote_players_when_native_sampling_is_temporarily_unavailable()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var snapshot = MeadowSnapshot();
        snapshot.Meadow = null;

        string deep = Lines(recorder.DeepTrace(snapshot, Now), LogStreamTelemetryRecorder.DeepTraceFileId);

        Assert.Contains("111.25", deep, StringComparison.Ordinal);
        Assert.DoesNotContain("9876.5", deep, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote Two", deep, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_network_samples_are_throttled_and_preserve_unknown_values()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var snapshot = MeadowSnapshot();
        snapshot.Meadow!.Peers[1].PingMilliseconds = null;
        snapshot.Meadow.Peers[1].IncomingBytesPerSecond = null;

        var first = Native(recorder.Observe(snapshot, Now));
        var early = Native(recorder.Observe(snapshot, Now.AddMilliseconds(200)));
        var next = Native(recorder.Observe(snapshot, Now.AddSeconds(1)));

        Assert.Single(first, record => Kind(record) == "network-sample");
        Assert.DoesNotContain(early, record => Kind(record) == "network-sample");
        using var sample = Assert.Single(next, record => Kind(record) == "network-sample");
        var remote = sample.RootElement.GetProperty("details").GetProperty("peers")[1];
        Assert.False(remote.TryGetProperty("pingMilliseconds", out _));
        Assert.False(remote.TryGetProperty("incomingBytesPerSecond", out _));
    }

    [Fact]
    public void Native_observations_record_remote_room_life_entity_and_story_transitions()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        recorder.Observe(MeadowSnapshot(), Now);
        var changed = MeadowSnapshot();
        changed.Players[1].RoomId = "SU_A07";
        changed.Players[1].Dead = true;
        changed.Players[1].NativeEntityAvailable = true;
        changed.Players[1].NativeLocationAvailability = "available";
        changed.Players[1].InDen = true;
        changed.Players[1].Trace = new()
        {
            Realized = false,
            PositionX = 8765.25f,
            Input = new() { Jump = true }
        };
        changed.Meadow!.Peers[1].StoryReadyForWin = true;
        changed.Meadow.Peers[1].StoryReadyForTransition = false;
        changed.Meadow.Peers[1].StoryDead = true;
        changed.Meadow.ConfigurationTruncated = true;
        changed.Meadow.RosterTruncated = true;

        var records = Native(recorder.Observe(changed, Now.AddSeconds(1)));
        var kinds = records.Select(Kind).ToHashSet(StringComparer.Ordinal);
        string raw = string.Concat(records.Select(record => record.RootElement.GetRawText()));

        Assert.Contains("avatar-location-changed", kinds);
        Assert.Contains("avatar-life-state-changed", kinds);
        Assert.Contains("avatar-entity-state-changed", kinds);
        Assert.Contains("avatar-den-state-changed", kinds);
        Assert.Contains("peer-story-state-changed", kinds);
        Assert.Contains("lobby-settings-changed", kinds);
        Assert.Contains("roster-observation-limit-changed", kinds);
        Assert.Contains("SU_A07", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("8765.25", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"jump\":true", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_native_snapshot_does_not_report_every_peer_as_left()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        recorder.Observe(MeadowSnapshot(), Now);
        var missing = MeadowSnapshot();
        missing.Meadow = null;

        var records = Native(recorder.Observe(missing, Now.AddSeconds(1)));

        Assert.Single(records, record => Kind(record) == "lobby-observation-ended");
        Assert.DoesNotContain(records, record => Kind(record) == "peer-left");
    }

    private static LiveSnapshot Snapshot() => new()
    {
        SessionId = "hook-session",
        GameplayId = "gameplay-session",
        State = "gameplay",
        Campaign = "White",
        Timeline = "White",
        GameVersion = "v1.11.8",
        ModVersion = "1.0.11",
        EnabledExpansions = ["moreslugcats"],
        ActiveMods =
        [
            new()
            {
                Id = "devourment",
                DisplayName = "Devourment",
                Version = "1.0.0",
                CodeFingerprint = "abc123",
                FingerprintStatus = "complete"
            }
        ],
        IsOnline = true,
        IsHost = false,
        AllowHostControl = true,
        Trace = new()
        {
            Process = "SU",
            Frame = 100,
            UnscaledDeltaSeconds = 0.02f,
            TimeScale = 1,
            ManagedMemoryBytes = 123_456,
            Cycle = 7,
            Karma = 3,
            KarmaCap = 5,
            RainTimer = 1_000,
            RainCycleLength = 20_000,
        },
        Players = [Player()]
    };

    private static LivePlayer Player() => new()
    {
        Id = "meadow:1",
        Name = "Player One",
        RoomId = "SU_A01",
        Region = "SU",
        Dead = false,
        IsLocal = true,
        Trace = new() { Realized = true }
    };

    private static LiveSnapshot MeadowSnapshot()
    {
        var snapshot = Snapshot();
        snapshot.Players =
        [
            new()
            {
                Id = "meadow:1:avatar-local", Name = "Local One", IsLocal = true, IsHost = true,
                MeadowSteamId = "76561198000000001", MeadowPeerId = 1, MeadowAvatarId = "avatar-local",
                RoomId = "SU_A01", Region = "SU", Dead = false, NativeEntityAvailable = true,
                NativeLocationAvailability = "available",
                Trace = new() { Realized = true, PositionX = 111.25f, Input = new() { Jump = true } }
            },
            new()
            {
                Id = "meadow:2:avatar-remote", Name = "Remote Two", IsLocal = false,
                MeadowSteamId = "76561198000000002", MeadowPeerId = 2, MeadowAvatarId = "avatar-remote",
                RoomId = "remote-world", Region = "SU", Dead = null, NativeEntityAvailable = false,
                NativeLocationAvailability = "entity-unresolved",
                Trace = new() { Realized = false, PositionX = 9876.5f, Input = new() { Jump = true } }
            }
        ];
        snapshot.Meadow = new()
        {
            LobbyId = "lobby-secret",
            ObserverSteamId = "76561198000000001",
            GameMode = "Story",
            Timeline = "Yellow",
            RequiredMods = ["rainmeadow"],
            Peers =
            [
                new()
                {
                    SteamId = "76561198000000001", LobbyPeerId = 1, DisplayName = "Local One",
                    IsLocal = true, IsHost = true, InGame = true, AvatarCount = 1, AvatarIds = ["avatar-local"]
                },
                new()
                {
                    SteamId = "76561198000000002", LobbyPeerId = 2, DisplayName = "Remote Two",
                    InGame = true, AvatarCount = 1, AvatarIds = ["avatar-remote"],
                    PingMilliseconds = 52, IncomingBytesPerSecond = 1200, OutgoingBytesPerSecond = 800,
                    Connection = new() { State = "Connected", LocalDeliveryQuality = 0.99f, PendingReliableBytes = 12 }
                }
            ]
        };
        return snapshot;
    }

    private static JsonDocument Parse(byte[] line)
        => JsonDocument.Parse(Encoding.UTF8.GetString(line).TrimEnd());

    private static JsonDocument[] Events(IEnumerable<LogStreamTelemetryRecord> records)
        => records.Where(record => record.FileId == LogStreamTelemetryRecorder.EventFileId)
            .Select(record => Parse(record.Data)).ToArray();

    private static JsonDocument[] Native(IEnumerable<LogStreamTelemetryRecord> records)
        => records.Where(record => record.FileId == LogStreamTelemetryRecorder.MeadowNativeFileId)
            .Select(record => Parse(record.Data)).ToArray();

    private static string Lines(IEnumerable<LogStreamTelemetryRecord> records, string fileId)
        => string.Concat(records.Where(record => record.FileId == fileId)
            .Select(record => Encoding.UTF8.GetString(record.Data)));

    private static string Kind(JsonDocument record)
        => record.RootElement.GetProperty("kind").GetString()!;
}
