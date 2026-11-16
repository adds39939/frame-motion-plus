using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// The <c>fmp simulate --check</c> verdict: derived from the design doc and the configuration only, it must pass
/// correct behaviour (including cases the old check wrongly failed) and fail on the reviewer's mutations.
/// </summary>
public class SimulationCheckTests
{
    private static (SchedulerOptions Options, SimulationOptions Sim, SimulationResult Result) Simulate(string mode, double fps, long hz, long dNs = 2 * Ms)
    {
        var rate = FrameRate.FromDouble(fps);
        var options = (mode == "vrr" ? Vrr(hz, initialDNs: dNs) : Fixed(hz, initialDNs: dNs));
        var sim = Sim(rate, 20, dNs);
        return (options, sim, RunCounting(options, sim));
    }

    [Theory]
    [InlineData("fixed", 60.0, 120L, 2_000_000L)]
    [InlineData("vrr", 60.0, 120L, 2_000_000L)]
    [InlineData("fixed", 30.0, 60L, 2_000_000L)]
    [InlineData("fixed", 30.0, 120L, 2_000_000L)]
    [InlineData("vrr", 30.0, 120L, 2_000_000L)]
    [InlineData("fixed", 40.0, 120L, 2_000_000L)]
    [InlineData("fixed", 59.94, 120L, 2_000_000L)]
    [InlineData("vrr", 59.94, 120L, 2_000_000L)]
    [InlineData("fixed", 30.0, 144L, 2_000_000L)]
    [InlineData("fixed", 120.0, 60L, 2_000_000L)]
    [InlineData("fixed", 144.0, 120L, 2_000_000L)]
    [InlineData("vrr", 144.0, 120L, 2_000_000L)]
    [InlineData("fixed", 24.0, 60L, 2_000_000L)]
    [InlineData("fixed", 50.0, 120L, 2_000_000L)]
    [InlineData("fixed", 60.0, 60L, 2_000_000L)]
    [InlineData("fixed", 30.0, 120L, 10_000_000L)] // integer fallback to 2x
    [InlineData("vrr", 30.0, 120L, 10_000_000L)]   // VRR fallback to 3x
    [InlineData("fixed", 60.0, 120L, 10_000_000L)] // nothing fits: real frames only
    [InlineData("fixed", 30.0, 144L, 8_000_000L)]  // non-integer fallback
    public void Correct_behaviour_passes(string mode, double fps, long hz, long dNs)
    {
        var (options, sim, result) = Simulate(mode, fps, hz, dNs);
        var verdict = SimulationCheck.Evaluate(options, sim, result);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.True(verdict.Strict);
    }

    [Theory]
    [InlineData("fixed", 60.0)]
    [InlineData("fixed", 30.0)]
    [InlineData("vrr", 60.0)]
    public void A_scheduler_that_never_generates_fails(string mode, double fps)
    {
        var (options, sim, result) = Simulate(mode, fps, 120);
        var mutated = result with
        {
            Real = result.Real,
            Generated = 0,
            Repeated = mode == "vrr" ? 0 : result.Slots - result.Real,
            DistinctTimesteps = [],
        };
        var verdict = SimulationCheck.Evaluate(options, sim, mutated);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("multiplier 1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_wrong_set_of_timesteps_fails()
    {
        var (options, sim, result) = Simulate("vrr", 30, 120);
        var verdict = SimulationCheck.Evaluate(options, sim, result with { DistinctTimesteps = [0.5] });
        Assert.False(verdict.Passed);

        verdict = SimulationCheck.Evaluate(options, sim, result with { DistinctTimesteps = [0.25, 0.5, 0.7] });
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("are not {k/4}", StringComparison.Ordinal));
    }

    [Fact]
    public void Inconsistent_or_mirrored_timesteps_fail()
    {
        var (options, sim, result) = Simulate("fixed", 30, 120);
        var verdict = SimulationCheck.Evaluate(options, sim, result with { TimestepInconsistencies = 1 });
        Assert.False(verdict.Passed);
    }

    [Fact]
    public void Correctness_counters_are_enforced_even_on_non_ideal_inputs()
    {
        var rate = FrameRate.FromHz(60);
        var sim = Sim(rate, 20) with { Source = new SimulatedSourceOptions { Rate = rate, DropProbability = 0.05 } };
        var options = Fixed(120);
        var result = RunCounting(options, sim);

        var verdict = SimulationCheck.Evaluate(options, sim, result);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.False(verdict.Strict);

        Assert.False(SimulationCheck.Evaluate(options, sim, result with { Missed = 1 }).Passed);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { ContentRegressions = 1 }).Passed);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { CausalityViolations = 1 }).Passed);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { TimestepInconsistencies = 1 }).Passed);
    }

    [Fact]
    public void Latency_outside_the_doc_band_fails()
    {
        var (options, sim, result) = Simulate("vrr", 60, 120);
        var verdict = SimulationCheck.Evaluate(options, sim, result);
        Assert.True(verdict.Passed);

        // VRR 60 to 120: dL = 2 + 8.333 ms; the band is [dL, dL + margin].
        Assert.InRange(verdict.LatencyLowerNs, 10_333_000, 10_334_000);
        Assert.InRange(verdict.LatencyUpperNs, 10_833_000, 10_884_000);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { ShownLatencyMaxNs = 12 * Ms }).Passed);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { ShownLatencyMinNs = 9 * Ms }).Passed);
    }

    [Fact]
    public void Repeats_or_starvation_on_ideal_inputs_fail()
    {
        var (options, sim, result) = Simulate("fixed", 60, 120);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { StarvedSlots = 3 }).Passed);
        Assert.False(SimulationCheck.Evaluate(options, sim, result with { Repeated = 50 }).Passed);
    }
}
