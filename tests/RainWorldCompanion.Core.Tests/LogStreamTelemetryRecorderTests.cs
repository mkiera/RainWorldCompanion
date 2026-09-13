using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Tests;

public class LogStreamTelemetryRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void First_snapshot_records_session_context_and_each_present_player()
    {
        var recorder = new LogStreamTelemetryRecorder("1.4.0-test");
        var records = recorder.Observe(Snapshot(), Now).Select(Parse).ToArray();

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

        var kinds = recorder.Observe(changed, Now.AddSeconds(1)).Select(Parse).Select(Kind).ToHashSet();

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

        Assert.Equal("connection-lost", Kind(Assert.Single(recorder.Observe(null, Now.AddSeconds(1)).Select(Parse))));
        Assert.Empty(recorder.Observe(null, Now.AddSeconds(2)));
        Assert.Contains(recorder.Observe(Snapshot(), Now.AddSeconds(3)).Select(Parse),
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

        using var record = Parse(recorder.DeepTrace(snapshot, Now));
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
            Now.AddSeconds(1), "completed", "teleport", "meadow:2", "SU_A03", "SU", true, "Done."));
        var root = record.RootElement;
        var details = root.GetProperty("details");

        Assert.Equal("companion-action-completed", root.GetProperty("kind").GetString());
        Assert.Equal("teleport", details.GetProperty("action").GetString());
        Assert.Equal("SU_A03", details.GetProperty("roomId").GetString());
        Assert.True(details.GetProperty("success").GetBoolean());
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

    private static JsonDocument Parse(byte[] line)
        => JsonDocument.Parse(Encoding.UTF8.GetString(line).TrimEnd());

    private static string Kind(JsonDocument record)
        => record.RootElement.GetProperty("kind").GetString()!;
}
