using System.Diagnostics;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Tests.Support;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Platform.Tests.Time;

public class MonotonicClockTests
{
    [Fact]
    public void Reads_never_go_backwards()
    {
        var previous = Monotonic.NowNs;
        for (var i = 0; i < 1_000_000; i++)
        {
            var now = Monotonic.NowNs;
            Assert.True(now >= previous, $"read {i}: {now} after {previous}");
            previous = now;
        }
    }

    [Fact]
    public void Reads_advance_and_are_positive()
    {
        var first = Monotonic.NowNs;
        Thread.Sleep(5);
        var second = Monotonic.NowNs;

        Assert.True(first > 0);
        Assert.True(second > first);
    }

    [Fact]
    public void Elapsed_time_is_in_nanoseconds_and_agrees_with_Stopwatch()
    {
        var sw1 = Stopwatch.GetTimestamp();
        var c1 = Monotonic.NowNs;
        Thread.Sleep(100);
        var c2 = Monotonic.NowNs;
        var sw2 = Stopwatch.GetTimestamp();

        var clockNs = c2 - c1;
        var stopwatchNs = (long)((sw2 - sw1) * (double)Nanoseconds.PerSecond / Stopwatch.Frequency);

        // The clock interval lies inside the Stopwatch interval; a unit error (us, ticks) would be off by 10x or more.
        Assert.InRange(clockNs, 90 * Nanoseconds.PerMillisecond, stopwatchNs + Nanoseconds.PerMillisecond);
        Assert.True(stopwatchNs - clockNs < Nanoseconds.PerMillisecond, $"clock {clockNs} ns vs Stopwatch {stopwatchNs} ns");
    }

    /// <summary>
    /// .NET's Stopwatch reads CLOCK_MONOTONIC on Linux (SystemNative_GetTimestamp), in nanoseconds. So if this clock is
    /// CLOCK_MONOTONIC too, the same domain V4L2 timestamps and Vulkan present timing use, any read lies between two
    /// Stopwatch reads taken around it. CLOCK_REALTIME or CLOCK_BOOTTIME would not.
    /// </summary>
    [Fact]
    public void Linux_reads_CLOCK_MONOTONIC_the_same_clock_as_Stopwatch()
    {
        TestPlatform.SkipUnlessLinux();
        Assert.Equal(Nanoseconds.PerSecond, Stopwatch.Frequency);
        Assert.True(Monotonic.IsClockMonotonic);

        for (var i = 0; i < 1000; i++)
        {
            var before = Stopwatch.GetTimestamp();
            var now = Monotonic.NowNs;
            var after = Stopwatch.GetTimestamp();
            Assert.InRange(now, before, after);
        }
    }

    [Fact]
    public void Linux_resolution_is_at_least_a_microsecond()
    {
        TestPlatform.SkipUnlessLinux();

        Assert.InRange(Monotonic.ResolutionNs, 1, Nanoseconds.PerMicrosecond);
        Assert.StartsWith("CLOCK_MONOTONIC", Monotonic.Source, StringComparison.Ordinal);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Calls through IClock are what is under test.")]
    public void MonotonicClock_is_an_IClock_on_the_same_time_base()
    {
        IClock clock = new MonotonicClock();

        var before = Monotonic.NowNs;
        var now = clock.NowNs;
        var after = Monotonic.NowNs;

        Assert.InRange(now, before, after);
        Assert.Same(DeadlineWaiter.Sleep, MonotonicClock.Shared.Waiter);
    }
}
