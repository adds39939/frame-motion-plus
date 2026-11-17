using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Interop;

namespace FrameMotionPlus.Platform.Time;

/// <summary>
/// The app's one time base: <c>long</c> nanoseconds on <c>CLOCK_MONOTONIC</c>, the clock V4L2 buffer timestamps and
/// Vulkan present timing report on Linux. Read with <c>clock_gettime</c> (vDSO, no syscall, no GC transition).
/// Elsewhere it falls back to <see cref="Stopwatch"/> converted to nanoseconds (a monotonic clock, but not the same
/// domain as anything outside the process).
/// </summary>
public static class Monotonic
{
    private static readonly long StopwatchFrequency = Stopwatch.Frequency;

    /// <summary>True when <see cref="NowNs"/> reads <c>CLOCK_MONOTONIC</c> directly (64-bit Linux).</summary>
    public static bool IsClockMonotonic => NativePlatform.IsLinux64;

    /// <summary>Human-readable name of the clock behind <see cref="NowNs"/>.</summary>
    public static string Source => NativePlatform.IsLinux64 ? "CLOCK_MONOTONIC (clock_gettime)" : "Stopwatch";

    /// <summary>Current time in nanoseconds. Allocation-free and safe to call from any thread.</summary>
    public static long NowNs
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (NativePlatform.IsLinux64)
            {
                return ReadClockMonotonic();
            }

            return ReadStopwatch();
        }
    }

    /// <summary>Clock resolution in nanoseconds as reported by the OS (1 with high-resolution timers on Linux).</summary>
    public static long ResolutionNs
    {
        get
        {
            if (NativePlatform.IsLinux64)
            {
                return ReadResolution();
            }

            return Math.Max(1, Nanoseconds.PerSecond / StopwatchFrequency);
        }
    }

    [SupportedOSPlatform("linux")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ReadClockMonotonic()
    {
        LibC.Timespec ts;
        _ = LibC.ClockGetTime(LibC.ClockMonotonic, &ts);
        return (ts.Seconds * Nanoseconds.PerSecond) + ts.Nanoseconds;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe long ReadResolution()
    {
        LibC.Timespec ts;
        return LibC.ClockGetRes(LibC.ClockMonotonic, &ts) == 0 ? (ts.Seconds * Nanoseconds.PerSecond) + ts.Nanoseconds : 1;
    }

    private static long ReadStopwatch()
    {
        var ticks = Stopwatch.GetTimestamp();
        var f = StopwatchFrequency;
        if (f == Nanoseconds.PerSecond)
        {
            return ticks;
        }

        // Split so ticks * 1e9 cannot overflow for any realistic uptime.
        var seconds = ticks / f;
        var remainder = ticks % f;
        return (seconds * Nanoseconds.PerSecond) + (remainder * Nanoseconds.PerSecond / f);
    }
}

/// <summary>
/// <see cref="IClock"/> on <see cref="Monotonic"/>: <see cref="NowNs"/> is <c>CLOCK_MONOTONIC</c> and
/// <see cref="SleepUntil"/> waits for an absolute deadline with the configured <see cref="DeadlineWaiter"/>.
/// </summary>
public sealed class MonotonicClock : IClock
{
    public MonotonicClock()
        : this(DeadlineWaiter.Sleep)
    {
    }

    public MonotonicClock(DeadlineWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        Waiter = waiter;
    }

    /// <summary>A clock that sleeps with <c>clock_nanosleep</c> only (no spinning).</summary>
    public static MonotonicClock Shared { get; } = new();

    public DeadlineWaiter Waiter { get; }

    public long NowNs
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Monotonic.NowNs;
    }

    public void SleepUntil(long deadlineNs) => Waiter.WaitUntil(deadlineNs);
}
