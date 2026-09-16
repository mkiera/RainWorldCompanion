using System.Text.Json;
using RainWorldCompanion.LiveProtocol;
using RWCompanion.Mod;

namespace RainWorldCompanion.Core.Tests;

public sealed class FramePerformanceSamplerTests
{
    [Fact]
    public void Completed_window_includes_hitches_between_transport_snapshots()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 3);
        sampler.Observe("game", true, 0.125);
        sampler.Observe("game", true, 0.25);
        sampler.Observe("game", true, 0.5);
        Assert.Null(sampler.Latest);
        sampler.Observe("game", true, 0.125);

        var summary = Assert.IsType<LiveFramePerformance>(sampler.Latest);
        Assert.True(summary.Ready);
        Assert.Equal("game", summary.GameplayId);
        Assert.Equal(1, summary.Sequence);
        Assert.Equal(1, summary.DurationSeconds);
        Assert.Equal(4, summary.FrameCount);
        Assert.Equal(500, summary.MaximumFrameMilliseconds);
        Assert.Equal(2, summary.FramesOver250Milliseconds);
        Assert.Equal(1, summary.FramesOver500Milliseconds);
        Assert.True(summary.GarbageCollections >= 0);
    }

    [Fact]
    public void Completed_window_survives_reads_and_is_not_mutated_by_next_window()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 0.01);
        sampler.Observe("game", true, 1.5);
        var previous = sampler.Latest;
        sampler.Observe("game", true, 0.125);
        Assert.Same(previous, sampler.Latest);
        for (int frame = 0; frame < 7; frame++) sampler.Observe("game", true, 0.125);

        Assert.NotSame(previous, sampler.Latest);
        Assert.Equal(2, sampler.Latest!.Sequence);
        Assert.Equal(8, sampler.Latest.FrameCount);
        Assert.Equal(0, sampler.Latest.FramesOver250Milliseconds);
        Assert.Equal(1.5, previous!.DurationSeconds);
        Assert.Equal(1500, previous.MaximumFrameMilliseconds);
    }

    [Fact]
    public void Pause_discards_partial_window_and_excludes_resume_loading_frame()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 0.01);
        sampler.Observe("game", true, 0.75);
        sampler.Observe("game", false, 8);
        sampler.Observe("game", true, 8);
        Assert.Null(sampler.Latest);
        for (int frame = 0; frame < 8; frame++) sampler.Observe("game", true, 0.125);
        Assert.Equal(125, sampler.Latest!.MaximumFrameMilliseconds);
        Assert.Equal(8, sampler.Latest.FrameCount);
    }

    [Fact]
    public void Gameplay_change_clears_stale_summary_but_keeps_sequence_monotonic()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("first", true, 0.01);
        sampler.Observe("first", true, 1);
        sampler.Observe("second", true, 10);
        Assert.Null(sampler.Latest);
        for (int frame = 0; frame < 8; frame++) sampler.Observe("second", true, 0.125);
        Assert.Equal("second", sampler.Latest!.GameplayId);
        Assert.Equal(2, sampler.Latest.Sequence);
        Assert.Equal(125, sampler.Latest.MaximumFrameMilliseconds);
        sampler.Observe("", false, 10);
        Assert.Null(sampler.Latest);
    }

    [Fact]
    public void Frame_observation_does_not_allocate_between_completed_windows()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 0.01);
        for (int frame = 0; frame < 100; frame++) sampler.Observe("game", true, 0.000001);
        long minimumAllocated = long.MaxValue;
        // Measure steady-state batches without counting one-time runtime initialization.
        for (int batch = 0; batch < 5; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 10000; frame++) sampler.Observe("game", true, 0.000001);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }
        Assert.Equal(0, minimumAllocated);
    }

    [Fact]
    public void Invalid_frame_values_cannot_poison_completed_summary()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 0.01);
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1, 0 })
            sampler.Observe("game", true, value);
        sampler.Observe("game", true, 1);
        Assert.Equal(1, sampler.Latest!.FrameCount);
        Assert.Equal(1, sampler.Latest.DurationSeconds);
    }

    [Fact]
    public void Performance_summary_roundtrips_and_is_optional_for_older_hooks()
    {
        var sampler = new FramePerformanceSampler();
        sampler.Observe("game", true, 0.01);
        sampler.Observe("game", true, 1.5);
        var source = new LiveSnapshot { Trace = new() { Performance = sampler.Latest } };
        var restored = JsonSerializer.Deserialize<LiveSnapshot>(JsonSerializer.Serialize(source));
        Assert.Equal("game", restored!.Trace!.Performance!.GameplayId);
        Assert.Equal(1500, restored.Trace.Performance.MaximumFrameMilliseconds);
        Assert.Equal(1, restored.Trace.Performance.FramesOver500Milliseconds);
        Assert.Null(JsonSerializer.Deserialize<LiveSnapshot>("{\"Trace\":{}}")!.Trace!.Performance);
    }
}
