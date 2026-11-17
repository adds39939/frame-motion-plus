using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.App.Bench;

/// <summary>Faults the tests inject into the pacing thread's first measured full-loop slot. Never set by the CLI.</summary>
internal readonly record struct JitterBenchFaults(long StallNs = 0, int AllocateBytes = 0, bool Collect = false)
{
    public bool Any => StallNs > 0 || AllocateBytes > 0 || Collect;
}

/// <summary>Configuration of <c>fmp bench jitter</c>. Durations are nanoseconds.</summary>
internal sealed record JitterBenchOptions
{
    /// <summary>Upper bound on stored samples per loop kind (about 80 MB of sample arrays each).</summary>
    public const long MaxSlotsPerLoop = 2_000_000;

    /// <summary>Not measured after each switch between the timer-only and the full loop.</summary>
    public const long BlockSettleNs = 100 * Nanoseconds.PerMillisecond;

    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal) { "rt", "no-baseline" };

    /// <summary>Measured time per loop kind, warm-up and settling excluded.</summary>
    public long DurationNs { get; init; } = 10 * Nanoseconds.PerSecond;

    /// <summary>Excluded from the statistics at the start of each loop kind (scheduler start-up, page faults, caches).</summary>
    public long WarmupNs { get; init; } = Nanoseconds.PerSecond;

    /// <summary>The timer-only and the full loop alternate in blocks of this length, so slow drift in the machine's
    /// state (frequency, load, idle states) affects both alike.</summary>
    public long BlockNs { get; init; } = 5 * Nanoseconds.PerSecond;

    /// <summary>Output slot rate: the panel refresh.</summary>
    public FrameRate Rate { get; init; } = FrameRate.FromHz(120);

    /// <summary>Synthetic unique-frame rate of the source thread.</summary>
    public FrameRate SourceRate { get; init; } = FrameRate.FromHz(60);

    public WaitStrategy Wait { get; init; } = WaitStrategy.Sleep;

    public long SpinWindowNs { get; init; }

    public bool RealTime { get; init; }

    public int RealTimePriority { get; init; } = 10;

    /// <summary>Nice value: requested on its own, or the fallback when real-time is refused (default -10 then).</summary>
    public int? Nice { get; init; }

    public int? PacingCore { get; init; }

    public int? SourceCore { get; init; }

    /// <summary>Per-thread timer slack, set explicitly on both threads.</summary>
    public long TimerSlackNs { get; init; } = 1;

    /// <summary>Generation time d the pacing loop reports to the scheduler for each generated slot.</summary>
    public long GenerationTimeNs { get; init; } = 1_200_000;

    /// <summary>Fail when p99 wake lateness of the full loop exceeds this.</summary>
    public long GateNs { get; init; } = 500_000;

    /// <summary>Fail when the full loop misses more slots than this in the measured time.</summary>
    public long MaxMissed { get; init; }

    /// <summary>Interleave the timer-only loop with the full loop, to separate OS jitter from ours.</summary>
    public bool Baseline { get; init; } = true;

    public string? CsvPath { get; init; }

    internal JitterBenchFaults Faults { get; init; }

    public DeadlineWaiter CreateWaiter() =>
        Wait == WaitStrategy.Sleep ? DeadlineWaiter.Sleep : DeadlineWaiter.Hybrid(SpinWindowNs > 0 ? SpinWindowNs : DeadlineWaiter.DefaultSpinWindowNs);

    public ThreadSchedulingRequest PacingRequest => new()
    {
        RealTime = RealTime ? RealTimePolicy.Fifo : RealTimePolicy.None,
        RealTimePriority = RealTimePriority,
        Nice = Nice ?? (RealTime ? -10 : null),
        Core = PacingCore,
        TimerSlackNs = TimerSlackNs,
    };

    public ThreadSchedulingRequest SourceRequest => PacingRequest with { Core = SourceCore };

    public string WaitDescription => CreateWaiter().ToString();

    /// <summary>Upper bound on the slots one loop kind can store, for sizing its sample arrays.</summary>
    public long SlotsPerLoopBound
    {
        get
        {
            var blocks = Baseline ? (DurationNs / Math.Max(1, BlockNs - BlockSettleNs)) + 2 : 1;
            var wallNs = WarmupNs + DurationNs + (blocks * BlockSettleNs);
            return (long)(wallNs / Rate.PeriodNs * 1.01) + 1024;
        }
    }

    public static JitterBenchOptions Parse(IReadOnlyList<string> args)
    {
        var r = new ArgumentReader(args, FlagNames);
        var durationS = r.Double("duration-s", 10, 0.05, 86_400);
        var warmupS = r.Double("warmup-s", 1, 0, 3_600);
        var blockS = r.Double("block-s", 5, 0.5, 3_600);
        var rate = r.Rate("rate", FrameRate.FromHz(120));
        var sourceRate = r.Rate("source-fps", FrameRate.FromHz(60));
        var wait = r.String("wait") switch
        {
            null or "sleep" => WaitStrategy.Sleep,
            "hybrid" => WaitStrategy.Hybrid,
            var other => throw new UsageException($"Option '--wait' expects 'sleep' or 'hybrid', got '{other}'."),
        };
        var spinGiven = r.Has("spin-us");
        var spinUs = r.Double("spin-us", DeadlineWaiter.DefaultSpinWindowNs / 1000.0, 1, DeadlineWaiter.MaxSpinWindowNs / 1000.0);
        var rt = r.Flag("rt");
        var rtPriority = (int)r.Int64("rt-priority", 10, 1, 99);
        var niceGiven = r.Has("nice");
        var nice = (int)r.Int64("nice", 0, -20, 19);
        var pinGiven = r.Has("pin-core");
        var pin = (int)r.Int64("pin-core", 0, 0, 65_535);
        var sourcePinGiven = r.Has("source-core");
        var sourcePin = (int)r.Int64("source-core", 0, 0, 65_535);
        var slack = r.Int64("timer-slack-ns", 1, 1, 10_000_000);
        var dMs = r.Double("d-ms", 1.2, 0, 100);
        var gateMs = r.Double("gate-ms", 0.5, 0.001, 1_000);
        var maxMissed = r.Int64("max-missed", 0, 0, int.MaxValue);
        var noBaseline = r.Flag("no-baseline");
        var csv = r.String("csv");
        r.EnsureAllConsumed();

        if (spinGiven && wait != WaitStrategy.Hybrid)
        {
            throw new UsageException("--spin-us only applies to --wait hybrid.");
        }

        var spinNs = Nanoseconds.FromMicroseconds(spinUs);
        if (wait == WaitStrategy.Hybrid && spinNs >= rate.PeriodNs)
        {
            throw new UsageException("--spin-us must be shorter than one slot period.");
        }

        if (rate.Hz < 1 || sourceRate.Hz < 1)
        {
            throw new UsageException("--rate and --source-fps must be at least 1.");
        }

        var options = new JitterBenchOptions
        {
            DurationNs = Nanoseconds.FromSeconds(durationS),
            WarmupNs = Nanoseconds.FromSeconds(warmupS),
            BlockNs = Nanoseconds.FromSeconds(blockS),
            Rate = rate,
            SourceRate = sourceRate,
            Wait = wait,
            SpinWindowNs = wait == WaitStrategy.Hybrid ? spinNs : 0,
            RealTime = rt,
            RealTimePriority = rtPriority,
            Nice = niceGiven ? nice : null,
            PacingCore = pinGiven ? pin : null,
            SourceCore = sourcePinGiven ? sourcePin : null,
            TimerSlackNs = slack,
            GenerationTimeNs = Nanoseconds.FromMilliseconds(dMs),
            GateNs = Nanoseconds.FromMilliseconds(gateMs),
            MaxMissed = maxMissed,
            Baseline = !noBaseline,
            CsvPath = csv,
        };

        if (options.SlotsPerLoopBound > MaxSlotsPerLoop)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture,
                $"--duration-s and --warmup-s at --rate {rate} need about {options.SlotsPerLoopBound} samples per loop; " +
                $"at most {MaxSlotsPerLoop} are stored (about {MaxSlotsPerLoop / rate.Hz / 3600:0.#} h at this rate)."));
        }

        return options;
    }
}
