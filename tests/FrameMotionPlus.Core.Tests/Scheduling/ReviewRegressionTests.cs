using System.Diagnostics;
using System.Reflection;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Tests.Support;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// One or more regression tests per finding of the Phase 1 scheduler review. Each reproduces the reviewer's
/// scenario (non-ideal wake-ups, rate changes, stalls, drops at a non-zero phase, ...) and failed on the code
/// that was reviewed.
/// </summary>
public class ReviewRegressionTests
{
    private static readonly FrameRate F60 = FrameRate.FromHz(60);
    private static readonly long T60 = (long)Math.Round(FrameRate.FromHz(60).PeriodNs);

    private static SchedulerOptions Mode(string mode, long hz = 120) => mode == "vrr" ? Vrr(hz) : Fixed(hz);

    // ---- Finding 1: VRR generated slots had zero timing slack; display timing inherited wake jitter ----

    [Theory]
    [InlineData(1L)]
    [InlineData(20_000L)]
    [InlineData(100_000L)]
    [InlineData(400_000L)]
    public void F1_vrr_late_wakeups_keep_every_generated_slot(long wakeLateNs)
    {
        var sim = Sim(F60, 20) with { WakeLatenessNs = wakeLateNs, StatsStartNs = 5 * Second };
        var run = Run(Vrr(120), sim);

        Assert.InRange(run.Result.Generated, run.Result.Real - 2, run.Result.Real + 2);
        Assert.Equal(0, run.Result.SkippedSlots);
        Assert.Equal(0, run.Result.Missed);

        // The lateness is measured and added to D once; display timing then stays exactly periodic.
        var expected = LatencyModel.AddedLatencyNs(T60, 2, 2 * Ms) + 500_000 + wakeLateNs;
        Assert.InRange(run.Result.RealLatencyMinNs, expected - 2, expected + 2);
        Assert.InRange(run.Result.RealLatencyMaxNs, expected - 2, expected + 2);
    }

    [Fact]
    public void F1_vrr_jittered_wakeups_do_not_jitter_display_timing()
    {
        var sim = Sim(F60, 30) with { WakeJitterNs = 300_000, StatsStartNs = 10 * Second };
        var run = Run(Vrr(120), sim);

        Assert.InRange(run.Result.Generated, run.Result.Real - 2, run.Result.Real + 2);
        Assert.Equal(0, run.Result.SkippedSlots);
        Assert.Equal(0, run.Result.Missed);
        Assert.InRange(run.Result.RealLatencyMaxNs - run.Result.RealLatencyMinNs, 0, 50_000);
    }

    [Theory]
    [InlineData(60.0, 50.0, 0L)]
    [InlineData(60.0, 0.0, 300_000L)]
    [InlineData(50.0, 0.0, 1_000_000L)]
    public void F1_vrr_with_skew_or_generation_jitter_keeps_generating(double fps, double skewPpm, long dJitterNs)
    {
        var rate = FrameRate.FromDouble(fps);
        var sim = Sim(rate, 30) with
        {
            GenerationJitterNs = dJitterNs,
            Source = new SimulatedSourceOptions { Rate = rate, ClockSkewPpm = skewPpm },
        };
        var run = Run(Vrr(120, initialDNs: (2 * Ms) + dJitterNs), sim);

        Assert.InRange(run.Result.Generated, run.Result.Real - 2, run.Result.Real + 2);
        Assert.Equal(0, run.Result.SkippedSlots);
        Assert.Equal(0, run.Result.Missed);
    }

    [Fact]
    public void F1_fixed_refresh_wakeups_later_than_the_margin_never_miss()
    {
        var sim = Sim(F60, 30) with { WakeJitterNs = 600_000 };
        var run = Run(Fixed(120), sim);

        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.InRange(run.Result.Diagnostics.WakeLatenessNs, 550_000, 600_000);
    }

