using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class NonIntegerRatioSchedulingTests
{
    private static void AssertEveryVblankShowsNewContent(List<FrameTimelineRecord> steady, double periodNs, double epsilonT)
    {
        // No doubled slots: every slot is a new real or generated image.
        Assert.All(steady, r => Assert.True(r.Kind is SlotKind.Real or SlotKind.Generated, $"slot {r.SlotIndex} is {r.Kind}/{r.Reason}"));

        for (var i = 1; i < steady.Count; i++)
        {
            // No missed slots: consecutive decisions are consecutive vblanks.
            var dv = steady[i].TargetDisplayNs - steady[i - 1].TargetDisplayNs;
            Assert.InRange(dv, (long)Math.Floor(periodNs), (long)Math.Ceiling(periodNs));

            // D is constant in steady state, so ideal content advances exactly with the display.
            Assert.InRange(steady[i].IdealContentNs - steady[i - 1].IdealContentNs, dv - 1, dv + 1);
        }

        foreach (var r in steady)
        {
            if (r.Kind == SlotKind.Generated)
            {
                Assert.Equal(r.IdealContentNs, r.ContentNs);
            }
            else
            {
                // Real frames are shown only when t is within ε of 0 or 1.
                Assert.InRange(Math.Abs(r.ContentNs - r.IdealContentNs), 0, epsilonT + 1);
            }
        }
    }

    [Fact]
    public void Ntsc_59_94_on_120_drifts_smoothly_through_every_timestep_with_no_missed_or_doubled_slots()
    {
        var source = FrameRate.FromDouble(59.94);
        Assert.Equal(new FrameRate(60000, 1001), source);

        var run = Run(Fixed(120), Sim(source, 600));
        var steady = run.From(2 * Second);
        var t = source.PeriodNs;
        var p = run.PeriodNs;

        Assert.False(run.Result.Diagnostics.PhaseCoherent);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.SkippedSlots);
        AssertEveryVblankShowsNewContent(steady, p, 0.02 * t);

        // Consecutive generated slots advance t by exactly P/T (mod 1): the timestep drifts, it never jumps.
        var expectedStep = p / t;
        var pairs = 0;
        for (var i = 1; i < steady.Count; i++)
        {
            var a = steady[i - 1];
            var b = steady[i];
            if (a.Kind != SlotKind.Generated || b.Kind != SlotKind.Generated)
            {
                continue;
            }

            var advance = b.Timestep + (b.FrameA - a.FrameA) - a.Timestep;
            Assert.InRange(advance, expectedStep - 1e-5, expectedStep + 1e-5);
            pairs++;
        }

        Assert.True(pairs > 60_000);

        // Over ten minutes the 0.06 Hz beat sweeps the timestep through the whole interval.
        var generated = steady.Where(r => r.Kind == SlotKind.Generated).Select(r => r.Timestep).ToArray();
        Assert.True(generated.Min() < 0.03);
        Assert.True(generated.Max() > 0.97);

        // Non-integer ratio: hold (1 − ε)·T, so D = (1 − ε)·T + p99(d) + margin (towards T + d).
        var expectedD = LatencyModel.NonIntegerFixedRefreshHoldNs((long)Math.Round(t), 0.02) + (2 * Ms) + 500_000;
        Assert.InRange(run.Result.PipelineDelayMinNs, expectedD - 2, expectedD + 2);
        Assert.InRange(run.Result.PipelineDelayMaxNs, expectedD - 2, expectedD + 2);
        Assert.InRange(run.Result.RealLatencyMinNs, expectedD - (0.02 * t) - 2, expectedD + (0.02 * t) + 2);
        Assert.InRange(run.Result.RealLatencyMaxNs, expectedD - (0.02 * t) - 2, expectedD + (0.02 * t) + 2);
    }

    [Fact]
    public void Thirty_to_144_repeats_its_timestep_cycle_every_24_slots_with_no_missed_or_doubled_slots()
    {
        var source = FrameRate.FromHz(30);
        var run = Run(Fixed(144), Sim(source, 30));
        var steady = run.From(2 * Second);

        Assert.False(run.Result.Diagnostics.PhaseCoherent);
        Assert.Equal(0, run.Result.Missed);
        AssertEveryVblankShowsNewContent(steady, run.PeriodNs, 0.02 * source.PeriodNs);

        // 144 / 30 = 24 / 5: the pattern of timesteps repeats every 24 slots, five source frames later.
        var compared = 0;
        for (var i = 0; i + 24 < steady.Count; i++)
        {
            var a = steady[i];
            var b = steady[i + 24];
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.FrameA + 5, b.FrameA);
            Assert.InRange(b.Timestep - a.Timestep, -1e-6, 1e-6);
            compared++;
        }

        Assert.True(compared > 3000);

        // Each generated t is the display-time rule: consecutive generated slots advance t by exactly P/T (mod 1),
        // checked on the scheduler's output rather than on the constants.
        var expectedStep = run.PeriodNs / source.PeriodNs;
        var pairs = 0;
        for (var i = 1; i < steady.Count; i++)
        {
            if (steady[i - 1].Kind == SlotKind.Generated && steady[i].Kind == SlotKind.Generated)
            {
                var advance = steady[i].Timestep + (steady[i].FrameA - steady[i - 1].FrameA) - steady[i - 1].Timestep;
                Assert.InRange(advance, expectedStep - 1e-6, expectedStep + 1e-6);
                pairs++;
            }
        }

        Assert.True(pairs > 3000);
        var tValues = steady.Take(24).Where(r => r.Kind == SlotKind.Generated).Select(r => r.Timestep).ToArray();
        Assert.All(tValues, v => Assert.InRange(v, 0.02, 0.98));
        Assert.True(tValues.Length >= 22);
    }

    [Fact]
    public void Ntsc_59_94_on_120_vrr_matches_the_model_with_no_drift_penalty()
    {
        var source = FrameRate.FromDouble(59.94);
        var run = Run(Vrr(120), Sim(source, 30));
        var tNs = (long)Math.Round(source.PeriodNs);
        var expected = LatencyModel.AddedLatencyNs(tNs, 2, 2 * Ms) + 500_000;

        Assert.InRange(run.Result.RealLatencyMeanNs, expected - 2, expected + 2);
        Assert.Equal(0, run.Result.Missed);
        Assert.Equal(0, run.Result.Repeated);
    }
}
