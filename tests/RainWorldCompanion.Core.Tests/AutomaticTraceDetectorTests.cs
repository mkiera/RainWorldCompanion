using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Tests;

public class AutomaticTraceDetectorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-16T12:00:00Z");

    [Fact]
    public void Sustained_stalls_trigger_after_warmup_but_repeated_snapshots_do_not()
    {
        var detector = new AutomaticTraceDetector();
        for (int second = 0; second < 17; second++)
            Assert.Null(detector.Observe(Sample(second), Start.AddSeconds(second)));
        var slow = Sample(17, true);
        Assert.Null(detector.Observe(slow, Start.AddSeconds(17)));
        for (int repeat = 0; repeat < 20; repeat++)
            Assert.Null(detector.Observe(slow, Start.AddSeconds(17)));
        Assert.Null(detector.Observe(Sample(18, true), Start.AddSeconds(18)));
        Assert.Contains("frame", detector.Observe(Sample(19, true), Start.AddSeconds(19))!);
    }

    [Fact]
    public void Loading_paused_missing_and_stale_data_never_trigger()
    {
        foreach (string state in new[] { "menu", "paused", "gameplay" })
        {
            var detector = new AutomaticTraceDetector();
            for (int i = 0; i < 40; i++)
            {
                var sample = Sample(i, true);
                sample.State = state;
                if (state == "gameplay") sample.Trace!.Performance = null;
                Assert.Null(detector.Observe(sample, Start.AddSeconds(i)));
            }
        }
        var stale = new AutomaticTraceDetector();
        for (int i = 0; i < 40; i++)
            Assert.Null(stale.Observe(Sample(i, true), Start.AddSeconds(i), Start.AddSeconds(i + 60)));
    }

    [Fact]
    public void One_stall_room_changes_and_memory_growth_without_slow_frames_do_not_trigger()
    {
        var detector = new AutomaticTraceDetector();
        for (int i = 0; i < 50; i++)
        {
            var sample = Sample(i, i % 8 == 0);
            sample.Trace!.ManagedMemoryBytes = (1000L + i * 100) * 1024 * 1024;
            Assert.Null(detector.Observe(sample, Start.AddSeconds(i)));
        }
        for (int i = 50; i < 65; i++)
        {
            var sample = Sample(i, true);
            sample.Players[0].RoomId = "SU_A" + i;
            Assert.Null(detector.Observe(sample, Start.AddSeconds(i)));
        }
    }

    [Fact]
    public void Cooldown_prevents_a_persistent_fault_from_retriggering()
    {
        var detector = new AutomaticTraceDetector();
        var reasons = new List<string>();
        for (int i = 0; i < 120; i++)
            if (detector.Observe(Sample(i, true), Start.AddSeconds(i)) is { } reason) reasons.Add(reason);
        Assert.Single(reasons);
    }

    [Fact]
    public void Unfocused_game_never_triggers_and_rapid_growth_requires_slow_frames()
    {
        var unfocused = new AutomaticTraceDetector();
        var growth = new AutomaticTraceDetector();
        string? reason = null;
        for (int i = 0; i <= 21; i++)
        {
            var sample = Sample(i, true);
            sample.Trace!.IsFocused = false;
            Assert.Null(unfocused.Observe(sample, Start.AddSeconds(i)));
            sample = Sample(i, i >= 20);
            sample.Trace!.ManagedMemoryBytes += i * 70L * 1024 * 1024;
            reason = growth.Observe(sample, Start.AddSeconds(i));
        }
        Assert.Contains("memory", reason!);
    }

    internal static LiveSnapshot Sample(int second, bool slow = false) => new()
    {
        SessionId = "game-session", GameplayId = "cycle", State = "gameplay",
        Players = [new() { Id = "local", IsLocal = true, RoomId = "SU_A01" }],
        Trace = new()
        {
            ManagedMemoryBytes = 1024L * 1024 * 1024,
            Performance = new()
            {
                Ready = true, Sequence = second + 1, GameplayId = "cycle", DurationSeconds = 1,
                FrameCount = slow ? 2 : 60, MaximumFrameMilliseconds = slow ? 700 : 20,
                FramesOver250Milliseconds = slow ? 2 : 0, FramesOver500Milliseconds = slow ? 1 : 0
            }
        }
    };
}
