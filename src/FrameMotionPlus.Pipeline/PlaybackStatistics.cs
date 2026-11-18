using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Why a slot was not shown at its refresh, and whose that was. The display's (<see cref="DisplayLate"/>,
/// <see cref="CatchUp"/>) only where the timestamps show the present was handed over in time and the display showed it
/// late; <see cref="RateChange"/> while the output's refresh rate changed; everything else is the pipeline's.
/// </summary>
public enum MissKind
{
    /// <summary>The display's: shown a refresh (or more) late although it was handed over (submitted, its content written)
    /// at least the measured deadline before its refresh.</summary>
    DisplayLate = 0,

    /// <summary>The display's: a slot given up (or a real frame shown a refresh late) to bring the FIFO back on time after a
    /// display-late show.</summary>
    CatchUp = 1,

    /// <summary>A slot missed while the output's refresh rate changed (the window moved to another output).</summary>
    RateChange = 2,

    /// <summary>The pipeline's: shown after its refresh, not handed over in time for it (or no evidence of what in time is).</summary>
    LatePresent = 3,

    /// <summary>The pipeline's: not presented, its refresh unreachable when it came (or given up to catch up after one of
    /// the pipeline's own late shows).</summary>
    Dropped = 4,

    /// <summary>The pipeline's: the presenter refused it until its refresh had passed.</summary>
    Refused = 5,

    /// <summary>The pipeline's: the pacing thread woke after a refresh's decision time and skipped it.</summary>
    SkippedVblank = 6,

    /// <summary>The pipeline's: a slot was not produced: the bridge refused a slot (writes unfinished on the GPU).</summary>
    BackPressure = 7,

    /// <summary>The pipeline's: a slot was not produced: the present thread had fallen behind.</summary>
    PresenterBacklog = 8,

    /// <summary>The pipeline's: shown a refresh (or more) before its refresh (handed over early enough to be taken for an
    /// earlier one, or behind a FIFO that was ahead).</summary>
    Early = 9,

    /// <summary>The pipeline's: presented without a first-pixel time, and its refresh showed another present.</summary>
    NotShown = 10,

    /// <summary>The pipeline's: a refresh with no new present that no missed slot explains (two refreshes or more between
    /// consecutive shows).</summary>
    MissedRefresh = 11,

    /// <summary>The display's: handed over in time (at least the measured deadline before its refresh) and never shown: the
    /// compositor discarded it (its report came without a first-pixel time and another present took its refresh), or a
    /// slot shown a refresh early behind such a discard (the compositor gave a refresh back that the FIFO did not owe).</summary>
    Discarded = 12,

    /// <summary>Not a miss (a sentinel; never counted).</summary>
    None = 13,
}

/// <summary>One missed slot: when (ns after the start of playback) and why.</summary>
public readonly record struct MissEvent(long AtNs, MissKind Kind, int Refreshes);

/// <summary>What the measured window of a playback showed (filled by the collector on the control thread).</summary>
public sealed class PlaybackStatistics
{
    public const int MaxMissEvents = 4096;

    private readonly MissEvent[] _misses = new MissEvent[MaxMissEvents];
    private readonly long[] _missCounts = new long[13];

    public long Slots { get; internal set; }

    public long Real { get; internal set; }

    public long Generated { get; internal set; }

    public long Repeated { get; internal set; }

    public long Empty { get; internal set; }

    public long LateDecisions { get; internal set; }

    public long StarvedSlots { get; internal set; }

    public long HoldSlots { get; internal set; }

    public long DiscontinuitySlots { get; internal set; }

    public long FallbackSlots { get; internal set; }

    public long Presents { get; internal set; }

    /// <summary>Shows reported without a first-pixel time although the surface reports them (the report never came):
    /// kept out of the refresh grid, the lateness test and the intervals.</summary>
    public long UntimedShows { get; internal set; }

    /// <summary>Untimed presents the neighbouring timed shows cannot judge (their refresh did not go to another present, nor
    /// is there a show at it): unjudged, which fails the gate.</summary>
    public long UnjudgedSlots { get; internal set; }

    /// <summary>Refreshes without a new present (intervals of two refreshes or more between consecutive timed shows).</summary>
    public long RefreshesWithoutPresent { get; internal set; }

    /// <summary>Intervals between consecutive timed shows (in the measured window).</summary>
    public long TimedIntervals { get; internal set; }

    /// <summary>
    /// Of <see cref="TimedIntervals"/>: more than a quarter refresh off a whole number of refreshes. A fixed-rate output
    /// has none; many mean the output is not refreshing on a fixed grid (VRR, or a compositor missing its own deadlines),
    /// where no slot can be placed on a refresh.
    /// </summary>
    public long OffGridIntervals { get; internal set; }

    /// <summary>The last image put in for a refresh nothing was ready for (not a miss by itself: the slot it stood in for
    /// is counted when it is shown late or given up).</summary>
    public long Fillers { get; internal set; }


