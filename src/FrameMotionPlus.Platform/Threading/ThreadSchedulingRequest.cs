namespace FrameMotionPlus.Platform.Threading;

public enum RealTimePolicy
{
    /// <summary>The normal time-sharing scheduler (SCHED_OTHER on Linux).</summary>
    None = 0,

    /// <summary>SCHED_FIFO: runs until it blocks or a higher real-time priority becomes runnable.</summary>
    Fifo = 1,

    /// <summary>SCHED_RR: SCHED_FIFO with a time slice among threads of equal priority.</summary>
    RoundRobin = 2,
}

/// <summary>
/// What a latency-critical thread asks the OS for. Every item is best effort: <see cref="ThreadScheduling"/> applies
/// what it can, never throws because something was refused, and reports what the thread actually got.
/// </summary>
public sealed record ThreadSchedulingRequest
{
    public static ThreadSchedulingRequest Default { get; } = new();

    public RealTimePolicy RealTime { get; init; } = RealTimePolicy.None;

    /// <summary>Real-time priority, 1 (lowest) to 99. Clamped to the policy's range; lowered to RLIMIT_RTPRIO when that
    /// is what allows real-time scheduling at all.</summary>
    public int RealTimePriority { get; init; } = 10;

    /// <summary>Nice value (-20 highest to 19 lowest) applied when real-time scheduling is not requested or refused.
    /// Null leaves it unchanged. Negative values need RLIMIT_NICE or CAP_SYS_NICE.</summary>
    public int? Nice { get; init; }

    /// <summary>Pin the thread to this logical CPU. Null leaves the affinity unchanged. Without real-time priority a
    /// pinned thread cannot move away from whatever else the kernel runs on that CPU, which lengthens its wake-up
    /// tail (docs/benchmarks/phase2-jitter.md); leave it unpinned unless the core is reserved for it.</summary>
    public int? Core { get; init; }

    /// <summary>Linux per-thread timer slack in ns: how much later than asked the kernel may fire this thread's
    /// timers so wake-ups can be batched (50 us for normal threads unless something lowered it). 1 makes sleeps as
    /// precise as the hardware allows. Null leaves it unchanged; a new thread inherits its creator's slack, so pass an
    /// explicit value (50_000 for the usual kernel default) rather than relying on that. 0 is rejected: the kernel
    /// reads it as "the slack inherited at thread creation", which is not a fixed value. Ignored on other OSes.</summary>
    public long? TimerSlackNs { get; init; } = 1;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RealTimePriority, 1, nameof(RealTimePriority));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RealTimePriority, 99, nameof(RealTimePriority));
        if (Nice is int nice && (nice < -20 || nice > 19))
        {
            throw new ArgumentOutOfRangeException(nameof(Nice), nice, "Must be between -20 and 19.");
        }

        if (Core is int core)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(core, nameof(Core));
        }

        if (TimerSlackNs is long slack)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slack, nameof(TimerSlackNs));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slack, (long)int.MaxValue, nameof(TimerSlackNs));
        }

        if (!Enum.IsDefined(RealTime))
        {
            throw new ArgumentOutOfRangeException(nameof(RealTime), RealTime, "Unknown policy.");
        }
    }
}
