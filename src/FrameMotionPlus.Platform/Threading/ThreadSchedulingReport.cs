using System.Globalization;
using System.Text;

namespace FrameMotionPlus.Platform.Threading;

/// <summary>What <see cref="ThreadScheduling.ApplyToCurrentThread"/> requested and what the thread actually got.</summary>
public sealed record ThreadSchedulingReport
{
    public required ThreadSchedulingRequest Requested { get; init; }

    public string? Name { get; init; }

    /// <summary>OS thread id (Linux tid); 0 when unknown.</summary>
    public int OsThreadId { get; init; }

    /// <summary>Obtained scheduling policy as the OS names it, e.g. SCHED_OTHER, SCHED_FIFO, or the managed thread
    /// priority where real-time scheduling is not implemented.</summary>
    public required string Policy { get; init; }

    public RealTimePolicy RealTime { get; init; }

    /// <summary>Obtained real-time priority; 0 when not real-time.</summary>
    public int RealTimePriority { get; init; }

    /// <summary>Obtained nice value; null where nice does not apply or could not be read.</summary>
    public int? Nice { get; init; }

    /// <summary>The single CPU the thread may run on, or null when its affinity allows more than one.</summary>
    public int? Core { get; init; }

    /// <summary>CPUs the thread may run on, as a Linux CPU list ("0-31"); null when unknown.</summary>
    public string? AllowedCpus { get; init; }

    /// <summary>Obtained timer slack, ns; null where it does not apply.</summary>
    public long? TimerSlackNs { get; init; }

    public string? RealTimeError { get; init; }

    public string? NiceError { get; init; }

    public string? AffinityError { get; init; }

    public string? TimerSlackError { get; init; }

    /// <summary>Soft RLIMIT_RTPRIO: the highest real-time priority allowed without CAP_SYS_NICE (Linux; -1 unknown,
    /// <see cref="long.MaxValue"/> unlimited).</summary>
    public long RtprioLimit { get; init; } = -1;

    /// <summary>Soft RLIMIT_NICE: the lowest nice allowed without CAP_SYS_NICE is 20 minus this (Linux; -1 unknown).</summary>
    public long NiceLimit { get; init; } = -1;

    public bool HasCapSysNice { get; init; }

    /// <summary>The rtkit daemon is installed (it can grant real-time priority over D-Bus; not used yet).</summary>
    public bool RtkitInstalled { get; init; }

    /// <summary>What the owner of the machine could change to get what was refused.</summary>
    public IReadOnlyList<string> Advice { get; init; } = [];

    public bool RealTimeObtained => RealTime != RealTimePolicy.None;

    /// <summary>Everything requested was granted as asked (real-time counts as granted when the priority was lowered to
    /// the rtprio limit).</summary>
    public bool FullyGranted =>
        (Requested.RealTime == RealTimePolicy.None || RealTime == Requested.RealTime)
        && (Requested.Nice is null || RealTimeObtained || Nice == Requested.Nice)
        && (Requested.Core is null || Core == Requested.Core)
        && (Requested.TimerSlackNs is null || (TimerSlackError is null && TimerSlackNs == Requested.TimerSlackNs));

    /// <summary>One line: obtained policy, priority or nice, core and timer slack.</summary>
    public string Summary
    {
        get
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(Policy);
            if (RealTimeObtained)
            {
                sb.Append(ic, $" priority {RealTimePriority}");
            }
            else if (Nice is int nice)
            {
                sb.Append(ic, $" nice {nice}");
            }

            sb.Append(Core is int core ? string.Create(ic, $", core {core}") : AllowedCpus is null ? "" : $", cpus {AllowedCpus}");
            if (TimerSlackNs is long slack)
            {
                sb.Append(ic, $", timer slack {slack} ns");
            }

            return sb.ToString();
        }
    }

    /// <summary>One line: what was asked for.</summary>
    public string RequestSummary
    {
        get
        {
            var ic = CultureInfo.InvariantCulture;
            var r = Requested;
            var parts = new List<string>(4)
            {
                r.RealTime switch
                {
                    RealTimePolicy.Fifo => string.Create(ic, $"SCHED_FIFO priority {r.RealTimePriority}"),
                    RealTimePolicy.RoundRobin => string.Create(ic, $"SCHED_RR priority {r.RealTimePriority}"),
                    _ => "default policy",
                },
            };
            if (r.Nice is int nice)
            {
                parts.Add(r.RealTime == RealTimePolicy.None ? string.Create(ic, $"nice {nice}") : string.Create(ic, $"else nice {nice}"));
            }

            if (r.Core is int core)
            {
                parts.Add(string.Create(ic, $"core {core}"));
            }

            if (r.TimerSlackNs is long slack)
            {
                parts.Add(string.Create(ic, $"timer slack {slack} ns"));
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>Refusals, one per line, each with its reason.</summary>
    public IEnumerable<string> Problems
    {
        get
        {
            if (RealTimeError is not null)
            {
                yield return "real-time: " + RealTimeError;
            }

            if (NiceError is not null)
            {
                yield return "nice: " + NiceError;
            }

            if (AffinityError is not null)
            {
                yield return "pinning: " + AffinityError;
            }

            if (TimerSlackError is not null)
            {
                yield return "timer slack: " + TimerSlackError;
            }
        }
    }

    /// <summary>The limits that decide what an unprivileged thread may get (Linux).</summary>
    public string LimitsSummary
    {
        get
        {
            var ic = CultureInfo.InvariantCulture;
            static string Limit(long v) => v < 0 ? "unknown" : v == long.MaxValue ? "unlimited" : v.ToString(CultureInfo.InvariantCulture);
            var niceFloor = NiceLimit switch
            {
                < 0 => "",
                <= 20 => " (no negative nice)",
                <= 40 => string.Create(ic, $" (nice down to {20 - NiceLimit})"),
                _ => " (nice down to -20)",
            };
            return string.Create(ic,
                $"RLIMIT_RTPRIO {Limit(RtprioLimit)}, RLIMIT_NICE {Limit(NiceLimit)}{niceFloor}, CAP_SYS_NICE {(HasCapSysNice ? "yes" : "no")}, " +
                $"rtkit {(RtkitInstalled ? "installed" : "not installed")}");
        }
    }
}
