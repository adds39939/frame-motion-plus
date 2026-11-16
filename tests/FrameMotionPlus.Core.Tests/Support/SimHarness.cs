using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Support;

internal sealed class CollectingSink : ITelemetrySink
{
    public List<FrameTimelineRecord> Records { get; } = new(capacity: 1 << 16);

    public void Write(in FrameTimelineRecord record) => Records.Add(record);
}

internal sealed record SimRun(
    SimulationResult Result,
    List<FrameTimelineRecord> Records,
    NullFrameGenerator Generator,
    FrameScheduler Scheduler,
    SchedulerOptions Options,
    SimulationOptions Simulation)
{
    /// <summary>Records whose target display time is at or after <paramref name="fromNs"/>.</summary>
    public List<FrameTimelineRecord> From(long fromNs) => Records.Where(r => r.TargetDisplayNs >= fromNs).ToList();

    public double PeriodNs => Options.DisplayRate.PeriodNs;
}

internal static class SimHarness
{
    public const long Ms = Nanoseconds.PerMillisecond;
    public const long Second = Nanoseconds.PerSecond;

    public static SchedulerOptions Fixed(long hz, long marginNs = 500_000, long initialDNs = 2 * Ms) => new()
    {
        ClockMode = ClockMode.FixedRefresh,
        DisplayRate = FrameRate.FromHz(hz),
        MarginNs = marginNs,
        InitialGenerationTimeNs = initialDNs,
    };

    public static SchedulerOptions Vrr(long maxHz, long marginNs = 500_000, long initialDNs = 2 * Ms) => new()
    {
        ClockMode = ClockMode.Vrr,
        DisplayRate = FrameRate.FromHz(maxHz),
        MarginNs = marginNs,
        InitialGenerationTimeNs = initialDNs,
    };

    public static SimulationOptions Sim(FrameRate source, double seconds, long dNs = 2 * Ms) => new()
    {
        DurationNs = (long)(seconds * Second),
        StatsStartNs = Second,
        GenerationTimeNs = dNs,
        Source = new SimulatedSourceOptions { Rate = source },
    };

    public static SimRun Run(SchedulerOptions options, SimulationOptions sim)
    {
        var scheduler = new FrameScheduler(options);
        var generator = new NullFrameGenerator();
        using var presenter = new NullPresenter(options.DisplayRate, options.VblankAnchorNs, options.ClockMode == ClockMode.Vrr);
        var sink = new CollectingSink();
        var result = new PipelineSimulator(sim, scheduler, generator, presenter, sink).Run();
        return new SimRun(result, sink.Records, generator, scheduler, options, sim);
    }

    /// <summary>Runs without collecting per-slot records, for long soaks.</summary>
    public static SimulationResult RunCounting(SchedulerOptions options, SimulationOptions sim)
    {
        var scheduler = new FrameScheduler(options);
        using var generator = new NullFrameGenerator();
        using var presenter = new NullPresenter(options.DisplayRate, options.VblankAnchorNs, options.ClockMode == ClockMode.Vrr);
        return new PipelineSimulator(sim, scheduler, generator, presenter).Run();
    }

    /// <summary>Content timestamps the simulated source produces for <paramref name="options"/>, by frame id.</summary>
    public static Dictionary<long, long> SourceTimes(SimulatedSourceOptions options, long untilNs)
    {
        var source = new SimulatedSource(options);
        var map = new Dictionary<long, long>();
        while (source.TryNext(out var f) && f.ContentTimeNs <= untilNs)
        {
            map[f.FrameId] = f.ContentTimeNs;
        }

        return map;
    }

    public static double Frac(double x) => x - Math.Floor(x);
}
