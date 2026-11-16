using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class DisturbanceSchedulingTests
{
    [Fact]
    public void Random_drops_never_use_unarrived_frames_never_go_backwards_and_interpolate_across_single_drops()
    {
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), DropProbability = 0.05, Seed = 42 };
        var sim = Sim(FrameRate.FromHz(60), 60) with { Source = sourceOptions, StatsStartNs = 5 * Second };
        var run = Run(Fixed(120), sim);
        var steady = run.From(5 * Second);
        var times = SourceTimes(sourceOptions, 61 * Second);
        var t = FrameRate.FromHz(60).PeriodNs;

        Assert.True(run.Result.SourceFramesDropped > 100);
        Assert.Equal(0, run.Result.CausalityViolations);
        Assert.Equal(0, run.Result.ContentRegressions);
        Assert.Equal(0, run.Result.Missed);

        // A repeat only ever happens because the next real frame was missing.
        Assert.All(steady.Where(r => r.Kind == SlotKind.Repeat), r => Assert.True(r.Reason is SlotReason.Starved or SlotReason.Hold, r.Reason.ToString()));

        // Generated frames use the bracketing frames' real timestamps, including 2T gaps left by a drop.
        var acrossDrops = 0;
        foreach (var g in steady.Where(r => r.Kind == SlotKind.Generated))
        {
            var tsA = times[g.FrameA];
            var tsB = times[g.FrameB];
            Assert.InRange(tsA + (g.Timestep * (tsB - tsA)), g.ContentNs - 1, g.ContentNs + 1);
            Assert.True(g.ArrivalNs <= g.DecisionNs);
            if (tsB - tsA > 1.5 * t)
            {
                acrossDrops++;
            }
        }

        Assert.True(acrossDrops > 50, $"only {acrossDrops} generated frames span a dropped frame");

        // Drops starve their own slots but do not raise D for everyone: it stays at the drop-free value.
        Assert.All(steady, r => Assert.InRange(r.PipelineDelayNs, 16_666_665, 16_666_668));
    }

    [Fact]
    public void Uneven_frame_times_use_actual_timestamps_and_never_starve()
    {
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(30), TimestampJitterNs = 5 * Ms, Seed = 3 };
        var sim = Sim(FrameRate.FromHz(30), 60) with { Source = sourceOptions };
        var run = Run(Fixed(120), sim);
        var steady = run.From(3 * Second);
        var times = SourceTimes(sourceOptions, 61 * Second);

        Assert.False(run.Result.Diagnostics.PhaseCoherent);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.ContentRegressions);
        Assert.Equal(0, run.Result.Repeated);

        foreach (var g in steady.Where(r => r.Kind == SlotKind.Generated))
        {
            var tsA = times[g.FrameA];
            var tsB = times[g.FrameB];
            var tExpected = (g.IdealContentNs - tsA) / (double)(tsB - tsA);
            Assert.InRange(g.Timestep, tExpected - 1e-9, tExpected + 1e-9);
            Assert.InRange(g.IdealContentNs, tsA, tsB);
        }

        // Ideal content advances one refresh per slot, bent only by slew-limited changes of D as the interval
        // percentile follows the jitter; those changes are rare.
        var p = run.PeriodNs;
        var upPerSlot = run.Options.SlewUpNsPerSecond * (long)p / Second;
        var downPerSlot = run.Options.SlewDownNsPerSecond * (long)p / Second;
        var bent = 0;
        for (var i = 1; i < steady.Count; i++)
        {
            var step = steady[i].IdealContentNs - steady[i - 1].IdealContentNs;
            Assert.InRange(step, (long)p - upPerSlot - 2, (long)p + downPerSlot + 2);
            if (Math.Abs(step - p) > 2)
            {
                bent++;
            }
        }

        Assert.True(bent < steady.Count / 20, $"{bent} of {steady.Count} slots had D moving");
    }

    [Fact]
    public void Uneven_but_on_grid_frame_times_stay_phase_locked_and_never_starve()
    {
        // A 40 fps game in a 60 Hz signal alternates one and two signal periods.
        var signal = FrameRate.FromHz(60);
        var stamps = new List<long>();
        for (var i = 0; stamps.Count < 1200; i += stamps.Count % 2 == 0 ? 1 : 2)
        {
            stamps.Add(signal.TickTimeNs(i));
        }

        var sim = Sim(signal, 25) with { Source = new SimulatedSourceOptions { ExplicitTimestamps = stamps.ToArray() } };
        var run = Run(Fixed(120), sim);
        var steady = run.From(2 * Second);

        Assert.True(run.Result.Diagnostics.PhaseCoherent);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.Repeated);

        // Intervals of 2 and 4 refreshes: t = 1/2 in the short ones, 1/4, 1/2, 3/4 in the long ones.
        var ts = steady.Where(r => r.Kind == SlotKind.Generated).Select(r => Math.Round(r.Timestep, 6)).Distinct().Order().ToArray();
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, ts);
        for (var i = 1; i < steady.Count; i++)
        {
            Assert.InRange(steady[i].ContentNs - steady[i - 1].ContentNs, 8_333_332, 8_333_335);
        }
    }

    [Fact]
    public void Long_hold_stops_generation_shows_the_last_frame_and_resumes_cleanly_on_fixed_refresh()
    {
        var hold = new SimulatedHold(5 * Second, 3 * Second);
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), Holds = new[] { hold } };
        var sim = Sim(FrameRate.FromHz(60), 12) with { Source = sourceOptions };
        var run = Run(Fixed(120), sim);
        var times = SourceTimes(sourceOptions, 13 * Second);
        var lastBefore = times.Where(kv => kv.Value < hold.StartNs).MaxBy(kv => kv.Value);
        var firstAfter = times.Where(kv => kv.Value >= hold.StartNs + hold.DurationNs).MinBy(kv => kv.Value);
        var d = 16_666_667L;

        Assert.Equal(1, run.Result.Counters.HoldsDetected);
        Assert.Equal(0, run.Result.Missed);

        // Never interpolated across the hold.
        Assert.DoesNotContain(run.Records, r => r.Kind == SlotKind.Generated && r.FrameA == lastBefore.Key);

        // While held: no generation at all, the last frame stays on screen, flagged as a hold once past ~2.5 T.
        var holdStart = lastBefore.Value + d + (long)(2.5 * FrameRate.FromHz(60).PeriodNs) + (10 * Ms);
        var holdEnd = firstAfter.Value + d - (10 * Ms);
        var during = run.Records.Where(r => r.TargetDisplayNs > holdStart && r.TargetDisplayNs < holdEnd).ToList();
        Assert.True(during.Count > 300);
        Assert.All(during, r =>
        {
            Assert.Equal(SlotKind.Repeat, r.Kind);
            Assert.Equal(SlotReason.Hold, r.Reason);
            Assert.Equal(lastBefore.Key, r.FrameA);
        });

        // Resume: the first new frame is shown real at its normal delay, then the 2x pattern continues.
        var resume = run.Records.FindIndex(r => r.FrameA == firstAfter.Key && r.Kind == SlotKind.Real);
        Assert.True(resume > 0);
        Assert.InRange(run.Records[resume].ActualDisplayNs - firstAfter.Value, d - 2, d + 2);
        for (var k = 1; k <= 8; k++)
        {
            var r = run.Records[resume + k];
            Assert.Equal(k % 2 == 1 ? SlotKind.Generated : SlotKind.Real, r.Kind);
        }

        // The hold is excluded from the interval estimate and D is unchanged.
        Assert.InRange(run.Result.Diagnostics.SourceIntervalNs, 16_666_660, 16_666_670);
        Assert.InRange(run.Result.PipelineDelayMinNs, d - 2, d + 2);
        Assert.InRange(run.Result.PipelineDelayMaxNs, d - 2, d + 2);
    }

    [Fact]
    public void Long_hold_on_vrr_presents_nothing_and_never_interpolates_across()
    {
        var hold = new SimulatedHold(5 * Second, 3 * Second);
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), Holds = new[] { hold } };
        var sim = Sim(FrameRate.FromHz(60), 12) with { Source = sourceOptions };
        var run = Run(Vrr(120), sim);
        var times = SourceTimes(sourceOptions, 13 * Second);
        var lastBefore = times.Where(kv => kv.Value < hold.StartNs).MaxBy(kv => kv.Value);
        var firstAfter = times.Where(kv => kv.Value >= hold.StartNs + hold.DurationNs).MinBy(kv => kv.Value);

        Assert.Equal(1, run.Result.Counters.HoldsDetected);
        Assert.DoesNotContain(run.Records, r => r.Kind == SlotKind.Generated && r.FrameA == lastBefore.Key);

        var shown = run.Records.Where(r => r.Kind is SlotKind.Real or SlotKind.Generated).ToList();
        var lastBeforeShown = shown.Last(r => r.FrameA == lastBefore.Key);
        var firstAfterShown = shown.First(r => r.FrameA == firstAfter.Key);
        Assert.Equal(SlotKind.Real, lastBeforeShown.Kind);
        Assert.Equal(SlotKind.Real, firstAfterShown.Kind);
        Assert.Equal(shown.IndexOf(lastBeforeShown) + 1, shown.IndexOf(firstAfterShown));
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.ContentRegressions);
    }

    [Fact]
    public void Source_discontinuity_is_never_interpolated_across()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        var t = (long)FrameRate.FromHz(60).PeriodNs;
        long id = 0, now = 0;
        var decisions = new List<SlotDecision>();
        for (var i = 0; i < 300; i++)
        {
            var ts = i * t;
            var discontinuity = i == 150;
            while (scheduler.NextDecisionTime(now) < ts)
            {
                now = Math.Max(now, scheduler.NextDecisionTime(now));
                decisions.Add(scheduler.Schedule(now));
            }

            now = Math.Max(now, ts);
            Assert.True(scheduler.OnFrameArrived(new FrameArrival(id++, ts, ts, discontinuity)));
        }

        Assert.Equal(1, scheduler.Counters.Discontinuities);
        Assert.DoesNotContain(decisions, d => d.Kind == SlotKind.Generated && d.FrameA == 149 && d.FrameB == 150);
        Assert.Contains(decisions, d => d.Reason == SlotReason.Discontinuity && d.FrameA == 149);
        Assert.Contains(decisions, d => d.Kind == SlotKind.Generated && d.FrameA == 150);
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(-100.0)]
    public void Clock_skew_between_source_and_panel_is_tracked_without_a_single_hitch(double ppm)
    {
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), ClockSkewPpm = ppm };
        var sim = Sim(FrameRate.FromHz(60), 600) with { Source = sourceOptions, StatsStartNs = 2 * Second };
        var run = Run(Fixed(120), sim);

        // 100 ppm walks the phase through a whole refresh every 83 s; D follows it instead of skipping a slot.
        Assert.True(run.Result.Diagnostics.PhaseCoherent);
        Assert.InRange(Math.Abs(run.Result.Diagnostics.PhaseDriftPpm - ppm), 0, 5);
        Assert.Equal(0, run.Result.Repeated);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.ContentRegressions);
        Assert.True(run.Result.PipelineDelayMaxNs - run.Result.PipelineDelayMinNs > 7 * Ms);
    }

    [Fact]
    public void Ingest_latency_is_measured_and_added_to_the_delay()
    {
        var sourceOptions = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), IngestLatencyNs = 1_200_000 };
        var sim = Sim(FrameRate.FromHz(60), 10) with { Source = sourceOptions };
        var run = Run(Vrr(120), sim);
        var tNs = (long)Math.Round(FrameRate.FromHz(60).PeriodNs);

        Assert.Equal(1_200_000, run.Result.Diagnostics.ArrivalLagNs);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.CausalityViolations);
        var expected = LatencyModel.AddedLatencyNs(tNs, 2, 2 * Ms) + 500_000 + 1_200_000;
        Assert.InRange(run.Result.RealLatencyMeanNs, expected - 2, expected + 2);
    }
}
