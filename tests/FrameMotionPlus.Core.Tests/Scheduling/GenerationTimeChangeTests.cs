using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// A change of the generator's cost (a quality switch): <see cref="FrameScheduler.PrepareGenerationTimeChange"/> raises D
/// for a dearer generator before the decisions change, the commit restarts the estimate from the new figure.
/// </summary>
public class GenerationTimeChangeTests
{
    private const long Ms = Nanoseconds.PerMillisecond;

    [Fact]
    public void The_tuner_restarts_from_a_new_initial_estimate()
    {
        var tuner = new PipelineDelayTuner(100, 0.99, initialGenerationTimeNs: 2 * Ms, marginNs: Ms / 2,
            slewUpNsPerSecond: 50 * Ms, slewDownNsPerSecond: 5 * Ms, deadbandNs: Ms / 10);
        for (var i = 0; i < 100; i++)
        {
            tuner.AddGenerationSample(Ms);
        }

        Assert.Equal(Ms, tuner.GenerationTimeNs);
        tuner.Restart(6 * Ms);
        Assert.Equal(0, tuner.SampleCount);
        Assert.Equal(6 * Ms, tuner.GenerationTimeNs);
        tuner.AddGenerationSample(5 * Ms);
        Assert.Equal(6 * Ms, tuner.GenerationTimeNs); // never below the new start until enough samples
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Restart(-1));
    }

    [Fact]
    public void D_is_raised_for_a_dearer_generator_before_the_commit_and_the_lead_after_it()
    {
        var scheduler = new FrameScheduler(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = 1_600_000 });
        var t = (long)FrameRate.FromHz(30).PeriodNs;
        long now = 0, frame = 0, id = 0;
        void RunFor(long ns)
        {
            var end = now + ns;
            while (now < end)
            {
                while (frame <= now)
                {
                    scheduler.OnFrameArrived(new FrameArrival(id++, frame, frame));
                    frame += t;
                }

                var next = scheduler.NextDecisionTime(now);
                if (next > now)
                {
                    now = Math.Min(Math.Min(next, frame), now + 250_000);
                    continue;
                }

                if (scheduler.Schedule(now).Kind == SlotKind.Generated)
                {
                    scheduler.ReportGenerationTime(1_400_000);
                }

                now += 1_000;
            }
        }

        RunFor(2 * Nanoseconds.PerSecond);
        var d0 = scheduler.PipelineDelayNs;
        var lead0 = scheduler.Diagnostics.LeadNs;
        Assert.False(scheduler.GenerationTimeChangePending);
        scheduler.PrepareGenerationTimeChange(6_500_000);
        Assert.True(scheduler.GenerationTimeChangePending);
        Assert.False(scheduler.GenerationTimeChangeReady);
        RunFor(20 * Ms);
        Assert.Equal(lead0, scheduler.Diagnostics.LeadNs); // the decisions keep the current estimate
        Assert.True(scheduler.Diagnostics.DelayTargetNs >= d0 + (4 * Ms));
        RunFor(200 * Ms);
        Assert.True(scheduler.GenerationTimeChangeReady);
        Assert.True(scheduler.PipelineDelayNs >= d0 + (4 * Ms));
        scheduler.CommitGenerationTimeChange();
        Assert.False(scheduler.GenerationTimeChangePending);
        Assert.Equal(0, scheduler.GenerationSampleCount);
        RunFor(20 * Ms);
        Assert.True(scheduler.Diagnostics.LeadNs >= lead0 + (4 * Ms));

        // A cheaper generator is ready at once; a cancelled change lets D fall back.
        scheduler.PrepareGenerationTimeChange(1_000_000);
        RunFor(20 * Ms);
        Assert.True(scheduler.GenerationTimeChangeReady);
        scheduler.CancelGenerationTimeChange();
        Assert.False(scheduler.GenerationTimeChangeReady);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.PrepareGenerationTimeChange(-1));
    }
}
