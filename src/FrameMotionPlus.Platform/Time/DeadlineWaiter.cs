using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Interop;

namespace FrameMotionPlus.Platform.Time;

public enum WaitStrategy
{
    /// <summary>Block in the kernel until the deadline (<c>clock_nanosleep</c>, <c>TIMER_ABSTIME</c>). No CPU cost;
    /// lateness is the OS's timer and wake-up latency (timer slack, C-state exit, scheduling).</summary>
    Sleep = 0,

    /// <summary>Sleep until <see cref="DeadlineWaiter.SpinWindowNs"/> before the deadline, then spin on the clock.
    /// Absorbs wake-up latency shorter than the window at the cost of that much CPU per wait. Not for a normal
    /// (SCHED_OTHER) thread that may share its core with other work: the spinning uses up its fair share, so after
    /// the sleep it no longer preempts that work and wakes milliseconds late (docs/benchmarks/phase2-jitter.md).</summary>
    Hybrid = 1,
}

/// <summary>
/// Waits for an absolute deadline on <see cref="Monotonic"/>. Never returns before the deadline; a deadline that has
/// already passed returns at once. Signals that interrupt the sleep (EINTR) are retried against the same absolute
/// deadline, so they cannot shorten or lengthen the wait. Allocation-free; one instance may be shared by any number
/// of threads.
/// </summary>
public sealed class DeadlineWaiter
{
    /// <summary>Default hybrid spin window: several times this machine's measured wake-up latency tail on an idle
    /// core (docs/benchmarks/phase2-jitter.md).</summary>
    public const long DefaultSpinWindowNs = 200_000;

    /// <summary>Upper bound on the spin window; longer windows burn CPU without helping.</summary>
    public const long MaxSpinWindowNs = 10_000_000;

    // Fallback (no clock_nanosleep): Thread.Sleep only while more than this remains; the rest is spun.
    private const long CoarseSleepGuardNs = 2_000_000;

    private static long _interruptedSleeps;

    public DeadlineWaiter(WaitStrategy strategy, long spinWindowNs = 0)
    {
        switch (strategy)
        {
            case WaitStrategy.Sleep:
                if (spinWindowNs != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(spinWindowNs), spinWindowNs, "The sleep strategy does not spin.");
                }

                break;
            case WaitStrategy.Hybrid:
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spinWindowNs);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(spinWindowNs, MaxSpinWindowNs);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown wait strategy.");
        }

        Strategy = strategy;
        SpinWindowNs = spinWindowNs;
    }

    /// <summary>Kernel sleep only.</summary>
    public static DeadlineWaiter Sleep { get; } = new(WaitStrategy.Sleep);

    public WaitStrategy Strategy { get; }

    /// <summary>How long before the deadline the hybrid strategy stops sleeping and starts spinning; 0 for sleep.</summary>
    public long SpinWindowNs { get; }

    /// <summary>Process-wide count of sleeps a signal interrupted (EINTR) and that were resumed. Diagnostics only.</summary>
    public static long InterruptedSleeps => Volatile.Read(ref _interruptedSleeps);

    /// <summary>True when sleeps use <c>clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME)</c>; otherwise a portable
    /// <see cref="Thread.Sleep(int)"/> plus spin fallback with coarser wake-ups.</summary>
    public static bool IsPrecise => NativePlatform.IsLinux64;

    public static DeadlineWaiter Hybrid(long spinWindowNs = DefaultSpinWindowNs) => new(WaitStrategy.Hybrid, spinWindowNs);

    /// <summary>
    /// Blocks until <see cref="Monotonic.NowNs"/> is at or after <paramref name="deadlineNs"/> and returns the time it
    /// observed on waking (always &gt;= the deadline; for a past deadline, the current time).
    /// </summary>
    public long WaitUntil(long deadlineNs)
    {
        var now = Monotonic.NowNs;
        if (now >= deadlineNs)
        {
            return now;
        }

        var sleepUntil = deadlineNs - SpinWindowNs;
        if (sleepUntil > now)
        {
            SleepUntil(sleepUntil);
            now = Monotonic.NowNs;
        }

        while (now < deadlineNs)
        {
            SpinPause();
            now = Monotonic.NowNs;
        }

        return now;
    }

    public override string ToString() => Strategy == WaitStrategy.Sleep
        ? "sleep"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"hybrid (spin {SpinWindowNs / Nanoseconds.PerMicrosecond} us)");

    /// <summary>Sleeps until about <paramref name="targetNs"/>; never past it on Linux. The caller spins any rest.</summary>
    private static void SleepUntil(long targetNs)
    {
        if (NativePlatform.IsLinux64)
        {
            if (SleepUntilLinux(targetNs))
            {
                return;
            }
        }

        SleepCoarse(targetNs);
    }

    [SupportedOSPlatform("linux")]
    private static unsafe bool SleepUntilLinux(long targetNs)
    {
        LibC.Timespec ts;
        ts.Seconds = targetNs / Nanoseconds.PerSecond;
        ts.Nanoseconds = targetNs % Nanoseconds.PerSecond;
        while (true)
        {
            var rc = LibC.ClockNanosleep(LibC.ClockMonotonic, LibC.TimerAbsTime, &ts, null);
            if (rc == 0)
            {
                return true;
            }

            if (rc != LibC.Eintr)
            {
                // Only EINVAL is possible (a malformed timespec); let the portable path finish the wait.
                return false;
            }

            // The absolute deadline is unchanged, so retrying needs no remaining-time bookkeeping.
            Interlocked.Increment(ref _interruptedSleeps);
        }
    }

    private static void SleepCoarse(long targetNs)
    {
        while (true)
        {
            var remaining = targetNs - Monotonic.NowNs;
            if (remaining <= CoarseSleepGuardNs)
            {
                return;
            }

            var ms = (remaining - CoarseSleepGuardNs) / Nanoseconds.PerMillisecond;
            if (ms <= 0)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep((int)Math.Min(ms, int.MaxValue));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpinPause()
    {
        if (X86Base.IsSupported)
        {
            X86Base.Pause();
        }
        else if (ArmBase.IsSupported)
        {
            ArmBase.Yield();
        }
        else
        {
            Thread.SpinWait(1);
        }
    }
}
