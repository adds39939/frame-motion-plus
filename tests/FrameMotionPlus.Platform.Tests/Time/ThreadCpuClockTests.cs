using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Platform.Tests.Time;

/// <summary><see cref="ThreadCpuClock"/>: the calling thread's CPU time, which a sleeping thread does not accumulate.</summary>
public class ThreadCpuClockTests
{
    [Fact]
    public void Busy_time_counts_and_sleeping_does_not()
    {
        if (!ThreadCpuClock.IsAvailable)
        {
            Assert.Equal(-1, ThreadCpuClock.CurrentThreadNs);
            return;
        }

        // Spin for 50 ms of wall time: nearly all of it is this thread's CPU time (a busy machine may preempt it a little).
        var cpu0 = ThreadCpuClock.CurrentThreadNs;
        var wall0 = Monotonic.NowNs;
        while (Monotonic.NowNs - wall0 < 50 * Nanoseconds.PerMillisecond)
        {
        }

        var spun = ThreadCpuClock.CurrentThreadNs - cpu0;
        Assert.InRange(spun, 10 * Nanoseconds.PerMillisecond, 60 * Nanoseconds.PerMillisecond);

        // Sleeping 100 ms costs (almost) no CPU time.
        var cpu1 = ThreadCpuClock.CurrentThreadNs;
        Thread.Sleep(100);
        var slept = ThreadCpuClock.CurrentThreadNs - cpu1;
        Assert.InRange(slept, 0, 5 * Nanoseconds.PerMillisecond);
    }

    [Fact]
    public void Each_thread_has_its_own_clock()
    {
        if (!ThreadCpuClock.IsAvailable)
        {
            return;
        }

        // Another thread spins 50 ms while this one sleeps: this thread's clock barely moves.
        var mine0 = ThreadCpuClock.CurrentThreadNs;
        long theirs = 0;
        var other = new Thread(() =>
        {
            var c0 = ThreadCpuClock.CurrentThreadNs;
            var w0 = Monotonic.NowNs;
            while (Monotonic.NowNs - w0 < 50 * Nanoseconds.PerMillisecond)
            {
            }

            theirs = ThreadCpuClock.CurrentThreadNs - c0;
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        var mine = ThreadCpuClock.CurrentThreadNs - mine0;
        Assert.True(theirs >= 10 * Nanoseconds.PerMillisecond, $"the spinning thread used {theirs} ns");
        Assert.True(mine < theirs / 2, $"this thread used {mine} ns while the other used {theirs} ns");
    }
}
