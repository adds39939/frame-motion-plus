using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FrameMotionPlus.Platform.Interop;

namespace FrameMotionPlus.Platform.Threading;

/// <summary>
/// Applies a <see cref="ThreadSchedulingRequest"/> to the calling thread. On Linux: affinity
/// (<c>sched_setaffinity</c>), timer slack (<c>PR_SET_TIMERSLACK</c>), real-time policy (<c>sched_setscheduler</c>
/// with <c>SCHED_RESET_ON_FORK</c>, so child processes do not inherit it), and a nice value when real-time is not
/// requested or refused (<c>setpriority</c> on the thread id). Elsewhere the request maps to the managed thread
/// priority. Never throws because the OS refused something; the report says what was refused and why.
/// </summary>
public static class ThreadScheduling
{
    private const int CapSysNiceBit = 23;

    /// <summary>glibc's cpu_set_t: 1024 CPUs, the first mask size tried.</summary>
    private const int CpuSetWords = 16;

    /// <summary>Largest affinity mask tried: 65536 CPUs.</summary>
    private const int MaxCpuMaskWords = 1024;

    private static readonly string[] RtkitPaths =
    [
        "/usr/share/dbus-1/system-services/org.freedesktop.RealtimeKit1.service",
        "/usr/lib/rtkit-daemon",
        "/usr/libexec/rtkit-daemon",
    ];

    /// <summary>Applies <paramref name="request"/> to the calling thread and reports the result. Call it first thing
    /// on the thread it is for (not on a thread-pool thread: the settings would stick to whatever runs there next).</summary>
    public static ThreadSchedulingReport ApplyToCurrentThread(ThreadSchedulingRequest request, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (NativePlatform.IsLinux64)
        {
            return ApplyLinux(request, name);
        }

        return ApplyPortable(request, name);
    }

    /// <summary>Reads the calling thread's scheduling state without changing it.</summary>
    public static ThreadSchedulingReport DescribeCurrentThread()
    {
        var request = ThreadSchedulingRequest.Default with { TimerSlackNs = null };
        return ApplyToCurrentThread(request);
    }

    /// <summary>The OS thread id of the calling thread (Linux tid), or 0 where there is none to report.</summary>
    public static int CurrentOsThreadId => NativePlatform.IsLinux64 ? LibC.GetTid() : 0;

    /// <summary>The CPU the calling thread is running on right now, or -1 when unknown.</summary>
    public static int CurrentCpu => NativePlatform.IsLinux64 ? LibC.SchedGetCpu() : -1;

    /// <summary>CPUs the calling thread may run on, in ascending order (Linux); empty when unknown.</summary>
    public static int[] AllowedCpus()
    {
        if (!NativePlatform.IsLinux64)
        {
            return [];
        }

        var mask = ReadAffinityMask();
        if (mask is null)
        {
            return [];
        }

        var cpus = new List<int>();
        for (var i = 0; i < mask.Length * 64; i++)
        {
            if ((mask[i / 64] & (1UL << (i % 64))) != 0)
            {
                cpus.Add(i);
            }
        }

        return [.. cpus];
    }

