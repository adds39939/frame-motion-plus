using FrameMotionPlus.App.Bench;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Platform.Tests.Cli;

/// <summary>
/// The bench must fail when the pacing loop really misbehaves. Faults are injected deterministically into its first
/// measured full-loop slot. Timing is never judged here; only that each fault is detected and reported. These run in
/// the serial collection, so the only allocations and collections in the process are the ones injected.
/// </summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class BenchJitterFaultTests
{
    private static readonly JitterBenchOptions Short = new()
    {
        DurationNs = Nanoseconds.PerSecond,
        WarmupNs = 200 * Nanoseconds.PerMillisecond,
        BlockNs = 600 * Nanoseconds.PerMillisecond,
        Rate = FrameRate.FromHz(120),
    };

    private static JitterBenchResult Run(JitterBenchOptions options) => new JitterBench(options).Run();

    [Fact]
    public void A_stall_counts_every_skipped_slot_and_fails_the_missed_slot_budget()
    {
        var r = Run(Short with { Faults = new JitterBenchFaults(StallNs: 110 * Nanoseconds.PerMillisecond) });

        // 110 ms at 120 Hz passes over about 13 slots, all of them due while the thread was not running.
        Assert.True(r.FullStats.Skipped >= 10, $"skipped {r.FullStats.Skipped}");
        Assert.Equal(r.FullStats.Slots + r.FullStats.Skipped, r.FullStats.WakeLateness.Count);
        Assert.True(r.FullStats.WakeLateness.Max >= 100 * Nanoseconds.PerMillisecond, $"max {r.FullStats.WakeLateness.Max} ns");
        Assert.True(r.FullStats.OverGate >= 10, $"over gate {r.FullStats.OverGate}");
        Assert.False(r.GatePassed);
        Assert.Contains(r.Failures, f => f.Contains("missed slot", StringComparison.Ordinal) && f.Contains("budget of 0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_missed_slot_budget_is_configurable()
    {
        var r = Run(Short with { MaxMissed = 1000, Faults = new JitterBenchFaults(StallNs: 110 * Nanoseconds.PerMillisecond) });

        Assert.True(r.FullStats.Missed > 0);
        Assert.DoesNotContain(r.Failures, f => f.Contains("missed slot", StringComparison.Ordinal));
    }

    [Fact]
    public void An_allocation_on_the_pacing_thread_fails_the_run()
    {
        var r = Run(Short with { Faults = new JitterBenchFaults(AllocateBytes: 4096) });

        Assert.True(r.PacingAllocatedBytes >= 4096, $"allocated {r.PacingAllocatedBytes} B");
        Assert.Contains(r.Failures, f => f.Contains("pacing thread allocated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_collection_during_the_measured_time_fails_the_run()
    {
        var r = Run(Short with { Faults = new JitterBenchFaults(Collect: true) });

        Assert.True(r.MeasuredGc.Collections >= 1);
        Assert.Contains(r.Failures, f => f.Contains("GC collection", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_run_allocates_nothing_on_its_hot_threads()
    {
        var r = Run(Short);

        Assert.Equal(0, r.PacingAllocatedBytes);
        Assert.Equal(0, r.SourceAllocatedBytes);

        // 1 s at 120 Hz is 120 slots per loop. Elsewhere the portable waiter sleeps in timer ticks (15.6 ms on Windows
        // by default), so only a few slots are certain.
        var floor = OperatingSystem.IsLinux() ? 50 : 1;
        Assert.True(r.FullStats.Slots >= floor, $"full loop served {r.FullStats.Slots}");
        Assert.True(r.BaselineStats!.Slots >= floor, $"timer-only loop served {r.BaselineStats.Slots}");
        Assert.True(r.MeasuredWindowNs > Nanoseconds.PerSecond);
        Assert.DoesNotContain(r.Failures, f => f.Contains("allocated", StringComparison.Ordinal));
    }
}

public class BenchJitterPlanTests
{
    [Theory]
    [InlineData(10.0, 1.0, 5.0)]
    [InlineData(60.0, 1.0, 5.0)]
    [InlineData(0.3, 0.1, 5.0)]
    [InlineData(7.0, 0.0, 0.5)]
    public void Blocks_alternate_starting_with_the_full_loop_and_measure_each_loop_for_the_duration(double durationS, double warmupS, double blockS)
    {
        var o = new JitterBenchOptions
        {
            DurationNs = Nanoseconds.FromSeconds(durationS),
            WarmupNs = Nanoseconds.FromSeconds(warmupS),
            BlockNs = Nanoseconds.FromSeconds(blockS),
        };

        var blocks = JitterBench.PlanBlocks(1_000, o);

        Assert.True(blocks[0].Full);
        Assert.Equal(1_000, blocks[0].StartNs);
        for (var i = 0; i < blocks.Length; i++)
        {
            Assert.True(blocks[i].StartNs <= blocks[i].MeasuredFromNs && blocks[i].MeasuredFromNs < blocks[i].StopNs);
            Assert.True(blocks[i].StopNs - blocks[i].StartNs <= Math.Max(o.BlockNs, o.WarmupNs + o.BlockNs));
            if (i > 0)
            {
                Assert.Equal(blocks[i - 1].StopNs, blocks[i].StartNs);
                Assert.NotEqual(blocks[i - 1].Full, blocks[i].Full);
            }
        }

        foreach (var full in new[] { true, false })
        {
            var mine = blocks.Where(b => b.Full == full).ToArray();
            Assert.Equal(o.DurationNs, mine.Sum(b => b.StopNs - b.MeasuredFromNs));
            Assert.Equal(Math.Max(o.WarmupNs, JitterBenchOptions.BlockSettleNs), mine[0].MeasuredFromNs - mine[0].StartNs);
            Assert.All(mine.Skip(1), b => Assert.Equal(JitterBenchOptions.BlockSettleNs, b.MeasuredFromNs - b.StartNs));
        }
    }

    [Fact]
    public void Without_a_baseline_the_full_loop_runs_in_one_block()
    {
        var o = new JitterBenchOptions { DurationNs = 3 * Nanoseconds.PerSecond, Baseline = false };

        var blocks = JitterBench.PlanBlocks(0, o);

        Assert.Equal([new BenchBlock(true, 0, Nanoseconds.PerSecond, 4 * Nanoseconds.PerSecond)], blocks);
    }

    [Fact]
    public void Sample_storage_is_bounded()
    {
        var o = new JitterBenchOptions { DurationNs = 86_400 * Nanoseconds.PerSecond, Rate = FrameRate.FromHz(1000) };

        Assert.True(o.SlotsPerLoopBound > JitterBenchOptions.MaxSlotsPerLoop);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JitterBench(o));
    }
}
