using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Tests.Support;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// Variable refresh, a 29.97 fps capture at 2x on 119.88 Hz with the card's arrival lag (2026-11-19 session: timestamp to
/// frame available p50 27.71, p99 28.54, max 30.31 ms): a generated slot whose next real frame comes too late for it is
/// skipped (<see cref="SchedulerOptions.VrrLateGeneratedNs"/> 0) or shown late with its content moved to match.
/// </summary>
public class VrrLateGeneratedTests(ITestOutputHelper output)
{
    private static readonly FrameRate Ntsc30 = new(30000, 1001);

    private static SchedulerOptions Options(long lateNs) => Vrr(120) with
    {
        DisplayRate = new FrameRate(120000, 1001),
        MaxMultiplier = 2,
        VrrLateGeneratedNs = lateNs,
    };

    // Base lag plus a uniform millisecond, and in one frame of a hundred up to 2.4 ms more: p50 about 27.7 ms, p99 about
    // 28.6, the largest about 30.3, as measured on the card.
    private static SimulationOptions Card(double seconds, double dropProbability = 0) => Sim(Ntsc30, seconds, dNs: 2 * Ms) with
    {
        GenerationJitterNs = 300_000,
        WakeLatenessNs = 50_000,
        WakeJitterNs = 50_000,
        Source = new SimulatedSourceOptions
        {
            Rate = Ntsc30,
            IngestLatencyNs = 27_200_000,
            IngestJitterNs = 1_000_000,
            IngestTailProbability = 0.01,
            IngestTailNs = 2_400_000,
            DropProbability = dropProbability,
            Seed = 11,
        },
    };

    [Fact]
    public void Showing_late_instead_of_skipping_cuts_the_skips_tenfold_at_the_same_latency_and_counts_them_apart()
    {
        var before = Run(Options(0), Card(300));
        var after = Run(Options(4 * Ms), Card(300));
        output.WriteLine($"300 s: skipped before {before.Result.SkippedSlots}, after {after.Result.SkippedSlots} (delayed {after.Scheduler.Counters.DelayedSlots}); real latency mean {before.Result.RealLatencyMeanNs / 1e6:0.000} / {after.Result.RealLatencyMeanNs / 1e6:0.000} ms");

        // Before: the tail skips generated slots (the image before stays a whole slot longer), none delayed.
        Assert.True(before.Result.SkippedSlots >= 20, $"skipped before {before.Result.SkippedSlots}");
        Assert.Equal(0, before.Scheduler.Counters.DelayedSlots);
        Assert.Equal(0, before.Records.Count(r => r.Reason == SlotReason.Delayed));

        // After: at least ten times fewer skips; each slot no longer skipped is a generated slot shown late, counted apart.
        Assert.True(after.Result.SkippedSlots * 10 <= before.Result.SkippedSlots, $"skipped before {before.Result.SkippedSlots}, after {after.Result.SkippedSlots}");
        var delayed = after.Records.Where(r => r.Reason == SlotReason.Delayed).ToList();
        Assert.NotEmpty(delayed);
        Assert.All(delayed, r => Assert.Equal(SlotKind.Generated, r.Kind));
        Assert.Equal(after.Scheduler.Counters.DelayedSlots, delayed.Count);

        // The same real frames, the same D and latency (the delay costs none), nothing missed, causal and consistent:
        // a delayed slot's content follows its frames' timestamps and t (moved with the display time), t inside (0, 1).
        Assert.Equal(before.Result.Real, after.Result.Real);
        Assert.Equal(0, before.Result.Missed);
        Assert.Equal(0, after.Result.Missed);
        Assert.Equal(0, after.Result.CausalityViolations);
        Assert.Equal(0, after.Result.TimestepInconsistencies);
        Assert.Equal(0, after.Result.ContentRegressions);
        Assert.InRange(after.Result.RealLatencyMeanNs - before.Result.RealLatencyMeanNs, -50_000, 50_000);
        Assert.InRange(after.Result.MaxTimestep, 0.5, 0.75);

        // Every slot is still either shown or skipped: generated + skipped is the same with and without the delay
        // (within the few slots the two runs' different timing moves across the statistics' start).
        Assert.InRange(
            (after.Result.Generated + after.Result.SkippedSlots) - (before.Result.Generated + before.Result.SkippedSlots), -2, 2);

        // Each delayed slot is shown at most the limit after its planned time, and still a shortest refresh period before
        // the real frame after it.
        var minimumSpacing = (long)Math.Floor(Options(0).DisplayRate.PeriodNs * (1.0 - Options(0).VrrRateTolerance));
        var byIndex = after.Records.ToDictionary(r => r.SlotIndex);
        foreach (var r in delayed)
        {
            // τ = V − D: the content moved with the display time; t past the planned 0.5 by the delay over T.
            Assert.InRange(r.ContentNs - (r.TargetDisplayNs - r.PipelineDelayNs), -1_000, 1_000);
            Assert.InRange((r.Timestep - 0.5) * Ntsc30.PeriodNs, 0, (4 * Ms) + 1_000);
            if (byIndex.TryGetValue(r.SlotIndex + 1, out var next))
            {
                Assert.Equal(SlotKind.Real, next.Kind);
                Assert.True(next.TargetDisplayNs - r.TargetDisplayNs >= minimumSpacing, $"slot {r.SlotIndex} crowds the real frame after it");
            }
        }
    }

    [Fact]
    public void A_frame_the_source_dropped_still_skips_its_first_slots_and_they_are_counted_as_the_sources()
    {
        // A dropped source frame makes an interval of 2T with four slots; the frame after it comes a whole interval later
        // than the first two generated slots need, far beyond any delay limit: those are skipped either way, and counted
        // as in long intervals.
        var before = Run(Options(0), Card(120, dropProbability: 0.02));
        var after = Run(Options(4 * Ms), Card(120, dropProbability: 0.02));
        var counters = after.Scheduler.Counters;
        output.WriteLine($"drops: skipped before {before.Scheduler.Counters.Skipped} (long {before.Scheduler.Counters.SkippedInLongIntervals}), after {counters.Skipped} (long {counters.SkippedInLongIntervals}), delayed {counters.DelayedSlots}");
        Assert.True(counters.SkippedInLongIntervals >= 2 * 40, $"long-interval skips {counters.SkippedInLongIntervals}");
        Assert.Equal(before.Scheduler.Counters.SkippedInLongIntervals, counters.SkippedInLongIntervals);
        Assert.True(counters.Skipped - counters.SkippedInLongIntervals <= (before.Scheduler.Counters.Skipped - before.Scheduler.Counters.SkippedInLongIntervals) / 5);
        Assert.Equal(0, after.Result.Missed);
        Assert.Equal(0, after.Result.TimestepInconsistencies);
    }

    [Fact]
    public void Off_by_default_and_inert()
    {
        Assert.Equal(0, new SchedulerOptions().VrrLateGeneratedNs);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulerOptions { VrrLateGeneratedNs = -1 }.Validate());

        // Fixed refresh ignores it: the same decisions with and without.
        var a = Run(Fixed(120), Sim(FrameRate.FromHz(30), 10));
        var b = Run(Fixed(120) with { VrrLateGeneratedNs = 4 * Ms }, Sim(FrameRate.FromHz(30), 10));
        Assert.Equal(a.Records.Select(r => (r.Kind, r.TargetDisplayNs, r.ContentNs)), b.Records.Select(r => (r.Kind, r.TargetDisplayNs, r.ContentNs)));
    }
}
