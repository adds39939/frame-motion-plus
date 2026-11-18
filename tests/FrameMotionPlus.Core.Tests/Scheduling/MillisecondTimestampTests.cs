using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// Matroska keeps whole-millisecond timestamps: a 60 fps file's frames come 16, 17, 17 ms apart (docs/benchmarks/
/// phase4a-decode.md, "What the pipeline must know", 9), so the median interval is 17 ms, 2.04 refreshes of 120 Hz.
/// Found when the pipeline first played such a file (Phase 4b): with the multiplier capped at 2, the fallback rule took
/// ceil(2.04 / 2) = 2, a stride of 2 refreshes, and no generation at all until the phase locked and two one-second
/// dwells had passed, again after every loop or seek; a two-second looping clip never generated a frame.
/// </summary>
public class MillisecondTimestampTests
{
    private const long Ms = Nanoseconds.PerMillisecond;

    [Fact]
    public void Sixty_fps_with_millisecond_timestamps_is_generated_at_2x_from_the_start_and_across_a_loop()
    {
        var scheduler = new FrameScheduler(new SchedulerOptions
        {
            ClockMode = ClockMode.FixedRefresh,
            DisplayRate = FrameRate.FromHz(120),
            MaxMultiplier = 2,
            InitialGenerationTimeNs = 200_000,
        });

        // 4 s of frames at whole milliseconds (round(k * 1000 / 60) ms); a loop (discontinuity) at 2 s. As a file is played:
        // each frame is handed over a fixed lead (67 ms) before its content time, so the scheduler learns the rate as it
        // plays, from the start and again after the loop.
        const long Lead = 67 * Ms;
        var frames = new List<FrameArrival>();
        for (var k = 0; k < 240; k++)
        {
            var content = (long)Math.Round(k * 1000.0 / 60) * Ms + (k >= 120 ? 17 * Ms : 0);
            frames.Add(new FrameArrival(k, 100 * Ms + content, 100 * Ms + content - Lead, Discontinuity: k == 120));
        }

        var decisions = new List<SlotDecision>();
        var now = 0L;
        var next = 0;
        while (now < 4_000 * Ms)
        {
            while (next < frames.Count && frames[next].ContentTimeNs <= now + Lead)
            {
                Assert.True(scheduler.OnFrameArrived(frames[next++]));
            }

            var due = scheduler.NextDecisionTime(now);
            if (due > now)
            {
                now = due;
                continue;
            }

            decisions.Add(scheduler.Schedule(now));
        }

        // Generated frames in every half second once the first frame shows, the loop's half second included.
        var start = decisions.First(d => d.Kind == SlotKind.Real).TargetTimeNs;
        for (var from = start + (250 * Ms); from < 3_500 * Ms; from += 500 * Ms)
        {
            var window = decisions.Where(d => d.TargetTimeNs >= from && d.TargetTimeNs < from + (500 * Ms)).ToList();
            var generated = window.Count(d => d.Kind == SlotKind.Generated);
            Assert.True(generated >= 15, $"{generated} generated slots of {window.Count} between {(from - start) / 1e6:0} and {(from - start) / 1e6 + 500:0} ms after the start");
        }

        Assert.Equal(2, scheduler.Diagnostics.Multiplier);
        Assert.Equal(1, scheduler.Diagnostics.Stride);
    }
}