    /// <summary>Intervals between consecutive shown presents (present feedback), ns.</summary>
    public RunningStats OutputIntervals { get; } = new(10 * Nanoseconds.PerMicrosecond, 500 * Nanoseconds.PerMillisecond);

    /// <summary>Generation time d (GPU span of each generated frame), ns.</summary>
    public RunningStats GenerationTimes { get; } = new(Nanoseconds.PerMicrosecond, 100 * Nanoseconds.PerMillisecond);

    /// <summary>GPU span of each real-frame copy into the ring, ns.</summary>
    public RunningStats CopyTimes { get; } = new(Nanoseconds.PerMicrosecond, 100 * Nanoseconds.PerMillisecond);

    /// <summary>Generator thread's host time per write (for VFG with shot-change detection: it blocks for the generation), ns.</summary>
    public RunningStats WriteHostTimes { get; } = new(Nanoseconds.PerMicrosecond, 100 * Nanoseconds.PerMillisecond);

    /// <summary>The scheduler's pipeline delay D over the decisions, ns.</summary>
    public RunningStats PipelineDelays { get; } = new(10 * Nanoseconds.PerMicrosecond, 2 * Nanoseconds.PerSecond);

    /// <summary>Real frames: shown (feedback) minus content time, ns: the delay each real frame was shown with.</summary>
    public RunningStats RealFrameLatency { get; } = new(10 * Nanoseconds.PerMicrosecond, 2 * Nanoseconds.PerSecond);

    /// <summary>
    /// Real frames shown at their refresh: shown minus content time minus the present lead minus the scheduler's D, ns: the
    /// scheduler's rounding to real frames (at stride s it rounds content offsets down to s refreshes, so a new image lands up
    /// to s − 1 refreshes after τ; not phase-locked, a real frame within the snap of τ is shown up to half an interval before
    /// τ reaches it) plus the refresh-to-first-pixel time.
    /// </summary>
    public RunningStats StrideDelay { get; } = new(10 * Nanoseconds.PerMicrosecond, 500 * Nanoseconds.PerMillisecond);

    /// <summary>The present lead used by each slot, ns (it adapts).</summary>
    public RunningStats PresentLeads { get; } = new(10 * Nanoseconds.PerMicrosecond, 500 * Nanoseconds.PerMillisecond);

    /// <summary>Each timed show's hand-over lead: the refresh it was meant for minus when it was both submitted and written, ns.</summary>
    public RunningStats HandoverLeads { get; } = new(10 * Nanoseconds.PerMicrosecond, 500 * Nanoseconds.PerMillisecond);

    /// <summary>Submit to shown (feedback) per present, ns.</summary>
    public RunningStats SubmitToShown { get; } = new(10 * Nanoseconds.PerMicrosecond, 500 * Nanoseconds.PerMillisecond);


    /// <summary>Variable refresh: generated slots the scheduler skipped because the real frame after them came too late (the
    /// image before stays on screen for both): not a missed refresh (there is no grid), counted on its own.</summary>
    public long VrrSkippedSlots { get; internal set; }

    public long MaxOutstanding { get; internal set; }

    public long MaxPresentsInFlight { get; internal set; }

    public long FirstMeasuredNs { get; internal set; }

    public long LastMeasuredNs { get; internal set; }

    public double MeasuredSeconds => LastMeasuredNs > FirstMeasuredNs ? Nanoseconds.ToSeconds(LastMeasuredNs - FirstMeasuredNs) : 0;

    public long MissEventsDropped { get; private set; }

    public int MissEventCount { get; private set; }

    public ReadOnlySpan<MissEvent> MissEvents => _misses.AsSpan(0, MissEventCount);

    public long MissCount(MissKind kind) => _missCounts[(int)kind];

    /// <summary>Every missed slot, whoever caused it.</summary>
    public long TotalMisses
    {
        get
        {
            long total = 0;
            foreach (var c in _missCounts)
            {
                total += c;
            }

            return total;
        }
    }

    /// <summary>The pipeline's misses: every one the timestamps do not show to be the display's or a rate change's.</summary>
    public long PipelineMisses => TotalMisses - EnvironmentMisses - RateChangeMisses;

    /// <summary>The display's: shown late although handed over in time, the slots given up to catch up after those, and
    /// presents handed over in time that the compositor discarded.</summary>
    public long EnvironmentMisses => MissCount(MissKind.DisplayLate) + MissCount(MissKind.CatchUp) + MissCount(MissKind.Discarded);

    /// <summary>Misses while the output's refresh rate changed (the window moved).</summary>
    public long RateChangeMisses => MissCount(MissKind.RateChange);

    internal void AddMiss(long atNs, MissKind kind, int refreshes)
    {
        _missCounts[(int)kind] += refreshes;
        if (MissEventCount < MaxMissEvents)
        {
            _misses[MissEventCount++] = new MissEvent(atNs, kind, refreshes);
        }
        else
        {
            MissEventsDropped++;
        }
    }
}
