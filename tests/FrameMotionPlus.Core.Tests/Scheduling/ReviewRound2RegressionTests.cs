using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Tests.Support;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>Regression tests for the second scheduler review (N1 to N10, plus two round-1 guards no test protected).</summary>
public class ReviewRound2RegressionTests
{
    private static readonly FrameRate F60 = FrameRate.FromHz(60);
    private static readonly FrameRate F30 = FrameRate.FromHz(30);

    private static SchedulerOptions Mode(string mode, long hz = 120, long initialDNs = 2 * Ms) =>
        mode == "vrr" ? Vrr(hz, initialDNs: initialDNs) : Fixed(hz, initialDNs: initialDNs);

    // ---- N1: re-anchoring while stride > 1 blocked generation for as long as the app had been running ----

    [Theory]
    [InlineData(50_000L)]
    [InlineData(-50_000L)]
    public void N1_reanchoring_while_stride_is_two_keeps_generating(long shiftNs)
    {
        var sim = Sim(F30, 45, dNs: 10 * Ms) with { ReanchorAtNs = 20 * Second, ReanchorShiftNs = shiftNs };
        var run = Run(Fixed(120, initialDNs: 10 * Ms), sim);
        var after = run.Records.Where(r => r.TargetDisplayNs is > 20_500_000_000 and < 40 * Second).ToList();

        Assert.Equal(2, run.Result.Diagnostics.Stride);
        Assert.InRange(after.Count(r => r.Kind == SlotKind.Generated), after.Count(r => r.Kind == SlotKind.Real) - 2, after.Count(r => r.Kind == SlotKind.Real) + 2);
        Assert.Equal(0, run.Result.Missed);
    }

    // ---- N2: hold detection locked on when the rate fell to an alternating two-valued cadence ----

    [Theory]
    [InlineData("fixed", 60, 3, 4)]
    [InlineData("vrr", 60, 3, 4)]
    [InlineData("fixed", 120, 3, 4)]
    [InlineData("vrr", 120, 3, 4)]
    [InlineData("fixed", 120, 3, 5)]
    [InlineData("fixed", 120, 4, 5)] // ratio exactly 1.25
    public void N2_an_alternating_cadence_reseeds_instead_of_holding(string mode, long baseFps, int a, int b)
    {
        var source = new SimulatedSourceOptions
        {
            Rate = FrameRate.FromHz(baseFps),
            RateChangeAtNs = 5 * Second,
            CadenceAfterChange = new[] { a, b },
        };
        var run = RunCounting(Mode(mode), Sim(FrameRate.FromHz(baseFps), 60) with { Source = source, StatsStartNs = 40 * Second });

        Assert.Equal(1, run.Counters.Reseeds);
        Assert.InRange(run.Counters.HoldsDetected, 0, 2);
        Assert.Equal(0, run.HoldSlots + run.StarvedSlots + run.SkippedSlots);
        Assert.True(run.Generated > run.Real, $"{run.Generated} generated for {run.Real} real");
        Assert.Equal(0, run.Missed);
    }

    // ---- N3: fixed refresh emitted Generated even when the decision was too late for its vblank ----

    [Fact]
    public void N3_first_decision_after_a_stall_never_misses()
    {
        var sim = Sim(F60, 20, dNs: 6 * Ms) with { StallAtNs = 10 * Second, StallDurationNs = 403 * Ms, StatsStartNs = 9 * Second };
        var result = RunCounting(Fixed(120, initialDNs: 6 * Ms), sim);
        Assert.Equal(0, result.Missed);
    }

    [Fact]
    public void N3_a_short_hiccup_never_misses()
    {
        // The generated slot at 10.008333 s is normally decided at V − 2.5 ms; a 1.8 ms hiccup moves that to V − 1 ms,
        // too late for a 2 ms generation. It must show the real frame, not present late.
        var sim = Sim(F60, 20) with { StallAtNs = 10_005_500_000, StallDurationNs = 1_800_000, StatsStartNs = 9 * Second };
        var result = RunCounting(Fixed(120), sim);
        Assert.Equal(0, result.Missed);
        Assert.True(result.LateSlots >= 1, "the late decision was not exercised");
    }

    [Fact]
    public void N3_generation_switching_back_on_after_a_spike_never_misses()
    {
        var sim = Sim(F60, 40) with
        {
            GenerationTimeSpikeFromNs = 5 * Second,
            GenerationTimeSpikeUntilNs = 6 * Second,
            GenerationTimeSpikeNs = 9 * Ms,
            StatsStartNs = 7 * Second,
        };
        var result = RunCounting(Fixed(120), sim);
        Assert.Equal(0, result.Missed);
        Assert.True(result.Generated > 1000);
    }

    // ---- N4: the generation gate flapped near a 1:1 ratio ----

