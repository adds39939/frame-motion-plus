using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Support;

/// <summary>A frame for <see cref="EventDriver"/>: content time, arrival time and a source discontinuity flag.</summary>
internal readonly record struct DrivenFrame(long ContentNs, long ArrivalNs, bool Discontinuity = false);

/// <summary>
/// Drives a scheduler the way the scheduling thread will: deliver frames at their arrival time, wake for each
/// decision at NextDecisionTime plus a lateness, and report a generation time. Optional hooks run after each
/// decision. Used for scenarios the pipeline simulator does not express (explicit discontinuity flags,
/// re-anchoring mid-run, counting NextDecisionTime calls).
/// </summary>
internal static class EventDriver
{
    public static List<DrivenFrame> Regular(long startNs, FrameRate rate, int count, long lagNs = 0)
    {
        var frames = new List<DrivenFrame>(count);
        for (var k = 0; k < count; k++)
        {
            var ts = startNs + rate.TickTimeNs(k);
            frames.Add(new DrivenFrame(ts, ts + lagNs));
        }

        return frames;
    }

    public static List<SlotDecision> Drive(
        FrameScheduler scheduler,
        IReadOnlyList<DrivenFrame> frames,
        long untilNs,
        Func<long, long>? generationNs = null,
        long wakeLateNs = 0,
        Action<SlotDecision, FrameScheduler>? afterDecision = null,
        int extraNextDecisionCalls = 0)
    {
        var decisions = new List<SlotDecision>();
        long now = 0, id = 0;
        var i = 0;
        var guard = 0;
        while (true)
        {
            var nextArrival = i < frames.Count ? frames[i].ArrivalNs : long.MaxValue;
            for (var k = 0; k < extraNextDecisionCalls; k++)
            {
                scheduler.NextDecisionTime(now);
            }

            var nextDecision = scheduler.NextDecisionTime(now);
            if (nextDecision != long.MaxValue)
            {
                nextDecision += wakeLateNs;
            }

            var next = Math.Min(nextArrival, nextDecision);
            if (next >= untilNs || next == long.MaxValue)
            {
                return decisions;
            }

            now = Math.Max(now, next);
            if (nextArrival <= nextDecision)
            {
                var f = frames[i++];
                scheduler.OnFrameArrived(new FrameArrival(id++, f.ContentNs, f.ArrivalNs, f.Discontinuity));
                continue;
            }

            var d = scheduler.Schedule(now);
            if (d.Kind == SlotKind.Generated)
            {
                scheduler.ReportGenerationTime(generationNs?.Invoke(now) ?? 2_000_000);
            }

            decisions.Add(d);
            afterDecision?.Invoke(d, scheduler);
            guard = d.Kind == SlotKind.Empty ? guard + 1 : 0;
            if (guard > 100_000)
            {
                throw new InvalidOperationException("no progress");
            }
        }
    }
}
