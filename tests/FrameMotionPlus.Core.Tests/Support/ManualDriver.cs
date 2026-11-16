using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;

namespace FrameMotionPlus.Core.Tests.Support;

/// <summary>
/// The scheduling-thread loop with nothing else in it: no telemetry, no result object. Used to measure the
/// scheduler's own per-slot allocations.
/// </summary>
internal sealed class ManualDriver
{
    private readonly FrameScheduler _scheduler;
    private readonly SimulatedSource _source;
    private readonly long _generationNs;
    private bool _hasPending;
    private FrameArrival _pending;

    public ManualDriver(FrameScheduler scheduler, SimulatedSourceOptions source, long generationNs)
    {
        _scheduler = scheduler;
        _source = new SimulatedSource(source);
        _generationNs = generationNs;
        _hasPending = _source.TryNext(out _pending);
    }

    public long Now { get; private set; }

    public long Slots { get; private set; }

    public long Generated { get; private set; }

    public long Checksum { get; private set; }

    public void RunUntil(long untilNs)
    {
        while (true)
        {
            var nextArrival = _hasPending ? _pending.ArrivalTimeNs : long.MaxValue;
            var nextDecision = _scheduler.NextDecisionTime(Now);
            var next = Math.Min(nextArrival, nextDecision);
            if (next >= untilNs)
            {
                Now = untilNs;
                return;
            }

            Now = Math.Max(Now, next);
            if (nextArrival <= nextDecision)
            {
                _scheduler.OnFrameArrived(_pending);
                _hasPending = _source.TryNext(out _pending);
                continue;
            }

            var decision = _scheduler.Schedule(Now);
            Slots++;
            if (decision.Kind == SlotKind.Generated)
            {
                Generated++;
                _scheduler.ReportGenerationTime(_generationNs);
            }

            var diagnostics = _scheduler.Diagnostics;
            var counters = _scheduler.Counters;
            Checksum += decision.ContentTimeNs + diagnostics.PipelineDelayNs + counters.Slots + _scheduler.OldestReferencedFrameId;
        }
    }
}