    [Theory]
    [InlineData(60L)]
    [InlineData(120L)]
    public void N4_generation_gate_does_not_flap_near_one_to_one(long hz)
    {
        var rate = FrameRate.FromHz(hz);
        var source = new SimulatedSourceOptions { Rate = rate, DropProbability = 0.1, TimestampJitterNs = 2 * Ms, Seed = 5 };
        var result = RunCounting(Fixed(hz), Sim(rate, 60) with { Source = source, StatsStartNs = 3 * Second });

        Assert.Equal(0, result.Missed);
        Assert.InRange(result.PipelineDelayMaxNs - result.PipelineDelayMinNs, 0, (long)rate.PeriodNs);
    }

    // ---- N5: fixed refresh never learnt a lateness that made every decision skip ----

    [Theory]
    [InlineData(2_000_000L, 3_000_000L, true)]
    [InlineData(8_000_000L, 700_000L, false)]
    public void N5_fixed_refresh_learns_wake_lateness_even_when_every_decision_skips(long dNs, long wakeNs, bool generates)
    {
        var sim = Sim(F60, 30, dNs) with { WakeLatenessNs = wakeNs, StatsStartNs = 5 * Second };
        var result = RunCounting(Fixed(120, initialDNs: dNs), sim);

        Assert.Equal(wakeNs, result.Diagnostics.WakeLatenessNs);
        Assert.Equal(0, result.SkippedSlots);
        Assert.Equal(0, result.Missed);
        Assert.Equal(generates, result.Generated > result.Real / 2);
    }

    // ---- N6: start-up livelock when every frame delivery pushed the first decision a vblank further ----

    [Fact]
    public void N6_the_first_decision_is_not_pushed_away_by_frame_deliveries()
    {
        var sim = Sim(F60, 20) with { WakeLatenessNs = 600_000 };
        var result = RunCounting(Fixed(60), sim);
        Assert.InRange(result.Real, 1100, 1200);
        Assert.Equal(0, result.Missed);
    }

    // ---- N7: a persistently slow generator was retried at a fixed period and every retry missed ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void N7_persistent_slow_generation_backs_off_and_retries_never_miss(string mode)
    {
        var sim = Sim(F60, 125) with { GenerationTimeChangeAtNs = 5 * Second, GenerationTimeAfterChangeNs = 9 * Ms, StatsStartNs = 20 * Second };
        var result = RunCounting(Mode(mode), sim);

        Assert.Equal(0, result.Missed);
        Assert.InRange(result.Generated, 1, 6); // retries at roughly 5, 10, 20, 40 s intervals: a handful in 105 s
    }

    [Fact]
    public void N7_tuner_doubles_the_retry_interval_after_each_failed_probe_and_resets_after_a_good_one()
    {
        var tuner = new PipelineDelayTuner(64, 0.99, 2 * Ms, Ms / 2, 50 * Ms, 5 * Ms, Ms / 10, maxSampleAgeNs: 5 * Second);
        tuner.AddGenerationSample(9 * Ms, 0);
        tuner.Expire(6 * Second); // all expired: a retry with the initial estimate, budgeted with the last known one
        Assert.Equal(2 * Ms, tuner.GenerationTimeNs);
        Assert.Equal(9 * Ms, tuner.GenerationLeadNs);

        tuner.AddGenerationSample(9 * Ms, 6 * Second); // the probe is still slow
        Assert.Equal(10 * Second, tuner.MaxSampleAgeNs);
        tuner.Expire(17 * Second);
        tuner.AddGenerationSample(9 * Ms, 17 * Second);
        Assert.Equal(20 * Second, tuner.MaxSampleAgeNs);
        tuner.Expire(38 * Second);
        tuner.AddGenerationSample(2 * Ms, 38 * Second); // finally fast again
        Assert.Equal(5 * Second, tuner.MaxSampleAgeNs);
    }

    // ---- N8: a discontinuity zeroed the hold interval ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void N8_a_cut_followed_by_a_pause_keeps_the_delay(string mode)
    {
        var source = new SimulatedSourceOptions
        {
            Rate = F60,
            DiscontinuityAtNs = 10 * Second,
            Holds = new[] { new SimulatedHold(10 * Second - Ms, 3 * Second) },
        };
        var reference = RunCounting(Mode(mode), Sim(F60, 9) with { StatsStartNs = 5 * Second });
        var run = Run(Mode(mode), Sim(F60, 20) with { Source = source, StatsStartNs = 5 * Second });

        // D stays through the cut and the pause, so nothing starves once frames resume (the few starved slots at the
        // very start of the pause are its onset, before the gap is long enough to be a hold).
        Assert.InRange(run.Result.PipelineDelayMinNs, reference.PipelineDelayMinNs - 2, long.MaxValue);
        Assert.Equal(0, run.Records.Count(r => r.TargetDisplayNs > 13 * Second && r.Reason == SlotReason.Starved));
        Assert.Equal(0, run.Result.Missed);
    }