    [Fact]
    public void F1_delay_increases_are_never_deadbanded()
    {
        var tuner = new PipelineDelayTuner(16, 0.99, 2 * Ms, Ms / 2, 50 * Ms, 5 * Ms, deadbandNs: Ms / 10);
        tuner.Reset(10 * Ms);
        tuner.Step((10 * Ms) + 50_000, Second);
        Assert.Equal((10 * Ms) + 50_000, tuner.CurrentNs);
    }

    // ---- Finding 2: hold detection locked itself on permanently ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void F2_a_short_first_interval_does_not_lock_hold_detection(string mode)
    {
        var scheduler = new FrameScheduler(Mode(mode));
        var frames = new List<DrivenFrame> { new(0, 0) };
        frames.AddRange(EventDriver.Regular(3 * Ms, F60, 60 * 60));
        var late = EventDriver.Drive(scheduler, frames, 60 * Second).Where(d => d.TargetTimeNs > 50 * Second).ToList();

        Assert.Equal(0, scheduler.Counters.HoldsDetected);
        Assert.InRange(scheduler.Diagnostics.SourceIntervalNs, T60 - 1, T60 + 1);
        Assert.InRange(late.Count(d => d.Kind == SlotKind.Generated), 598, 602);
        Assert.DoesNotContain(late, d => d.Reason is SlotReason.Hold or SlotReason.Starved);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void F2_a_discontinuity_followed_by_a_short_interval_recovers(string mode)
    {
        var scheduler = new FrameScheduler(Mode(mode));
        var frames = EventDriver.Regular(0, F60, 300);
        var t0 = frames[^1].ContentNs + T60;
        frames.Add(new DrivenFrame(t0, t0, Discontinuity: true));
        frames.AddRange(EventDriver.Regular(t0 + (3 * Ms), F60, 60 * 55));
        var late = EventDriver.Drive(scheduler, frames, 60 * Second).Where(d => d.TargetTimeNs > 50 * Second).ToList();

        Assert.Equal(0, scheduler.Counters.HoldsDetected);
        Assert.InRange(scheduler.Diagnostics.SourceIntervalNs, T60 - 1, T60 + 1);
        Assert.InRange(late.Count(d => d.Kind == SlotKind.Generated), 598, 602);
    }

    [Theory]
    [InlineData("fixed", 120, 40, true)]
    [InlineData("vrr", 120, 40, true)]
    [InlineData("fixed", 60, 20, true)]
    [InlineData("vrr", 60, 20, true)]
    [InlineData("fixed", 60, 30, false)]
    [InlineData("vrr", 60, 30, false)]
    public void F2_a_source_rate_drop_reseeds_instead_of_holding_for_ever(string mode, int fast, int slow, bool needsReseed)
    {
        var source = new SimulatedSourceOptions
        {
            Rate = FrameRate.FromHz(fast),
            RateChangeAtNs = 5 * Second,
            RateAfterChange = FrameRate.FromHz(slow),
        };
        // Statistics start once the 256-sample interval window holds only the new rate and the multiplier has settled.
        var sim = Sim(FrameRate.FromHz(fast), 40) with { Source = source, StatsStartNs = 18 * Second };
        var run = Run(Mode(mode), sim);
        var newInterval = (long)Math.Round(FrameRate.FromHz(slow).PeriodNs);

        Assert.InRange(run.Result.Diagnostics.SourceIntervalNs, newInterval - 2, newInterval + 2);
        Assert.Equal(0, run.Result.HoldSlots);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.Missed);
        var m = 120 / slow;
        Assert.InRange(run.Result.Generated, ((m - 1) * run.Result.Real) - 3, ((m - 1) * run.Result.Real) + 3);
        Assert.Equal(needsReseed ? 1 : 0, run.Result.Counters.Reseeds);
        Assert.InRange(run.Result.Counters.HoldsDetected, 0, needsReseed ? 2 : 0);
    }

