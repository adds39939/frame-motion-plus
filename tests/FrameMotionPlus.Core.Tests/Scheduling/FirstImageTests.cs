using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// The first image of a playback is a real frame (found in Phase 4b's review: on the virtual clock and on an output the
/// file's rate is not locked to, the first image was one generated between frames 0 and 1 at t 0.45 or 0.24, because τ
/// first reached the content somewhere inside the first interval).
/// </summary>
public class FirstImageTests
{
    private const long Ms = Nanoseconds.PerMillisecond;

    [Theory]
    [InlineData(120, 60.0, 3.1)]
    [InlineData(120, 59.94, 5.7)]
    [InlineData(240, 60.0, 11.3)]
    [InlineData(119.88, 60.0, 0.4)]
    public void The_first_slot_with_content_shows_the_first_frame_itself(double displayHz, double sourceFps, double phaseMs)
    {
        var scheduler = new FrameScheduler(new SchedulerOptions
        {
            ClockMode = ClockMode.FixedRefresh,
            DisplayRate = FrameRate.FromDouble(displayHz),
            MaxMultiplier = 2,
            InitialGenerationTimeNs = 200_000,
        });

        // Frames due from 100 ms + phase (any phase against the refreshes), all handed over at once (decoded ahead).
        var interval = 1e9 / sourceFps;
        for (var k = 0; k < 60; k++)
        {
            var content = (100 * Ms) + (long)(phaseMs * Ms) + (long)Math.Round(k * interval);
            Assert.True(scheduler.OnFrameArrived(new FrameArrival(k, content, 0, Discontinuity: false)));
        }

        var now = 0L;
        SlotDecision first = default;
        for (var i = 0; i < 1000; i++)
        {
            var due = scheduler.NextDecisionTime(now);
            now = Math.Max(now, due);
            var d = scheduler.Schedule(now);
            if (d.Kind != SlotKind.Empty)
            {
                first = d;
                break;
            }
        }

        Assert.Equal(SlotKind.Real, first.Kind);
        Assert.Equal(0, first.FrameA);
    }
}