    // ---- N9: the causality test must catch removed arrival guards ----

    [Theory]
    [InlineData("fixed")]
    [InlineData("vrr")]
    public void N9_frames_delivered_ahead_are_never_used_before_they_arrive(string mode)
    {
        var rate = FrameRate.FromDouble(59.94);
        var source = new SimulatedSourceOptions
        {
            Rate = rate,
            IngestJitterNs = 12 * Ms,
            DropProbability = 0.1,
            TimestampJitterNs = 2 * Ms,
            Seed = 1,
        };
        var sim = Sim(rate, 60) with { Source = source, DeliverAheadNs = 100 * Ms };
        var result = RunCounting(Mode(mode), sim);

        Assert.Equal(0, result.CausalityViolations);
        Assert.Equal(0, result.TimestepInconsistencies);
        Assert.Equal(0, result.Missed);
        Assert.True(result.Generated > 0);
    }

    // ---- N10: few samples made p99(d) the maximum of a handful ----

    [Fact]
    public void N10_heavy_combined_jitter_on_vrr_never_misses()
    {
        var source = new SimulatedSourceOptions { Rate = F60, DropProbability = 0.02, TimestampJitterNs = Ms, ClockSkewPpm = -80, Seed = 21 };
        var sim = Sim(F60, 60) with { Source = source, GenerationJitterNs = 500_000 };
        var result = RunCounting(Vrr(120), sim);
        Assert.Equal(0, result.Missed);
    }

    [Fact]
    public void N10_few_samples_never_lower_the_estimate_below_the_warm_up_value()
    {
        var tuner = new PipelineDelayTuner(256, 0.99, 3 * Ms, Ms / 2, 50 * Ms, 5 * Ms, Ms / 10);
        for (var i = 0; i < PipelineDelayTuner.ConfidentSampleCount - 1; i++)
        {
            tuner.AddGenerationSample(Ms);
        }

        Assert.Equal(3 * Ms, tuner.GenerationTimeNs);
        tuner.AddGenerationSample(Ms);
        Assert.Equal(Ms, tuner.GenerationTimeNs);
    }

    [Fact]
    public void N10_a_step_in_generation_time_shows_within_two_samples()
    {
        var tuner = new PipelineDelayTuner(256, 0.99, 2 * Ms, Ms / 2, 50 * Ms, 5 * Ms, Ms / 10);
        for (var i = 0; i < 256; i++)
        {
            tuner.AddGenerationSample(2 * Ms);
        }

        tuner.AddGenerationSample(10 * Ms);
        Assert.Equal(2 * Ms, tuner.GenerationTimeNs); // one outlier is ignored
        tuner.AddGenerationSample(10 * Ms);
        Assert.Equal(10 * Ms, tuner.GenerationTimeNs); // a step is followed
    }

    // ---- Round-1 guards that no test protected ----

    [Theory]
    [InlineData(30.0, 144L, 13_000_000L, 7_000_000L, 200)]
    [InlineData(23.976, 120L, 16_000_000L, 9_000_000L, 500)]
    public void Guard_generations_stay_a_stride_apart_while_D_falls_fast(double fps, long hz, long d0, long d1, long slewDownMsPerSecond)
    {
        var rate = FrameRate.FromDouble(fps);
        var options = Fixed(hz, initialDNs: d0) with { SlewDownNsPerSecond = slewDownMsPerSecond * Ms };
        var sim = Sim(rate, 40, d0) with { GenerationTimeChangeAtNs = 10 * Second, GenerationTimeAfterChangeNs = d1 };
        var run = Run(options, sim);
        var p = FrameRate.FromHz(hz).PeriodNs;
        var gens = run.From(2 * Second).Where(r => r.Kind == SlotKind.Generated).ToList();

        Assert.Equal(2, run.Result.Diagnostics.Stride);
        for (var i = 1; i < gens.Count; i++)
        {
            Assert.True(gens[i].TargetDisplayNs - gens[i - 1].TargetDisplayNs >= (2 * p) - (p / 2),
                $"generations at {gens[i - 1].TargetDisplayNs} and {gens[i].TargetDisplayNs} are less than a stride apart");
        }

        Assert.Equal(0, run.Result.Missed);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(1.0, false)]
    [InlineData(1.0 - 1e-9, false)] // rounds to 1.0f
    [InlineData(1e-50, false)]     // rounds to 0.0f
    [InlineData(-0.25, false)]
    [InlineData(0.5, true)]
    [InlineData(1e-6, true)]
    [InlineData(0.9999, true)]
    public void Guard_timesteps_that_round_to_zero_or_one_as_float_are_never_generated(double t, bool generatable)
    {
        Assert.Equal(generatable, FrameScheduler.IsGeneratableTimestep(t));
    }
}