    [Fact]
    public void F2_estimator_needs_samples_before_holds_and_never_reseeds_on_long_pauses()
    {
        var e = new SourceIntervalEstimator(64, 2.5, 1.75, 0.9, 100 * Ms, minSamplesForHolds: 4, reseedAfter: 3);
        Assert.False(e.Add(3 * Ms));
        Assert.False(e.Add(T60));
        Assert.False(e.Add(T60));
        Assert.False(e.Add(T60));
        Assert.Equal(T60, e.NominalNs);

        // Three consistent 50 ms gaps: the rate fell to 20 fps (the first two are still treated as holds).
        Assert.True(e.Add(50 * Ms));
        Assert.True(e.Add(50 * Ms));
        Assert.False(e.Add(50 * Ms));
        Assert.Equal(1, e.Reseeds);
        Assert.Equal(50 * Ms, e.NominalNs);

        // Pauses above the absolute cap are holds however consistent they are.
        Assert.True(e.Add(500 * Ms));
        Assert.True(e.Add(500 * Ms));
        Assert.True(e.Add(500 * Ms));
        Assert.True(e.Add(500 * Ms));
        Assert.Equal(1, e.Reseeds);

        // Three consecutive sub-cap long gaps re-seed even when their sizes differ (an alternating cadence never
        // supplies three equal ones), so no steady source under the cap can keep the estimator holding.
        e.Reset();
        for (var i = 0; i < 8; i++)
        {
            e.Add(T60);
        }

        Assert.True(e.Add(45 * Ms));
        Assert.True(e.Add(90 * Ms));
        Assert.False(e.Add(45 * Ms));
        Assert.Equal(2, e.Reseeds);
        Assert.Equal(45 * Ms, e.NominalNs);
    }

    // ---- Finding 3: generation never came back after a fallback to 1x ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void F3_generation_returns_after_a_spike_switched_it_off(string mode)
    {
        var sim = Sim(F60, 40) with
        {
            GenerationTimeSpikeFromNs = 5 * Second,
            GenerationTimeSpikeUntilNs = 6 * Second,
            GenerationTimeSpikeNs = 9 * Ms,
            StatsStartNs = 20 * Second,
        };
        var run = Run(Mode(mode), sim);

        Assert.True(run.Result.Diagnostics.GenerationEnabled);
        Assert.InRange(run.Result.Generated, run.Result.Real - 2, run.Result.Real + 2);
        Assert.Equal(0, run.Result.Missed);
        Assert.False(run.Records.Any(r => r.TargetDisplayNs is > 6500 * Ms and < 8 * Second && r.Kind == SlotKind.Generated),
            "the spike did not disable generation, so recovery was not exercised");
    }

    [Fact]
    public void F3_generation_time_samples_age_out()
    {
        var tuner = new PipelineDelayTuner(256, 0.99, 2 * Ms, Ms / 2, 50 * Ms, 5 * Ms, Ms / 10, maxSampleAgeNs: 5 * Second);
        for (var i = 0; i < 10; i++)
        {
            tuner.AddGenerationSample(9 * Ms, Second + i);
        }

        tuner.Expire(4 * Second);
        Assert.Equal(9 * Ms, tuner.GenerationTimeNs);
        tuner.Expire(7 * Second);
        Assert.Equal(0, tuner.SampleCount);
        Assert.Equal(2 * Ms, tuner.GenerationTimeNs);
    }

    // ---- Finding 4: the VRR backlog was never dropped ----

    [Fact]
    public void F4_vrr_latency_recovers_after_one_late_call()
    {
        var sim = Sim(F60, 30) with { StallAtNs = 5 * Second, StallDurationNs = 20 * Ms, StatsStartNs = 6 * Second };
        var run = Run(Vrr(120), sim);
        var expected = LatencyModel.AddedLatencyNs(T60, 2, 2 * Ms) + 500_000;

        Assert.InRange(run.Result.RealLatencyMinNs, expected - 2, expected + 2);
        Assert.InRange(run.Result.RealLatencyMaxNs, expected - 2, expected + 2);
    }