    /// <summary>
    /// The calling thread's affinity mask (Linux), or null when it cannot be read. Starts with a cpu_set_t-sized buffer
    /// and doubles it while the kernel answers EINVAL, which it does when its CPU mask is larger than the buffer
    /// (machines configured for more than 1024 CPUs).
    /// </summary>
    internal static unsafe ulong[]? ReadAffinityMask(int initialWords = CpuSetWords)
    {
        if (!NativePlatform.IsLinux64)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(initialWords);
        for (var words = initialWords; words <= MaxCpuMaskWords; words = words == 0 ? 1 : words * 2)
        {
            var mask = new ulong[words];
            fixed (ulong* p = mask)
            {
                if (LibC.SchedGetAffinity(0, (nuint)(words * sizeof(ulong)), p) == 0)
                {
                    return mask;
                }
            }

            if (Marshal.GetLastPInvokeError() != LibC.Einval)
            {
                return null;
            }
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe ThreadSchedulingReport ApplyLinux(ThreadSchedulingRequest request, string? name)
    {
        var ic = CultureInfo.InvariantCulture;
        var tid = LibC.GetTid();
        if (name is not null)
        {
            SetName(name);
        }

        var rtprioLimit = ReadLimit(LibC.RlimitRtprio);
        var niceLimit = ReadLimit(LibC.RlimitNice);
        var capSysNice = HasCapSysNice();
        var rtkit = RtkitPaths.Any(File.Exists);
        var advice = new List<string>();

        string? affinityError = null;
        if (request.Core is int core)
        {
            if (core >= MaxCpuMaskWords * 64)
            {
                affinityError = string.Create(ic, $"core {core} is beyond the {MaxCpuMaskWords * 64} CPUs supported");
            }
            else
            {
                // The kernel zero-extends a short mask and ignores bits past its CPU count, so any size that holds the
                // core works.
                var words = Math.Max(CpuSetWords, (core / 64) + 1);
                var mask = new ulong[words];
                mask[core / 64] = 1UL << (core % 64);
                fixed (ulong* p = mask)
                {
                    if (LibC.SchedSetAffinity(0, (nuint)(words * sizeof(ulong)), p) != 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        affinityError = errno == LibC.Einval
                            ? string.Create(ic, $"EINVAL: core {core} does not exist, is offline or is outside this process's cpuset")
                            : LibC.ErrorName(errno);
                    }
                }
            }
        }

        string? slackError = null;
        if (request.TimerSlackNs is long slack && LibC.Prctl(LibC.PrSetTimerslack, (nuint)slack, 0, 0, 0) != 0)
        {
            slackError = LibC.ErrorName(Marshal.GetLastPInvokeError());
        }

        string? rtError = null;
        if (request.RealTime != RealTimePolicy.None)
        {
            var policy = request.RealTime == RealTimePolicy.Fifo ? LibC.SchedFifo : LibC.SchedRr;
            var min = LibC.SchedGetPriorityMin(policy);
            var max = LibC.SchedGetPriorityMax(policy);
            var priority = Math.Clamp(request.RealTimePriority, Math.Max(1, min), Math.Max(1, max));
            var errno = SetRealTime(policy, priority);
            if (errno == LibC.Eperm && rtprioLimit > 0 && rtprioLimit < priority)
            {
                // RLIMIT_RTPRIO allows real-time up to its value without privileges: take that rather than nothing.
                var lowered = (int)rtprioLimit;
                if (SetRealTime(policy, lowered) == 0)
                {
                    errno = 0;
                    advice.Add(string.Create(ic, $"real-time priority {lowered} (the RLIMIT_RTPRIO limit) was granted instead of {priority}: raise the rtprio limit to {priority} for more"));
                }
            }

            if (errno != 0)
            {
                rtError = errno == LibC.Eperm
                    ? string.Create(ic, $"EPERM: {PolicyName(policy)} priority {priority} needs CAP_SYS_NICE or RLIMIT_RTPRIO >= {priority} (it is {FormatLimit(rtprioLimit)})")
                    : LibC.ErrorName(errno);
                if (errno == LibC.Eperm)
                {
                    advice.Add(
                        $"allow real-time scheduling: an rtprio limit (a file in /etc/security/limits.d with '{Environment.UserName} - rtprio 98', " +
                        "or on Arch the realtime-privileges package and the 'realtime' group; log in again), " +
                        "or CAP_SYS_NICE on the binary ('sudo setcap cap_sys_nice+ep <path to fmp>')");
                    advice.Add(rtkit
                        ? "rtkit is installed: it can grant real-time priority over D-Bus (org.freedesktop.RealtimeKit1), which fmp does not request yet"
                        : "rtkit is not installed (the xdg-desktop-portal Realtime interface forwards to it, so that route is closed too)");
                }
            }
        }

        var obtainedPolicy = LibC.SchedGetScheduler(0) & ~LibC.SchedResetOnFork;
        var rtObtained = obtainedPolicy is LibC.SchedFifo or LibC.SchedRr;

        string? niceError = null;
        if (!rtObtained && request.Nice is int nice)
        {
            var current = ReadNice(tid) ?? 0;
            if (nice != current && LibC.SetPriority(LibC.PrioProcess, (uint)tid, nice) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                var floor = niceLimit is >= 0 and <= 40 ? (int)(20 - niceLimit) : 20;
                var fallback = Math.Max(nice, floor);
                var appliedFallback = fallback < current && LibC.SetPriority(LibC.PrioProcess, (uint)tid, fallback) == 0;
                var got = appliedFallback ? string.Create(ic, $"; got nice {fallback}, the limit") : "";
                niceError = errno is LibC.Eacces or LibC.Eperm
                    ? string.Create(ic, $"{LibC.ErrorName(errno)}: nice {nice} needs CAP_SYS_NICE or RLIMIT_NICE >= {20 - nice} (it is {FormatLimit(niceLimit)}){got}")
                    : LibC.ErrorName(errno);
                if (errno is LibC.Eacces or LibC.Eperm)
                {
                    advice.Add(string.Create(ic,
                        $"allow a negative nice: '{Environment.UserName} - nice {nice}' in /etc/security/limits.d (log in again), or CAP_SYS_NICE on the binary"));
                }
            }
        }

        SchedParamRead(out var rtPriority);
        int? pinned = null;
        string? allowedText = null;
        if (ReadAffinityMask() is { } allowed)
        {
            allowedText = FormatCpuList(allowed);
            pinned = SingleCpu(allowed);
        }

        return new ThreadSchedulingReport
        {
            Requested = request,
            Name = name,
            OsThreadId = tid,
            Policy = PolicyName(obtainedPolicy),
            RealTime = obtainedPolicy == LibC.SchedFifo ? RealTimePolicy.Fifo : obtainedPolicy == LibC.SchedRr ? RealTimePolicy.RoundRobin : RealTimePolicy.None,
            RealTimePriority = rtObtained ? rtPriority : 0,
            Nice = ReadNice(tid),
            Core = pinned,
            AllowedCpus = allowedText,
            TimerSlackNs = ReadTimerSlack(),
            RealTimeError = rtError,
            NiceError = niceError,
            AffinityError = affinityError,
            TimerSlackError = slackError,
            RtprioLimit = rtprioLimit,
            NiceLimit = niceLimit,
            HasCapSysNice = capSysNice,
            RtkitInstalled = rtkit,
            Advice = advice,
        };
    }

    private static ThreadSchedulingReport ApplyPortable(ThreadSchedulingRequest request, string? name)
    {
        var thread = Thread.CurrentThread;
        string? priorityError = null;
        var wanted = request.RealTime != RealTimePolicy.None
            ? ThreadPriority.Highest
            : request.Nice switch
            {
                null => (ThreadPriority?)null,
                < -10 => ThreadPriority.Highest,
                < 0 => ThreadPriority.AboveNormal,
                0 => ThreadPriority.Normal,
                <= 10 => ThreadPriority.BelowNormal,
                _ => ThreadPriority.Lowest,
            };
        if (wanted is ThreadPriority p)
        {
            try
            {
                thread.Priority = p;
            }
            catch (Exception e) when (e is ThreadStateException or PlatformNotSupportedException)
            {
                priorityError = e.Message;
            }
        }

        return new ThreadSchedulingReport
        {
            Requested = request,
            Name = name ?? thread.Name,
            Policy = "thread priority " + thread.Priority.ToString(),
            RealTimeError = request.RealTime == RealTimePolicy.None
                ? null
                : "real-time scheduling is implemented for Linux only; mapped to the highest managed thread priority" + (priorityError is null ? "" : $" ({priorityError})"),
            NiceError = request.RealTime == RealTimePolicy.None && request.Nice is not null
                ? "nice is Linux-only; mapped to a managed thread priority" + (priorityError is null ? "" : $" ({priorityError})")
                : null,
            AffinityError = request.Core is null ? null : "pinning is implemented for Linux only",
        };
    }

    [SupportedOSPlatform("linux")]
    private static unsafe int SetRealTime(int policy, int priority)
    {
        var param = new LibC.SchedParam { Priority = priority };
        return LibC.SchedSetScheduler(0, policy | LibC.SchedResetOnFork, &param) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    [SupportedOSPlatform("linux")]
    private static unsafe void SchedParamRead(out int priority)
    {
        var param = default(LibC.SchedParam);
        priority = LibC.SchedGetParam(0, &param) == 0 ? param.Priority : 0;
    }

    [SupportedOSPlatform("linux")]
    private static int? ReadNice(int tid)
    {
        var nice = LibC.GetPriority(LibC.PrioProcess, (uint)tid);
        return nice == -1 && Marshal.GetLastPInvokeError() != 0 ? null : nice;
    }

    [SupportedOSPlatform("linux")]
    private static long? ReadTimerSlack()
    {
        var slack = LibC.Prctl(LibC.PrGetTimerslack, 0, 0, 0, 0);
        return slack < 0 ? null : slack;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe long ReadLimit(int resource)
    {
        LibC.Rlimit limit;
        if (LibC.GetRlimit(resource, &limit) != 0)
        {
            return -1;
        }

        return limit.Current == LibC.RlimInfinity || limit.Current > long.MaxValue ? long.MaxValue : (long)limit.Current;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe void SetName(string name)
    {
        // The kernel keeps 15 bytes plus the terminator.
        Span<byte> buffer = stackalloc byte[16];
        buffer.Clear();
        var bytes = Encoding.UTF8.GetBytes(name);
        var length = Math.Min(bytes.Length, 15);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--; // do not cut a UTF-8 sequence in half
        }

        bytes.AsSpan(0, length).CopyTo(buffer);
        fixed (byte* p = buffer)
        {
            _ = LibC.Prctl(LibC.PrSetName, (nuint)p, 0, 0, 0);
        }
    }

    private static bool HasCapSysNice()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("CapEff:", StringComparison.Ordinal)
                    && ulong.TryParse(line.AsSpan(7).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var caps))
                {
                    return (caps & (1UL << CapSysNiceBit)) != 0;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static int? SingleCpu(ReadOnlySpan<ulong> mask)
    {
        var count = 0;
        var cpu = -1;
        for (var w = 0; w < mask.Length; w++)
        {
            var bits = BitOperations.PopCount(mask[w]);
            if (bits > 0)
            {
                count += bits;
                cpu = (w * 64) + BitOperations.TrailingZeroCount(mask[w]);
            }
        }

        return count == 1 ? cpu : null;
    }

    private static string FormatCpuList(ReadOnlySpan<ulong> mask)
    {
        var sb = new StringBuilder();
        var i = 0;
        var bits = mask.Length * 64;
        while (i < bits)
        {
            if ((mask[i / 64] & (1UL << (i % 64))) == 0)
            {
                i++;
                continue;
            }

            var start = i;
            while (i + 1 < bits && (mask[(i + 1) / 64] & (1UL << ((i + 1) % 64))) != 0)
            {
                i++;
            }

            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(start.ToString(CultureInfo.InvariantCulture));
            if (i > start)
            {
                sb.Append('-').Append(i.ToString(CultureInfo.InvariantCulture));
            }

            i++;
        }

        return sb.ToString();
    }

    private static string FormatLimit(long limit) =>
        limit < 0 ? "unknown" : limit == long.MaxValue ? "unlimited" : limit.ToString(CultureInfo.InvariantCulture);

    private static string PolicyName(int policy) => policy switch
    {
        0 => "SCHED_OTHER",
        1 => "SCHED_FIFO",
        2 => "SCHED_RR",
        3 => "SCHED_BATCH",
        5 => "SCHED_IDLE",
        6 => "SCHED_DEADLINE",
        _ => "policy " + policy.ToString(CultureInfo.InvariantCulture),
    };
}
