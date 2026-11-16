using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class FallbackSchedulingTests
{
    [Fact]
    public void Thirty_to_120_drops_to_2x_when_d_exceeds_the_4x_budget()
    {
        // d = 10 ms > T/4 = 8.33 ms: 4x cannot keep up, 2x (budget 16.7 ms) can.
        var run = Run(Fixed(120, initialDNs: 10 * Ms), Sim(FrameRate.FromHz(30), 20, dNs: 10 * Ms));
        var steady = run.From(2 * Second);

        Assert.Equal(2, run.Result.Diagnostics.Stride);
        Assert.Equal(2, run.Result.Diagnostics.Multiplier);
        Assert.Equal(0, run.Result.Missed);

        // real, (real held), t = 0.5, (t = 0.5 held), ...: each image is shown for two refreshes.
        var start = steady.FindIndex(r => r.Kind == SlotKind.Real);
        for (var i = start; i + 4 <= steady.Count; i += 4)
        {
            Assert.Equal(SlotKind.Real, steady[i].Kind);
            Assert.Equal(SlotKind.Repeat, steady[i + 1].Kind);
            Assert.Equal(SlotKind.Generated, steady[i + 2].Kind);
            Assert.InRange(steady[i + 2].Timestep, 0.5 - 1e-6, 0.5 + 1e-6);
            Assert.Equal(SlotKind.Repeat, steady[i + 3].Kind);
            Assert.Equal(SlotReason.Fallback, steady[i + 1].Reason);
            Assert.Equal(SlotReason.Fallback, steady[i + 3].Reason);
        }

        // Generations are two refreshes (16.7 ms) apart, so the 10 ms GPU job always finishes in time.
        var gens = steady.Where(r => r.Kind == SlotKind.Generated).ToList();
        for (var i = 1; i < gens.Count; i++)
        {
            Assert.True(gens[i].GenerationStartNs >= gens[i - 1].GenerationEndNs);
            Assert.True(gens[i].GenerationEndNs <= gens[i].TargetDisplayNs);
        }

        // Latency follows the model at M = 2: hold 16.667 + d 10 + margin 0.5, rounded up to the vblank grid.
        Assert.InRange(run.Result.Diagnostics.HoldNs, 16_666_665, 16_666_668);
        Assert.InRange(run.Result.RealLatencyMeanNs, 33_333_332, 33_333_335);
    }

    [Fact]
    public void Vrr_thirty_to_120_drops_to_3x_when_d_exceeds_the_4x_budget()
    {
        var run = Run(Vrr(120, initialDNs: 10 * Ms), Sim(FrameRate.FromHz(30), 20, dNs: 10 * Ms));
        var steady = run.From(2 * Second);
        var tNs = (long)Math.Round(FrameRate.FromHz(30).PeriodNs);

        Assert.Equal(3, run.Result.Diagnostics.Multiplier);
        Assert.Equal(3, LatencyModel.MaxMultiplierThatFits(tNs, 10 * Ms + 500_000));
        Assert.Equal(0, run.Result.Missed);
        var ts = steady.Where(r => r.Kind == SlotKind.Generated).Select(r => Math.Round(r.Timestep, 6)).Distinct().Order().ToArray();
        Assert.Equal(new[] { Math.Round(1.0 / 3, 6), Math.Round(2.0 / 3, 6) }, ts);

        var expected = LatencyModel.AddedLatencyNs(tNs, 3, 10 * Ms) + 500_000;
        Assert.InRange(run.Result.RealLatencyMeanNs, expected - 2, expected + 2);
    }

    [Fact]
    public void Generation_turns_off_when_no_multiplier_fits()
    {
        // 60 to 120 with d = 10 ms > T/2: show real frames only, at their own cadence.
        var run = Run(Fixed(120, initialDNs: 10 * Ms), Sim(FrameRate.FromHz(60), 10, dNs: 10 * Ms));
        var steady = run.From(2 * Second);

        Assert.False(run.Result.Diagnostics.GenerationEnabled);
        Assert.Equal(0, run.Result.Generated);
        Assert.Equal(0, run.Result.Missed);
        var start = steady.FindIndex(r => r.Kind == SlotKind.Real);
        for (var i = start; i + 2 <= steady.Count; i += 2)
        {
            Assert.Equal(SlotKind.Real, steady[i].Kind);
            Assert.Equal(SlotKind.Repeat, steady[i + 1].Kind);
            Assert.Equal(SlotReason.Fallback, steady[i + 1].Reason);
        }

        // Without generation the delay collapses to the margin, rounded up to the next vblank.
        Assert.InRange(run.Result.RealLatencyMeanNs, 8_333_332, 8_333_335);
    }

    [Fact]
    public void Recovers_full_multiplier_after_d_improves_and_the_dwell_passes()
    {
        var options = Fixed(120, initialDNs: 10 * Ms) with { GenerationTimeWindow = 64, FallbackDwellNs = Second };
        var sim = Sim(FrameRate.FromHz(30), 30, dNs: 10 * Ms) with
        {
            GenerationTimeChangeAtNs = 10 * Second,
            GenerationTimeAfterChangeNs = 3 * Ms,
        };
        var run = Run(options, sim);

        var before = run.Records.Where(r => r.TargetDisplayNs is > 2 * Second and < 10 * Second && r.Kind == SlotKind.Generated).ToList();
        var after = run.Records.Where(r => r.TargetDisplayNs > 20 * Second && r.Kind == SlotKind.Generated).ToList();
        Assert.All(before, r => Assert.InRange(r.Timestep, 0.5 - 1e-6, 0.5 + 1e-6));
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, after.Select(r => Math.Round(r.Timestep, 6)).Distinct().Order().ToArray());
        Assert.Equal(1, run.Result.Diagnostics.Stride);
        Assert.Equal(0, run.Result.Missed);
    }

    [Fact]
    public void Falls_back_immediately_when_d_gets_worse()
    {
        var options = Fixed(120, initialDNs: 3 * Ms) with { GenerationTimeWindow = 64 };
        var sim = Sim(FrameRate.FromHz(30), 20, dNs: 3 * Ms) with
        {
            GenerationTimeChangeAtNs = 10 * Second,
            GenerationTimeAfterChangeNs = 10 * Ms,
        };
        var run = Run(options, sim);

        Assert.Equal(2, run.Result.Diagnostics.Stride);

        // Only the generation that discovered the slower d can be late.
        Assert.InRange(run.Result.Missed, 0, 2);
        Assert.All(run.Records.Where(r => r.TargetDisplayNs > 11 * Second && r.Kind == SlotKind.Generated),
            r => Assert.InRange(r.Timestep, 0.5 - 1e-6, 0.5 + 1e-6));
    }
}
