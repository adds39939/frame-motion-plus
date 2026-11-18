using System.Runtime.Versioning;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Interop;

namespace FrameMotionPlus.Platform.Time;

/// <summary>
/// CPU time consumed by the calling thread (<c>CLOCK_THREAD_CPUTIME_ID</c>): what a pipeline thread costs per slot, as
/// opposed to its wall time, which includes its waits. Allocation-free.
/// </summary>
public static class ThreadCpuClock
{
    /// <summary>True when <see cref="CurrentThreadNs"/> measures (64-bit Linux).</summary>
    public static bool IsAvailable => NativePlatform.IsLinux64;

    /// <summary>The calling thread's CPU time so far, ns; -1 where it cannot be read (see <see cref="IsAvailable"/>).</summary>
    public static long CurrentThreadNs => NativePlatform.IsLinux64 ? Read() : -1;

    [SupportedOSPlatform("linux")]
    private static unsafe long Read()
    {
        LibC.Timespec ts;
        return LibC.ClockGetTime(LibC.ClockThreadCpuTime, &ts) == 0 ? (ts.Seconds * Nanoseconds.PerSecond) + ts.Nanoseconds : -1;
    }
}
