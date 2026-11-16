using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class FrameSchedulerBasicsTests
{
    [Fact]
    public void Slots_are_empty_until_the_startup_frames_arrive()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        var t = (long)FrameRate.FromHz(60).PeriodNs;
        long now = 0;
        for (var i = 0; i < 7; i++)
        {
            scheduler.OnFrameArrived(new FrameArrival(i, i * t, i * t));
            now = Math.Max(now, scheduler.NextDecisionTime(now));
            var d = scheduler.Schedule(now);
            Assert.Equal(SlotKind.Empty, d.Kind);
            Assert.Equal(SlotReason.Startup, d.Reason);
        }

        scheduler.OnFrameArrived(new FrameArrival(7, 7 * t, 7 * t));
        now = Math.Max(now, scheduler.NextDecisionTime(now));
        Assert.NotEqual(SlotKind.Empty, scheduler.Schedule(now).Kind);
    }

    [Fact]
    public void Frames_must_arrive_in_strictly_increasing_content_time()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        Assert.True(scheduler.OnFrameArrived(new FrameArrival(0, 1000, 1000)));
        Assert.False(scheduler.OnFrameArrived(new FrameArrival(1, 1000, 1000)));
        Assert.False(scheduler.OnFrameArrived(new FrameArrival(2, 500, 1000)));
        Assert.True(scheduler.OnFrameArrived(new FrameArrival(3, 2000, 2000)));
        Assert.Equal(2, scheduler.Counters.FramesRejected);
        Assert.Equal(2, scheduler.Counters.FramesAccepted);
    }

    [Fact]
    public void Late_caller_skips_vblanks_and_reports_them()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        var t = (long)FrameRate.FromHz(60).PeriodNs;
        for (var i = 0; i < 10; i++)
        {
            scheduler.OnFrameArrived(new FrameArrival(i, i * t, i * t));
        }

        var first = scheduler.Schedule(scheduler.NextDecisionTime(9 * t));
        var late = scheduler.Schedule(first.TargetTimeNs + (3 * (long)FrameRate.FromHz(120).PeriodNs) + 1);
        Assert.Equal(3, late.SkippedSlots);
        Assert.Equal(first.SlotIndex + 1, late.SlotIndex);
        Assert.True(late.TargetTimeNs > first.TargetTimeNs + (3 * 8_333_333));
    }

    [Fact]
    public void Oldest_referenced_frame_advances_with_content_time()
    {
        var run = Run(Fixed(120), Sim(FrameRate.FromHz(60), 5));
        var oldest = run.Scheduler.OldestReferencedFrameId;
        var last = run.Records[^1];
        Assert.Equal(last.FrameA, oldest);
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { MaxMultiplier = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { SnapEpsilon = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { TimelineCapacity = 48 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { HoldIntervalFactor = 1.0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new SchedulerOptions { DisplayRate = default }));
        Assert.Throws<ArgumentNullException>(() => new FrameScheduler(null!));
    }

    [Fact]
    public void Display_timing_update_reanchors_the_vblank_grid()
    {
        var scheduler = new FrameScheduler(Fixed(120));
        var t = (long)FrameRate.FromHz(60).PeriodNs;
        for (var i = 0; i < 10; i++)
        {
            scheduler.OnFrameArrived(new FrameArrival(i, i * t, i * t));
        }

        var a = scheduler.Schedule(scheduler.NextDecisionTime(9 * t));

        // Present feedback says the panel's vblanks are actually 1 ms later than assumed. The slot just scheduled
        // was really the vblank at the anchor, so the next slot is the one after it, not the anchor again.
        var anchor = a.TargetTimeNs + 1_000_000;
        scheduler.UpdateDisplayTiming(anchor, FrameRate.FromHz(120));
        var b = scheduler.Schedule(Math.Max(a.DecisionTimeNs, scheduler.NextDecisionTime(a.DecisionTimeNs)));
        var c = scheduler.Schedule(Math.Max(b.DecisionTimeNs, scheduler.NextDecisionTime(b.DecisionTimeNs)));
        Assert.Equal(anchor + FrameRate.FromHz(120).TickTimeNs(1), b.TargetTimeNs);
        Assert.Equal(anchor + FrameRate.FromHz(120).TickTimeNs(2), c.TargetTimeNs);
    }
}
