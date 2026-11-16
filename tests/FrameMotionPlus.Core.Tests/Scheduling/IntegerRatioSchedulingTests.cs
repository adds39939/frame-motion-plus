using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Tests.Support;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class IntegerRatioSchedulingTests
{
    /// <summary>
    /// Asserts the steady-state output is exactly: real frame, then M − 1 generated frames at t = k/M between
    /// consecutive real frames, repeating; every slot shows content exactly one refresh after the previous one.
    /// </summary>
    private static void AssertIntegerPattern(List<FrameTimelineRecord> records, int m, double periodNs)
    {
        var start = records.FindIndex(r => r.Kind == SlotKind.Real);
        Assert.True(start >= 0, "no real frame in steady state");
        var groups = 0;
        for (var i = start; i + m <= records.Count; i += m)
        {
            var real = records[i];
            Assert.Equal(SlotKind.Real, real.Kind);
            Assert.Equal(real.FrameA, real.FrameB);
            Assert.Equal(0.0, real.Timestep);
            for (var k = 1; k < m; k++)
            {
                var g = records[i + k];
                Assert.Equal(SlotKind.Generated, g.Kind);
                Assert.Equal(real.FrameA, g.FrameA);
                Assert.Equal(real.FrameA + 1, g.FrameB);
                Assert.InRange(g.Timestep, ((double)k / m) - 1e-6, ((double)k / m) + 1e-6);
                Assert.Equal(g.IdealContentNs, g.ContentNs);
            }

            if (i + m < records.Count)
            {
                Assert.Equal(real.FrameA + 1, records[i + m].FrameA);
            }

            groups++;
        }

        Assert.True(groups > 10);
        for (var i = 1; i < records.Count; i++)
        {
            var step = records[i].ContentNs - records[i - 1].ContentNs;
            Assert.InRange(step, (long)Math.Floor(periodNs) - 1, (long)Math.Ceiling(periodNs) + 1);
        }
    }

    [Fact]
    public void Sixty_to_120_fixed_alternates_real_and_generated_at_half_with_real_frames_on_vblanks()
    {
        var run = Run(Fixed(120), Sim(FrameRate.FromHz(60), 20));
        var steady = run.From(2 * Second);

        AssertIntegerPattern(steady, 2, run.PeriodNs);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.Repeated);
        Assert.True(run.Result.Diagnostics.PhaseCoherent);
        Assert.Equal(2, run.Result.Diagnostics.Multiplier);

        // Model: hold (M−1)/M·T = 8.333, + p99(d) 2 + margin 0.5 = 10.833 ms; with the source phase at 0 the
        // smallest vblank-aligned delay at or above that (plus half the margin) is two refreshes.
        foreach (var r in steady.Where(r => r.Kind == SlotKind.Real))
        {
            Assert.InRange(r.ActualDisplayNs - r.ContentNs, 16_666_665, 16_666_668);
        }

        Assert.InRange(run.Result.RealLatencyMeanNs, 16_666_665, 16_666_668);
    }

    [Fact]
    public void Sixty_to_120_vrr_adds_exactly_the_model_latency_plus_margin()
    {
        var run = Run(Vrr(120), Sim(FrameRate.FromHz(60), 20));
        var steady = run.From(2 * Second);
        var t = FrameRate.FromHz(60).PeriodNs;

        var expected = LatencyModel.AddedLatencyNs((long)Math.Round(t), 2, 2 * Ms) + 500_000;
        Assert.InRange(run.Result.RealLatencyMeanNs, expected - 2, expected + 2);
        Assert.InRange(run.Result.RealLatencyMaxNs - run.Result.RealLatencyMinNs, 0, 2);
        Assert.Equal(0, run.Result.Missed);

        var start = steady.FindIndex(r => r.Kind == SlotKind.Real);
        for (var i = start; i + 1 < steady.Count; i += 2)
        {
            Assert.Equal(SlotKind.Real, steady[i].Kind);
            Assert.Equal(SlotKind.Generated, steady[i + 1].Kind);
            Assert.Equal(0.5, steady[i + 1].Timestep, 9);

            // VRR slots follow the source: V = ts + D + kT/M.
            Assert.InRange(steady[i + 1].TargetDisplayNs - steady[i].TargetDisplayNs, (long)(t / 2) - 1, (long)(t / 2) + 1);
        }
    }

    [Fact]
    public void Thirty_in_60_signal_detected_by_cadence_then_alternates_real_and_generated_at_half_on_60_hz()
    {
        // A 30 fps game inside a 60 Hz capture signal: every picture arrives twice.
        var signal = FrameRate.FromHz(60);
        var tracker = new CadenceTracker();
        var unique = new List<long>();
        for (var i = 0; i < 60 * 30; i++)
        {
            var ts = signal.TickTimeNs(i);
            var metric = i % 2 == 0 ? 0.05 : 0.0;
            var c = tracker.Process(ts, metric, isPq: false);
            if (c.IsUnique)
            {
                unique.Add(c.ContentTimeNs);
            }
        }

        Assert.InRange(tracker.EstimatedFrameRateHz, 29.99, 30.01);
        Assert.Equal(900, unique.Count);

        var sim = Sim(FrameRate.FromHz(30), 29) with
        {
            Source = new SimulatedSourceOptions { ExplicitTimestamps = unique.ToArray() },
        };
        var run = Run(Fixed(60), sim);
        var steady = run.From(2 * Second);

        AssertIntegerPattern(steady, 2, run.PeriodNs);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.Repeated);

        // Hold T/2 = 16.667 + 2.5 lead = 19.167 ms, rounded up to the vblank grid: 33.333 ms.
        Assert.InRange(run.Result.RealLatencyMeanNs, 33_333_332, 33_333_335);
    }

    [Fact]
    public void Forty_in_120_shows_real_then_thirds()
    {
        var run = Run(Fixed(120), Sim(FrameRate.FromHz(40), 20));
        var steady = run.From(2 * Second);

        AssertIntegerPattern(steady, 3, run.PeriodNs);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(3, run.Result.Diagnostics.Multiplier);

        // Hold 2/3·25 = 16.667 ms, matching the design doc's table row for 40 to 120.
        Assert.InRange(run.Result.Diagnostics.HoldNs, 16_666_665, 16_666_668);
    }

    [Fact]
    public void Thirty_to_120_shows_real_then_quarters()
    {
        var run = Run(Fixed(120), Sim(FrameRate.FromHz(30), 20));
        var steady = run.From(2 * Second);

        AssertIntegerPattern(steady, 4, run.PeriodNs);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(4, run.Result.Diagnostics.Multiplier);
        Assert.InRange(run.Result.Diagnostics.HoldNs, 24_999_999, 25_000_001);
    }

    [Fact]
    public void Thirty_to_120_vrr_matches_model_exactly()
    {
        var run = Run(Vrr(120), Sim(FrameRate.FromHz(30), 20));
        var tNs = (long)Math.Round(FrameRate.FromHz(30).PeriodNs);
        var expected = LatencyModel.AddedLatencyNs(tNs, 4, 2 * Ms) + 500_000;

        Assert.InRange(run.Result.RealLatencyMeanNs, expected - 2, expected + 2);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(4, run.Result.Diagnostics.Multiplier);

        // The actual output: real, then t = 1/4, 2/4, 3/4 between consecutive frames, slots T/4 apart.
        var steady = run.From(2 * Second);
        AssertIntegerPattern(steady, 4, FrameRate.FromHz(120).PeriodNs);
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, run.Result.DistinctTimesteps);
    }

    [Fact]
    public void Max_multiplier_caps_generation_on_a_fast_panel()
    {
        // 30 on 240 Hz is 8 slots per frame; with a cap of 4 each image is held for two refreshes.
        var options = Fixed(240) with { MaxMultiplier = 4 };
        var run = Run(options, Sim(FrameRate.FromHz(30), 10));
        var steady = run.From(2 * Second);

        Assert.Equal(2, run.Result.Diagnostics.Stride);
        Assert.Equal(4, run.Result.Diagnostics.Multiplier);
        var ts = steady.Where(r => r.Kind == SlotKind.Generated).Select(r => Math.Round(r.Timestep, 6)).Distinct().Order().ToArray();
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, ts);
        Assert.All(steady.Where(r => r.Kind == SlotKind.Repeat), r => Assert.Equal(SlotReason.Fallback, r.Reason));
        Assert.InRange(steady.Count(r => r.Kind == SlotKind.Repeat), (steady.Count / 2) - 1, (steady.Count / 2) + 1);
        Assert.Equal(0, run.Result.Missed);
    }

    [Fact]
    public void Source_phase_offset_moves_the_aligned_delay_but_real_frames_still_land_on_vblanks()
    {
        var sim = Sim(FrameRate.FromHz(60), 10) with
        {
            Source = new SimulatedSourceOptions { Rate = FrameRate.FromHz(60), StartNs = 3 * Ms },
        };
        var run = Run(Fixed(120), sim);
        var steady = run.From(2 * Second);

        AssertIntegerPattern(steady, 2, run.PeriodNs);

        // Base 10.833 + guard 0.25 = 11.083 ms; D must satisfy D ≡ −3 ms (mod 8.333): 13.667 ms.
        Assert.InRange(run.Result.RealLatencyMeanNs, 13_666_665, 13_666_669);
    }
}