    [Fact]
    public void F4_vrr_drops_the_backlog_after_a_long_stall()
    {
        var sim = Sim(F60, 12) with { StallAtNs = 5 * Second, StallDurationNs = 400 * Ms, StatsStartNs = 5 * Second };
        var run = Run(Vrr(120), sim);
        var expected = LatencyModel.AddedLatencyNs(T60, 2, 2 * Ms) + 500_000;
        var afterStall = run.Records.Where(r => r.DecisionNs >= 5400 * Ms && r.Kind is SlotKind.Real).ToList();

        Assert.True(run.Result.Counters.FramesDroppedLate >= 20);
        Assert.InRange(afterStall[0].ActualDisplayNs - afterStall[0].ContentNs, expected, expected + T60);
        Assert.All(afterStall.Skip(2), r => Assert.InRange(r.ActualDisplayNs - r.ContentNs, expected - 2, expected + 2));
        Assert.Equal(0, run.Result.ContentRegressions);
    }

    [Fact]
    public void F4_vrr_source_faster_than_the_display_keeps_latency_bounded()
    {
        var rate = FrameRate.FromHz(144);
        var run = Run(Vrr(120), Sim(rate, 30));

        Assert.Equal(0, run.Result.Generated);
        Assert.InRange(run.Result.RealLatencyMaxNs, 0, 500_000 + (long)rate.PeriodNs + 100_000);
        var dropped = run.Result.Counters.FramesDroppedLate / (double)run.Result.SourceFramesDelivered;
        // 144 fps through at most ~120 presents per second (2 % VRR tolerance): about one frame in six is dropped.
        Assert.InRange(dropped, 0.13, 0.19);
    }

    [Fact]
    public void F4_vrr_equal_rates_with_negative_skew_do_not_accumulate_latency()
    {
        var sim = Sim(F60, 600) with { Source = new SimulatedSourceOptions { Rate = F60, ClockSkewPpm = -50 } };
        var result = RunCounting(Vrr(60), sim);

        Assert.InRange(result.RealLatencyMaxNs, 0, 600_000);
        Assert.Equal(0, result.Missed);
    }

    // ---- Finding 5: T was the mean interval including dropped-frame gaps ----

    [Fact]
    public void F5_drops_change_neither_T_nor_the_multiplier()
    {
        var rate = FrameRate.FromHz(30);
        var sim = Sim(rate, 60) with { Source = new SimulatedSourceOptions { Rate = rate, DropProbability = 0.1 } };
        var run = Run(Fixed(120), sim);

        Assert.InRange(run.Result.Diagnostics.SourceIntervalNs, 33_333_332, 33_333_334);
        Assert.Equal(4, run.Result.Diagnostics.Multiplier);
        Assert.Equal(0, run.Result.Missed);
    }

