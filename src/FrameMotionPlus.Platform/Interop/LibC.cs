using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FrameMotionPlus.Platform.Interop;

/// <summary>Which native code path the platform layer takes.</summary>
internal static class NativePlatform
{
    /// <summary>64-bit Linux: <see cref="LibC"/> is used. A constant after AOT compilation for a given target.</summary>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux64 => OperatingSystem.IsLinux() && Environment.Is64BitProcess;
}

/// <summary>
/// The glibc subset the platform layer uses. The constants are identical on x86-64 and AArch64 Linux. Only called from
/// 64-bit Linux processes (<see cref="NativePlatform.IsLinux64"/>), where <c>long</c> is the C <c>long</c> and
/// <c>time_t</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class LibC
{
    private const string Library = "libc";

    public const int ClockMonotonic = 1;
    public const int ClockThreadCpuTime = 3;
    public const int TimerAbsTime = 1;

    public const int Eperm = 1;
    public const int Esrch = 3;
    public const int Eintr = 4;
    public const int Eacces = 13;
    public const int Einval = 22;

    public const int SchedOther = 0;
    public const int SchedFifo = 1;
    public const int SchedRr = 2;
    public const int SchedBatch = 3;
    public const int SchedIdle = 5;
    public const int SchedDeadline = 6;
    public const int SchedResetOnFork = 0x40000000;

    public const int PrioProcess = 0;

    public const int RlimitNice = 13;
    public const int RlimitRtprio = 14;
    public const ulong RlimInfinity = ulong.MaxValue;

    public const int PrSetName = 15;
    public const int PrSetTimerslack = 29;
    public const int PrGetTimerslack = 30;

    [StructLayout(LayoutKind.Sequential)]
    public struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SchedParam
    {
        public int Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rlimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    // vDSO, a few tens of nanoseconds, never blocks: no GC transition needed on the hottest call in the app.
    [LibraryImport(Library, EntryPoint = "clock_gettime")]
    [SuppressGCTransition]
    public static partial int ClockGetTime(int clockId, Timespec* time);

    [LibraryImport(Library, EntryPoint = "clock_getres")]
    public static partial int ClockGetRes(int clockId, Timespec* resolution);

    /// <summary>Returns 0 or the error number itself (it does not set errno).</summary>
    [LibraryImport(Library, EntryPoint = "clock_nanosleep")]
    public static partial int ClockNanosleep(int clockId, int flags, Timespec* request, Timespec* remain);

    [LibraryImport(Library, EntryPoint = "sched_setscheduler", SetLastError = true)]
    public static partial int SchedSetScheduler(int pid, int policy, SchedParam* param);

    [LibraryImport(Library, EntryPoint = "sched_getscheduler", SetLastError = true)]
    public static partial int SchedGetScheduler(int pid);

    [LibraryImport(Library, EntryPoint = "sched_getparam", SetLastError = true)]
    public static partial int SchedGetParam(int pid, SchedParam* param);

    [LibraryImport(Library, EntryPoint = "sched_get_priority_min", SetLastError = true)]
    public static partial int SchedGetPriorityMin(int policy);

    [LibraryImport(Library, EntryPoint = "sched_get_priority_max", SetLastError = true)]
    public static partial int SchedGetPriorityMax(int policy);

    [LibraryImport(Library, EntryPoint = "sched_setaffinity", SetLastError = true)]
    public static partial int SchedSetAffinity(int pid, nuint cpuSetSize, ulong* mask);

    [LibraryImport(Library, EntryPoint = "sched_getaffinity", SetLastError = true)]
    public static partial int SchedGetAffinity(int pid, nuint cpuSetSize, ulong* mask);

    [LibraryImport(Library, EntryPoint = "sched_getcpu")]
    public static partial int SchedGetCpu();

    /// <summary>With <see cref="PrioProcess"/> and a thread id, Linux applies the nice value to that thread only.</summary>
    [LibraryImport(Library, EntryPoint = "setpriority", SetLastError = true)]
    public static partial int SetPriority(int which, uint who, int priority);

    /// <summary>Returns the nice value; -1 is ambiguous, so check the last error (the stub clears it before the call).</summary>
    [LibraryImport(Library, EntryPoint = "getpriority", SetLastError = true)]
    public static partial int GetPriority(int which, uint who);

    [LibraryImport(Library, EntryPoint = "getrlimit", SetLastError = true)]
    public static partial int GetRlimit(int resource, Rlimit* limit);

    // prctl is variadic in C; integer arguments are passed exactly like fixed ones on x86-64 and AArch64 Linux.
    [LibraryImport(Library, EntryPoint = "prctl", SetLastError = true)]
    public static partial int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport(Library, EntryPoint = "gettid")]
    public static partial int GetTid();

    [LibraryImport(Library, EntryPoint = "getpid")]
    public static partial int GetPid();

    public static string ErrorName(int errno) => errno switch
    {
        0 => "no error",
        Eperm => "EPERM",
        Esrch => "ESRCH",
        Eintr => "EINTR",
        Eacces => "EACCES",
        Einval => "EINVAL",
        _ => "errno " + errno.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>_exit(2): ends the process at once, without exit handlers (atexit, the runtime's shutdown).</summary>
    [LibraryImport(Library, EntryPoint = "_exit")]
    public static partial void ExitImmediately(int status);
}
