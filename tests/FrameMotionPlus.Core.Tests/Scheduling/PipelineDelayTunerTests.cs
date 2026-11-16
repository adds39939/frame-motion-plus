using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class PipelineDelayTunerTests
{
    private const long Ms = Nanoseconds.PerMillisecond;
    private const long Second = Nanoseconds.PerSecond;

    private static PipelineDelayTuner Create(int window = 100) =>
        new(window, 0.99, initialGenerationTimeNs: 3 * Ms, marginNs: Ms / 2,
            slewUpNsPerSecond: 50 * Ms, slewDownNsPerSecond: 5 * Ms, deadbandNs: Ms / 10);

    [Fact]
    public void Uses_the_initial_estimate_until_d_is_measured_confidently()
    {
        var tuner = Create();
        Assert.Equal(3 * Ms, tuner.GenerationTimeNs);

        // A handful of samples cannot establish a p99: the estimate does not drop below the initial value yet...
        tuner.AddGenerationSample(2 * Ms);
        Assert.Equal(3 * Ms, tuner.GenerationTimeNs);

        // ...but slower samples raise it at once, and enough faster ones replace it.
        tuner.AddGenerationSample(5 * Ms);
        Assert.Equal(5 * Ms, tuner.GenerationTimeNs);
        for (var i = 0; i < 200; i++)
        {
            tuner.AddGenerationSample(2 * Ms);
        }

        Assert.Equal(2 * Ms, tuner.GenerationTimeNs);
    }

    [Fact]
    public void Tracks_the_rolling_p99_of_d_not_the_worst_case_ever_seen()
    {
        var tuner = Create(window: 100);
        for (var i = 0; i < 99; i++)
        {
            tuner.AddGenerationSample(2 * Ms);
        }

        tuner.AddGenerationSample(20 * Ms); // one outlier in a hundred is the p100, not the p99
        Assert.Equal(2 * Ms, tuner.GenerationTimeNs);

        tuner.AddGenerationSample(20 * Ms); // two in the window: now it is the p99
        Assert.Equal(20 * Ms, tuner.GenerationTimeNs);

        for (var i = 0; i < 100; i++)
        {
            tuner.AddGenerationSample(4 * Ms); // the outliers age out
        }

        Assert.Equal(4 * Ms, tuner.GenerationTimeNs);
    }

    [Fact]
    public void First_step_jumps_straight_to_the_target()
    {
        var tuner = Create();
        Assert.False(tuner.IsInitialized);
        Assert.Equal(12 * Ms, tuner.Step(12 * Ms, 0));
        Assert.True(tuner.IsInitialized);
    }

    [Fact]
    public void Rises_at_the_up_slew_rate_and_falls_at_the_down_slew_rate()
    {
        var tuner = Create();
        tuner.Reset(10 * Ms);
        var slot = 8_333_333L;

        // Up at 50 ms/s: about 0.417 ms per 120 Hz slot.
        var before = tuner.CurrentNs;
        tuner.Step(20 * Ms, slot);
        Assert.Equal(50 * Ms * slot / Second, tuner.CurrentNs - before);

        var steps = 0;
        while (tuner.CurrentNs < 20 * Ms)
        {
            var prev = tuner.CurrentNs;
            tuner.Step(20 * Ms, slot);
            Assert.InRange(tuner.CurrentNs - prev, 0, (50 * Ms * slot / Second) + 1);
            steps++;
        }

        Assert.Equal(20 * Ms, tuner.CurrentNs);
        Assert.InRange(steps, 22, 24);

        // Down at 5 ms/s, ten times slower, until it is within the deadband of the target.
        steps = 0;
        while (tuner.CurrentNs - (10 * Ms) > Ms / 10)
        {
            var prev = tuner.CurrentNs;
            tuner.Step(10 * Ms, slot);
            Assert.InRange(prev - tuner.CurrentNs, 1, (5 * Ms * slot / Second) + 1);
            steps++;
            Assert.True(steps < 1000);
        }

        Assert.InRange(steps, 236, 240);
        Assert.InRange(tuner.CurrentNs, 10 * Ms, (10 * Ms) + (Ms / 10));
    }

    [Fact]
    public void Ignores_small_decreases_inside_the_deadband()
    {
        var tuner = Create();
        tuner.Reset(10 * Ms);
        tuner.Step((10 * Ms) - (Ms / 20), Second);
        Assert.Equal(10 * Ms, tuner.CurrentNs);
        tuner.Step((10 * Ms) - (Ms / 5), Second);
        Assert.Equal((10 * Ms) - (Ms / 5), tuner.CurrentNs);
    }

    [Fact]
    public void Long_gaps_do_not_overflow_or_jump_more_than_ten_seconds_of_slew()
    {
        var tuner = Create();
        tuner.Reset(0);
        tuner.Step(long.MaxValue / 4, long.MaxValue);
        Assert.Equal(10 * 50 * Ms, tuner.CurrentNs);
    }

    [Fact]
    public void Slews_d_in_a_live_scheduler_after_a_step_in_generation_time()
    {
        var options = new SchedulerOptions
        {
            ClockMode = ClockMode.Vrr,
            DisplayRate = FrameRate.FromHz(120),
            InitialGenerationTimeNs = 2 * Ms,
            GenerationTimeWindow = 32,
        };
        var sim = new Simulation.SimulationOptions
        {
            DurationNs = 10 * Second,
            StatsStartNs = 0,
            GenerationTimeNs = 2 * Ms,
            GenerationTimeChangeAtNs = 5 * Second,
            GenerationTimeAfterChangeNs = 5 * Ms,
            Source = new Simulation.SimulatedSourceOptions { Rate = FrameRate.FromHz(60) },
        };
        var run = Support.SimHarness.Run(options, sim);
        var tNs = (long)Math.Round(FrameRate.FromHz(60).PeriodNs);

        var before = run.Records.Last(r => r.TargetDisplayNs < 5 * Second).PipelineDelayNs;
        var after = run.Records[^1].PipelineDelayNs;
        Assert.InRange(before, LatencyModel.AddedLatencyNs(tNs, 2, 2 * Ms) + (Ms / 2) - 2, LatencyModel.AddedLatencyNs(tNs, 2, 2 * Ms) + (Ms / 2) + 2);
        Assert.InRange(after, LatencyModel.AddedLatencyNs(tNs, 2, 5 * Ms) + (Ms / 2) - 2, LatencyModel.AddedLatencyNs(tNs, 2, 5 * Ms) + (Ms / 2) + 2);

        // D never moved faster than the up-slew limit.
        for (var i = 1; i < run.Records.Count; i++)
        {
            var dt = run.Records[i].DecisionNs - run.Records[i - 1].DecisionNs;
            var dd = run.Records[i].PipelineDelayNs - run.Records[i - 1].PipelineDelayNs;
            Assert.True(dd <= (50 * Ms * dt / Second) + 1, $"D rose {dd} ns in {dt} ns");
        }
    }
}