    [Fact]
    public void F5_drops_combined_with_fallback_keep_stride_two()
    {
        var rate = FrameRate.FromHz(30);
        var sim = Sim(rate, 60, dNs: 10 * Ms) with { Source = new SimulatedSourceOptions { Rate = rate, DropProbability = 0.1 } };
        var run = Run(Fixed(120, initialDNs: 10 * Ms), sim);

        Assert.Equal(2, run.Result.Diagnostics.Stride);
        Assert.True(run.Result.Diagnostics.GenerationEnabled);
        Assert.True(run.Result.Generated > run.Result.Real * 0.8);
        Assert.Equal(0, run.Result.Missed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.02)]
    [InlineData(0.05)]
    [InlineData(0.2)]
    public void F5_drops_at_a_nonzero_source_phase_do_not_move_D(double dropRate)
    {
        RunResult(0.0, out var reference);
        RunResult(dropRate, out var run);

        // Drop-free: hold 8.333 + lead 2.5 + guard 0.25, aligned to the 5.167 ms source phase, is about 11.5 ms.
        Assert.InRange(reference.PipelineDelayMinNs, 11_499_000, 11_501_000);
        Assert.Equal(reference.PipelineDelayMinNs, reference.PipelineDelayMaxNs);
        Assert.InRange(run.PipelineDelayMinNs, reference.PipelineDelayMinNs - 2, reference.PipelineDelayMinNs + 2);
        Assert.InRange(run.PipelineDelayMaxNs, reference.PipelineDelayMinNs - 2, reference.PipelineDelayMinNs + 2);
        Assert.Equal(0, run.Missed);

        static void RunResult(double drops, out SimulationResult result)
        {
            var source = new SimulatedSourceOptions { Rate = F60, StartNs = 5_167_000, DropProbability = drops, Seed = 3 };
            result = RunCounting(Fixed(120), Sim(F60, 60) with { Source = source, StatsStartNs = 5 * Second });
        }
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, 2)] // 40 fps in a 60 Hz signal: intervals of 2 and 4 refreshes at 120 Hz
    [InlineData(new[] { 2, 3 }, 2)] // 24 fps 3:2 pulldown in 60 Hz: 4 and 6 refreshes
    public void F5_on_grid_uneven_cadences_keep_generation_under_fallback(int[] pattern, int stride)
    {
        var signal = F60;
        var stamps = new List<long>();
        var tick = 0;
        for (var k = 0; k < 1500; k++)
        {
            stamps.Add(signal.TickTimeNs(tick));
            tick += pattern[k % 2];
        }

        var sim = Sim(signal, 25, dNs: 10 * Ms) with { Source = new SimulatedSourceOptions { ExplicitTimestamps = stamps.ToArray() } };
        var run = Run(Fixed(120, initialDNs: 10 * Ms), sim);

        Assert.True(run.Result.Diagnostics.GenerationEnabled);
        Assert.Equal(stride, run.Result.Diagnostics.Stride);
        // 40-in-60: only the 4-refresh intervals have room for a generated frame (one each): real/2 of them.
        Assert.True(run.Result.Generated >= (run.Result.Real / 2) - 2, $"{run.Result.Generated} generated for {run.Result.Real} real");
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.StarvedSlots);
    }

    // ---- Finding 6: the VRR multiplier cap had a tolerance of about 1 ppm ----

    [Theory]
    [InlineData(60.0, -50.0, 0L, 2)]
    [InlineData(60.0, 0.0, 50_000L, 2)]
    [InlineData(30.0, 0.0, 200_000L, 4)]
    public void F6_vrr_multiplier_tolerates_skew_and_jitter(double fps, double skewPpm, long jitterNs, int expectedM)
    {
        var rate = FrameRate.FromDouble(fps);
        var source = new SimulatedSourceOptions { Rate = rate, ClockSkewPpm = skewPpm, TimestampJitterNs = jitterNs };
        var run = Run(Vrr(120), Sim(rate, 20) with { Source = source });

        Assert.Equal(expectedM, run.Result.Diagnostics.Multiplier);
        Assert.InRange(run.Result.Generated, ((expectedM - 1) * run.Result.Real) - 3, ((expectedM - 1) * run.Result.Real) + 3);
        Assert.Equal(0, run.Result.Missed);
    }

    // ---- Finding 7: the simulator could not detect causality violations ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void F7_frames_delivered_ahead_are_never_used_before_they_arrive(string mode)
    {
        var source = new SimulatedSourceOptions { Rate = F60, IngestLatencyNs = Ms, IngestJitterNs = 6 * Ms, Seed = 9 };
        var sim = Sim(F60, 30) with { Source = source, DeliverAheadNs = 40 * Ms };
        var run = Run(Mode(mode), sim);

        Assert.Equal(0, run.Result.CausalityViolations);
        Assert.Equal(0, run.Result.TimestepInconsistencies);
        Assert.Equal(0, run.Result.Missed);
        Assert.True(run.Result.Generated > run.Result.Real / 2);
    }

    // ---- Finding 8: non-integer fallback put generations on consecutive vblanks ----

    [Theory]
    [InlineData(30.0, 144L, 8_000_000L)]
    [InlineData(23.976, 120L, 9_000_000L)]
    public void F8_non_integer_fallback_never_misses_and_spaces_generations_by_the_stride(double fps, long hz, long dNs)
    {
        var rate = FrameRate.FromDouble(fps);
        var run = Run(Fixed(hz, initialDNs: dNs), Sim(rate, 30, dNs));
        var stride = run.Result.Diagnostics.Stride;
        var p = FrameRate.FromHz(hz).PeriodNs;

        Assert.Equal(2, stride);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.True(run.Result.Generated > 0);
        var gens = run.From(2 * Second).Where(r => r.Kind == SlotKind.Generated).ToList();
        for (var i = 1; i < gens.Count; i++)
        {
            Assert.True(gens[i].TargetDisplayNs - gens[i - 1].TargetDisplayNs >= (stride * p) - 2);
        }
    }

    // ---- Finding 9: a small positive re-anchor scheduled the same vblank twice ----

    [Theory]
    [InlineData(60.0, 50_000L)]
    [InlineData(60.0, -50_000L)]
    [InlineData(59.94, 50_000L)]
    [InlineData(60.0, 1_000_000L)]
    [InlineData(60.0, -1_000_000L)]
    public void F9_reanchoring_never_schedules_a_vblank_twice(double fps, long shiftNs)
    {
        var rate = FrameRate.FromDouble(fps);
        var scheduler = new FrameScheduler(Fixed(120));
        var count = 0;
        var decisions = EventDriver.Drive(scheduler, EventDriver.Regular(0, rate, 1200), 10 * Second, afterDecision: (d, s) =>
        {
            if (++count == 700)
            {
                s.UpdateDisplayTiming(d.TargetTimeNs + shiftNs, FrameRate.FromHz(120));
            }
        });

        var p = FrameRate.FromHz(120).PeriodNs;
        var abs = Math.Abs(shiftNs);
        for (var i = 1; i < decisions.Count; i++)
        {
            var dv = decisions[i].TargetTimeNs - decisions[i - 1].TargetTimeNs;
            Assert.InRange(dv, (long)p - abs - 2, (long)p + abs + 2);
        }
    }

    // ---- Finding 10 (minor) ----

    [Fact]
    public void F10a_slew_arithmetic_cannot_overflow_and_slew_is_bounded()
    {
        var tuner = new PipelineDelayTuner(256, 0.99, 2 * Ms, Ms / 2, 1000 * Ms, 5 * Ms, Ms / 10);
        tuner.Reset(10 * Ms);
        Assert.Equal(20 * Ms, tuner.Step(20 * Ms, 10 * Second));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { SlewUpNsPerSecond = 2 * Second }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineDelayTuner(16, 0.99, 0, 0, 2 * Second, 1, 0));
    }

    [Fact]
    public void F10b_zero_snap_epsilon_never_asks_for_a_timestep_outside_the_open_interval()
    {
        var rate = new FrameRate(60000, 1001);
        var result = RunCounting(Fixed(120) with { SnapEpsilon = 0 }, Sim(rate, 600));

        Assert.Equal(0, result.TimestepInconsistencies);
        Assert.True(result.MinTimestep > 0);
        Assert.True((float)result.MaxTimestep < 1f);
    }

    [Fact]
    public void F10c_catching_up_after_a_long_stall_is_constant_time()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        for (var i = 0; i < 10; i++)
        {
            scheduler.OnFrameArrived(new FrameArrival(i, F60.TickTimeNs(i), F60.TickTimeNs(i)));
        }

        var first = scheduler.Schedule(scheduler.NextDecisionTime(9 * T60));
        var year = 365L * 86_400 * Second;
        var watch = Stopwatch.StartNew();
        var late = scheduler.Schedule(first.TargetTimeNs + year);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, $"catch-up took {watch.ElapsedMilliseconds} ms");
        Assert.True(late.TargetTimeNs > first.TargetTimeNs + year);
        var display = FrameRate.FromHz(120);
        Assert.Equal(display.TickIndexAtOrBefore(first.TargetTimeNs + year) - display.TickIndexAtOrBefore(first.TargetTimeNs), late.SkippedSlots);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void F10c_next_decision_time_has_no_side_effects(string mode)
    {
        var frames = EventDriver.Regular(0, F60, 600);
        frames.RemoveAll(f => f.ContentNs is > 3 * Second and < 4 * Second); // a hold
        frames.RemoveAt(400); // and a drop
        var once = EventDriver.Drive(new FrameScheduler(Mode(mode)), frames, 10 * Second);
        var often = EventDriver.Drive(new FrameScheduler(Mode(mode)), frames, 10 * Second, extraNextDecisionCalls: 7);
        Assert.Equal(once, often);

        // And directly: with frames delivered but not yet scheduled (so there is an exhausted interval to step past),
        // asking for the next decision time does not move the slot position or any counter.
        var scheduler = new FrameScheduler(Mode(mode));
        var head = frames.Where(f => f.ArrivalNs < 2 * Second).ToList();
        EventDriver.Drive(scheduler, head, 2 * Second);
        var id = 10_000L;
        foreach (var f in frames.Where(f => f.ArrivalNs >= 2 * Second).Take(3))
        {
            scheduler.OnFrameArrived(new FrameArrival(id++, f.ContentNs, f.ArrivalNs));
        }

        var before = (PrivateState(scheduler), scheduler.Counters, scheduler.Diagnostics);
        for (var i = 0; i < 10; i++)
        {
            scheduler.NextDecisionTime(2100 * Ms);
        }

        Assert.Equal(before, (PrivateState(scheduler), scheduler.Counters, scheduler.Diagnostics));
    }

    [Fact]
    public void F10d_late_real_frames_are_included_in_latency()
    {
        // A copy slower than the decision lead makes every real frame late; they must still be measured.
        var run = Run(Fixed(120), Sim(F60, 10) with { CopyTimeNs = 3 * Ms });
        Assert.True(run.Result.Missed > 0);
        Assert.Equal(run.Result.Real, run.Result.RealLatencySamples);
    }

    [Theory]
    [InlineData(300_000L)]
    [InlineData(500_000L)]
    public void F10e_capture_timestamp_jitter_stays_phase_locked(long jitterNs)
    {
        var source = new SimulatedSourceOptions { Rate = F60, TimestampJitterNs = jitterNs, Seed = 5 };
        var run = Run(Fixed(120), Sim(F60, 30) with { Source = source, StatsStartNs = 5 * Second });

        Assert.True(run.Result.Diagnostics.PhaseCoherent);
        Assert.InRange(run.Result.Real, run.Result.Generated * 0.95, run.Result.Generated * 1.05);
        Assert.Equal(0, run.Result.StarvedSlots);
        Assert.Equal(0, run.Result.Missed);
        Assert.InRange(run.Result.PipelineDelayMaxNs, 0, 16_666_668 + 8_333_334);
    }

    // ---- Test gaps named in the review ----

    [Theory]
    [InlineData(0.05, 0L, 0.0, 0L, 0.9)]
    [InlineData(0.0, 2_000_000L, 0.0, 0L, 0.5)] // ±2 ms frame times: intervals under two refresh periods fit one slot
    [InlineData(0.0, 0L, 100.0, 0L, 0.99)]
    [InlineData(0.02, 1_000_000L, -80.0, 500_000L, 0.55)] // ±1 ms: about a third of intervals fit one slot
    public void Gap_vrr_with_drops_jitter_skew_and_d_jitter_stays_correct(double drops, long frameJitterNs, double skewPpm, long dJitterNs, double minGeneratedPerReal)
    {
        var source = new SimulatedSourceOptions { Rate = F60, DropProbability = drops, TimestampJitterNs = frameJitterNs, ClockSkewPpm = skewPpm, Seed = 21 };
        var sim = Sim(F60, 60) with { Source = source, GenerationJitterNs = dJitterNs };
        var run = Run(Vrr(120, initialDNs: (2 * Ms) + dJitterNs), sim);

        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.ContentRegressions);
        Assert.Equal(0, run.Result.CausalityViolations);
        Assert.Equal(0, run.Result.TimestepInconsistencies);
        Assert.True(run.Result.Generated >= run.Result.Real * minGeneratedPerReal, $"{run.Result.Generated} generated for {run.Result.Real} real");

        // A dropped frame leaves its first slots without inputs (VRR then keeps the previous image): about two per drop.
        Assert.InRange(run.Result.SkippedSlots, 0, (3 * run.Result.SourceFramesDropped) + (run.Result.Slots / 20));
    }

    [Theory]
    [InlineData("fixed", 60L, 120.0)]
    [InlineData("vrr", 120L, 144.0)]
    [InlineData("fixed", 120L, 144.0)]
    [InlineData("vrr", 60L, 120.0)]
    public void Gap_source_faster_than_display_shows_newest_frames_without_generation(string mode, long hz, double fps)
    {
        var rate = FrameRate.FromDouble(fps);
        var run = Run(Mode(mode, hz), Sim(rate, 20));
        var verdict = SimulationCheck.Evaluate(run.Options, run.Simulation, run.Result);

        Assert.Equal(0, run.Result.Generated);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.InRange(run.Result.ShownLatencyMaxNs, 0, 500_000 + (long)Math.Max(rate.PeriodNs, FrameRate.FromHz(hz).PeriodNs) + 100_000);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void Gap_two_hour_soak_with_realistic_disturbances(string mode)
    {
        var source = new SimulatedSourceOptions
        {
            Rate = F60,
            DropProbability = 0.01,
            TimestampJitterNs = 200_000,
            IngestLatencyNs = Ms,
            IngestJitterNs = 500_000,
            ClockSkewPpm = 40,
            Seed = 77,
        };
        var sim = Sim(F60, 2 * 3600) with
        {
            Source = source,
            GenerationJitterNs = 500_000,
            WakeJitterNs = 200_000,
            StatsStartNs = 10 * Second,
        };
        var result = RunCounting(Mode(mode), sim);

        Assert.Equal(0, result.Missed);
        Assert.Equal(0, result.ContentRegressions);
        Assert.Equal(0, result.CausalityViolations);
        Assert.Equal(0, result.TimestepInconsistencies);
        // Two consecutive drops leave a 3T gap, which is a hold by design (over 2.5 T): rare at 1 % drops.
        Assert.InRange(result.HoldSlots, 0, result.Slots / 5000);
        Assert.True(result.Generated > result.Real * 0.9, $"{result.Generated} generated for {result.Real} real");
        Assert.InRange(result.RealLatencyMaxNs, 0, 30 * Ms);
        Assert.InRange(result.PipelineDelayMaxNs - result.PipelineDelayMinNs, 0, 9 * Ms);
    }

    private static (long, int, int) PrivateState(FrameScheduler scheduler)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(FrameScheduler);
        return (
            (long)type.GetField("_vrrBase", Flags)!.GetValue(scheduler)!,
            (int)type.GetField("_vrrK", Flags)!.GetValue(scheduler)!,
            (int)type.GetField("_vrrSlots", Flags)!.GetValue(scheduler)!);
    }
}
