using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Telemetry;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// The control thread's side of telemetry: joins the pacing thread's decision records, the generator thread's write
/// records and the present thread's present records by slot into Core's per-slot <see cref="FrameTimelineRecord"/>
/// (content time, decode available, decision, generation start and end as a GPU span on <c>CLOCK_MONOTONIC</c>, write
/// signalled, present submitted, shown from feedback, slot kind and reason), puts each into Core's
/// <see cref="TelemetryRing"/> for the CSV writer and the summary, and keeps the measured window's statistics, including
/// every missed slot and whose it was. Off the hot path, and allocation-free all the same (its GC collections would be
/// the process's).
/// </summary>
internal sealed class TelemetryCollector
{
    private readonly Entry[] _entries;
    private readonly int _mask;
    private readonly TelemetryRing _ring;
    private long _newestSlot = -1;
    private long _measureFromNs = long.MaxValue;
    private long _originNs;
    private readonly long _warmupNs;

    private const int PendingCapacity = 64;
    private const int GapCapacity = 16384;
    private readonly long[] _pendingUntimed = new long[PendingCapacity];
    private int _pendingCount;
    private long _lastTimedShowNs;
    private readonly long[] _gapFrom = new long[GapCapacity];
    private readonly long[] _gapTo = new long[GapCapacity];
    private readonly int[] _gapUntimed = new int[GapCapacity];
    private int _gapCount;
    private long _periodNs;
    private VerifierRow[] _rows = [];
    private int _rowCount;

    public TelemetryCollector(int capacity, long warmupNs)
    {
        _warmupNs = warmupNs;
        _entries = new Entry[capacity];
        _mask = capacity - 1;
        for (var i = 0; i < capacity; i++)
        {
            _entries[i].SlotIndex = -1;
        }

        _ring = new TelemetryRing(capacity);
    }

    public PlaybackStatistics Statistics { get; } = new();

    /// <summary>Completed per-slot timeline records (Core's ring); the session drains it into the CSV.</summary>
    public TelemetryRing Records => _ring;

    public long RecordsCompleted { get; private set; }

    public long RecordsIncomplete { get; private set; }


    public bool IsMeasuring => _measureFromNs != long.MaxValue;

    /// <summary>The frame the first slot showing a frame showed (its FrameA); -1 before.</summary>
    public long FirstShownFrame { get; private set; } = -1;

    /// <summary>The first refresh of the measured window (long.MaxValue before), ns.</summary>
    public long MeasureFromNs => _measureFromNs;

    /// <summary>Rows kept for the telemetry verifier (every slot and filler completed so far).</summary>
    public ArraySegment<VerifierRow> VerifierRows => new(_rows, 0, _rowCount);

    /// <summary>Rows that did not fit in the verifier's buffer (the verification is then incomplete).</summary>
    public long VerifierRowsDropped { get; private set; }

    /// <summary>The refresh period the presents were judged with, ns (0 before the first).</summary>
    public long PeriodNs => _periodNs;

    /// <summary>Sizes the verifier's buffer (before playback: no allocation while it runs).</summary>
    public void ReserveVerifierRows(int rows) => _rows = new VerifierRow[Math.Max(rows, 1024)];

    public void Drain(PacingLoop pacing, RingWriter writer, PresentLoop presenter)
    {
        while (pacing.Decisions.TryDequeue(out var d))
        {
            OnDecision(d);
        }

        while (writer.Records.TryDequeue(out var w))
        {
            if (w.SlotIndex >= 0)
            {
                ref var e = ref Get(w.SlotIndex);
                if (e.SlotIndex == w.SlotIndex)
                {
                    e.Write = w;
                    e.HasWrite = true;
                    TryComplete(ref e);
                }
            }
        }

        while (presenter.Records.TryDequeue(out var p))
        {
            if (p.PeriodNs > 0)
            {
                _periodNs = p.PeriodNs;
            }

            var timedShow = p.Timed && p.ShownNs > 0;
            if (p.Kind == PresentKind.PreRoll)
            {
                // The pre-roll's black frames are not slots of the playback, but their shows are neighbours.
                JudgeUntimed(timedShow, p.ShownNs);
                continue;
            }

            if (p.SlotIndex < 0)
            {
                OnFiller(p);
                JudgeUntimed(timedShow, p.ShownNs);
                continue;
            }

            ref var e = ref Get(p.SlotIndex);
            if (e.SlotIndex == p.SlotIndex)
            {
                e.Present = p;
                e.HasPresent = true;
                if (p.Untimed && _pendingCount < PendingCapacity)
                {
                    e.AwaitingNeighbour = true;
                    e.PreviousTimedNs = _lastTimedShowNs;
                    _pendingUntimed[_pendingCount++] = p.SlotIndex;
                }

                TryComplete(ref e);
            }
            else
            {
                // Its decision was flushed already (or it is a pre-roll present): count the show alone.
                Show(p, false);
            }

            JudgeUntimed(timedShow, p.ShownNs);
        }
    }

    /// <summary>
    /// A timed show judges the untimed presents waiting before it: an untimed present is missed when the timed show before
    /// it or this one took its refresh (first pixel within half a refresh of it), and unjudged otherwise (there is nothing
    /// to say where it went). <paramref name="timedShow"/> false: nothing to judge with.
    /// </summary>
    private void JudgeUntimed(bool timedShow, long shownNs)
    {
        if (!timedShow)
        {
            return;
        }

        for (var i = 0; i < _pendingCount; i++)
        {
            ref var e = ref Get(_pendingUntimed[i]);
            if (e.SlotIndex != _pendingUntimed[i] || !e.AwaitingNeighbour)
            {
                continue;
            }

            var target = e.Decision.RefreshNs;
            var half = _periodNs / 2;
            var taken = (e.PreviousTimedNs > 0 && Math.Abs(e.PreviousTimedNs - target) <= half) || Math.Abs(shownNs - target) <= half;
            e.AwaitingNeighbour = false;
            e.UntimedMiss = taken;
            e.Unjudged = !taken;
            TryComplete(ref e);
        }

        _pendingCount = 0;
        _lastTimedShowNs = shownNs;
    }

    /// <summary>Completes whatever is still open (the end of the run).</summary>
    public void Flush()
    {
        for (var i = 0; i < _pendingCount; i++)
        {
            ref var e = ref Get(_pendingUntimed[i]);
            if (e.SlotIndex == _pendingUntimed[i] && e.AwaitingNeighbour)
            {
                e.AwaitingNeighbour = false;
                e.Unjudged = true; // no timed show came after it
            }
        }

        _pendingCount = 0;

        for (var i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].SlotIndex >= 0)
            {
                Complete(ref _entries[i]);
            }
        }
    }

    private ref Entry Get(long slot) => ref _entries[(int)(slot & _mask)];

    private void OnDecision(in DecisionRecord d)
    {
        var slot = d.Decision.SlotIndex;
        ref var e = ref Get(slot);
        if (e.SlotIndex >= 0)
        {
            // The entry's previous slot never completed (a record lost, or a decision whose present never came).
            Complete(ref e);
        }

        if (_measureFromNs == long.MaxValue && d.Decision.Kind is SlotKind.Real or SlotKind.Generated)
        {
            // The first slot that shows a frame starts playback (decisions come in order); the window starts a warm-up later.
            _originNs = d.RefreshNs;
            _measureFromNs = _originNs + _warmupNs;
            FirstShownFrame = d.Decision.FrameA;
        }

        e = default;
        e.SlotIndex = slot;
        e.Decision = d;
        e.HasDecision = true;
        _newestSlot = Math.Max(_newestSlot, slot);
        TryComplete(ref e);
    }

    private void TryComplete(ref Entry e)
    {
        if (!e.HasDecision)
        {
            return;
        }

        var needsWrite = e.Decision.Outcome == DecisionOutcome.Written;
        var needsPresent = e.Decision.Outcome is DecisionOutcome.Written or DecisionOutcome.Repeat;
        if ((needsWrite && !e.HasWrite) || (needsPresent && !e.HasPresent) || e.AwaitingNeighbour)
        {
            return;
        }

        Complete(ref e);
    }

    private void Complete(ref Entry e)
    {
        if (!e.HasDecision)
        {
            e.SlotIndex = -1;
            return;
        }

        var d = e.Decision.Decision;
        var refresh = e.Decision.RefreshNs;
        var measured = refresh >= _measureFromNs;
        var missed = false;
        if (measured)
        {
            var s = Statistics;
            if (s.FirstMeasuredNs == 0)
            {
                s.FirstMeasuredNs = refresh;
            }

            s.LastMeasuredNs = Math.Max(s.LastMeasuredNs, refresh);
            s.Slots++;
            switch (d.Kind)
            {
                case SlotKind.Real:
                    s.Real++;
                    break;
                case SlotKind.Generated:
                    s.Generated++;
                    break;
                case SlotKind.Repeat:
                    s.Repeated++;
                    break;
                default:
                    s.Empty++;
                    break;
            }

            switch (d.Reason)
            {
                case SlotReason.Late:
                    s.LateDecisions++;
                    break;
                case SlotReason.Starved:
                    s.StarvedSlots++;
                    break;
                case SlotReason.Hold:
                    s.HoldSlots++;
                    break;
                case SlotReason.Discontinuity:
                    s.DiscontinuitySlots++;
                    break;
                case SlotReason.Fallback:
                    s.FallbackSlots++;
                    break;
                default:
                    break;
            }

            if (d.Kind != SlotKind.Empty)
            {
                s.PipelineDelays.Add(d.PipelineDelayNs);
                s.PresentLeads.Add(e.Decision.PresentLeadNs);
            }

            s.MaxOutstanding = Math.Max(s.MaxOutstanding, e.Decision.Outstanding);
            if (d.SkippedSlots > 0)
            {
                s.AddMiss(refresh - _originNs, MissKind.SkippedVblank, (int)Math.Min(d.SkippedSlots, int.MaxValue));
                missed = true;
            }

            if (e.Decision.Outcome == DecisionOutcome.SkippedBackPressure)
            {
                s.AddMiss(refresh - _originNs, MissKind.BackPressure, 1);
                missed = true;
            }
            else if (e.Decision.Outcome == DecisionOutcome.SkippedPresenterBacklog)
            {
                s.AddMiss(refresh - _originNs, MissKind.PresenterBacklog, 1);
                missed = true;
            }

            if (e.HasWrite)
            {
                var w = e.Write;
                s.WriteHostTimes.Add(w.HostEndNs - w.HostStartNs);
                if (w.Outcome == WriteOutcome.Generated && w.GpuNs > 0)
                {
                    s.GenerationTimes.Add(w.GpuNs);
                }
                else if (w.Outcome is WriteOutcome.Copied or WriteOutcome.Fallback && w.GpuNs > 0)
                {
                    s.CopyTimes.Add(w.GpuNs);
                }
            }
        }

        var unjudged = false;
        if (e.HasPresent)
        {
            var p = e.Present;
            missed |= Show(p, measured);
            if (p.Untimed)
            {
                // Judged from the timed shows around it (JudgeUntimed): missed when one of them took its refresh.
                missed |= e.UntimedMiss;
                unjudged = e.Unjudged;
                if (measured && e.UntimedMiss)
                {
                    Statistics.AddMiss(refresh - _originNs, MissKind.NotShown, 1);
                }

                if (measured && e.Unjudged)
                {
                    Statistics.UnjudgedSlots++;
                }
            }
            if (measured && d.Kind == SlotKind.Real && p.Outcome == PresentOutcome.Shown && p.ShownNs > 0 && !p.Untimed)
            {
                Statistics.RealFrameLatency.Add(p.ShownNs - d.ContentTimeNs);
                if (p.Timed && !p.IsMiss)
                {
                    // Shown at its refresh: what is left after the content time, the lead and D is the stride's rounding
                    // (plus the refresh-to-first-pixel time).
                    Statistics.StrideDelay.Add(p.ShownNs - d.ContentTimeNs - e.Decision.PresentLeadNs - d.PipelineDelayNs);
                }
            }
        }

        var w2 = e.Write;
        var genEnd = e.HasWrite ? w2.GpuEndNs : 0;
        var record = new FrameTimelineRecord
        {
            SlotIndex = d.SlotIndex,
            Kind = d.Kind,
            Reason = d.Reason,
            FrameA = d.FrameA,
            FrameB = d.FrameB,
            Timestep = d.Timestep,
            TargetDisplayNs = refresh,
            ActualDisplayNs = Shown(e),
            IdealContentNs = d.IdealContentTimeNs,
            ContentNs = d.ContentTimeNs,
            PipelineDelayNs = d.PipelineDelayNs,
            Multiplier = e.Decision.Multiplier,
            CaptureNs = e.Decision.CaptureNs,
            ArrivalNs = e.Decision.ArrivalNs,
            DecisionNs = d.DecisionTimeNs,
            GenerationStartNs = genEnd > 0 && w2.GpuNs > 0 ? genEnd - w2.GpuNs : 0,
            GenerationEndNs = genEnd,
            PresentSubmitNs = Submitted(e),
            Missed = missed,
            Unjudged = unjudged,
        };
        _ = _ring.TryWrite(record);
        Keep(new VerifierRow
        {
            Slot = d.SlotIndex,
            Kind = d.Kind switch
            {
                SlotKind.Real => VerifierKind.Real,
                SlotKind.Generated => VerifierKind.Generated,
                SlotKind.Repeat => VerifierKind.Repeat,
                _ => VerifierKind.Empty,
            },
            TargetNs = refresh,
            ActualNs = record.ActualDisplayNs,
            SubmitNs = record.PresentSubmitNs,
            LiveMiss = missed,
            LiveUnjudged = unjudged,
        });
        if (e.HasDecision && ((e.Decision.Outcome == DecisionOutcome.Written && (!e.HasWrite || !e.HasPresent)) || (e.Decision.Outcome == DecisionOutcome.Repeat && !e.HasPresent)))
        {
            RecordsIncomplete++;
        }

        RecordsCompleted++;
        e = default;
        e.SlotIndex = -1;
    }

    /// <summary>
    /// A present's outcome into the statistics; true when its slot missed its refresh. Whose a miss is, the present thread
    /// decided from host-clock facts (<see cref="MissAccounting"/>); intervals come only from shows with a first-pixel time
    /// (or every show, on a surface that reports none).
    /// </summary>
    private bool Show(in PresentRecord p, bool measured)
    {
        if (!measured && p.TargetNs < _measureFromNs && p.ShownNs < _measureFromNs)
        {
            return false;
        }

        var s = Statistics;
        if (p.Outcome == PresentOutcome.Filler)
        {
            s.Fillers++;
        }

        if (p.Outcome is PresentOutcome.DroppedLate or PresentOutcome.DroppedRefused or PresentOutcome.DroppedAbsorbed)
        {
            if (p.IsMiss)
            {
                s.AddMiss(p.TargetNs - _originNs, p.Miss, 1);
            }

            return p.IsMiss;
        }

        if (p.ShownNs == 0)
        {
            return false;
        }

        s.Presents++;
        s.MaxPresentsInFlight = Math.Max(s.MaxPresentsInFlight, p.InFlight);
        if (p.Untimed)
        {
            s.UntimedShows++;
        }
        else
        {
            s.SubmitToShown.Add(p.ShownNs - p.SubmitNs);
            if (p.Timed && p.Outcome != PresentOutcome.Filler && p.PredictedNs > 0)
            {
                s.HandoverLeads.Add(p.HandoverLeadNs);
            }

            if (p.PreviousShownNs > 0)
            {
                var interval = p.ShownNs - p.PreviousShownNs;
                s.OutputIntervals.Add(interval);
                if (p.Timed && p.PeriodNs > 0)
                {
                    var periods = interval / (double)p.PeriodNs;
                    s.TimedIntervals++;
                    s.OffGridIntervals += Math.Abs(periods - Math.Round(periods)) > 0.25 ? 1 : 0;
                }
            }

            if (p.Timed && p.PreviousTimedNs > 0 && p.PeriodNs > 0
                && p.ShownNs - p.PreviousTimedNs > (p.UntimedBefore + 1.25) * p.PeriodNs)
            {
                // Perhaps refreshes between this timed show and the one before that no present accounts for (each untimed
                // show in between may have taken one): counted at the end, with the period then (ExplainGaps).
                if (_gapCount < GapCapacity)
                {
                    _gapFrom[_gapCount] = p.PreviousTimedNs;
                    _gapTo[_gapCount] = p.ShownNs;
                    _gapUntimed[_gapCount++] = p.UntimedBefore;
                }
                else
                {
                    GapsDropped++;
                }
            }
        }

        if (p.IsMiss)
        {
            s.AddMiss(p.TargetNs - _originNs, p.Miss, 1);
        }

        return p.IsMiss;
    }

    private void OnFiller(in PresentRecord p)
    {
        var measured = p.TargetNs >= _measureFromNs;
        var missed = Show(p, measured);
        var shown = p.Untimed ? 0 : p.ShownNs;
        _ = _ring.TryWrite(new FrameTimelineRecord
        {
            SlotIndex = -1,
            Kind = SlotKind.Repeat,
            Reason = SlotReason.Starved,
            FrameA = -1,
            FrameB = -1,
            TargetDisplayNs = p.TargetNs,
            ActualDisplayNs = shown,
            PresentSubmitNs = p.SubmitNs,
            Missed = missed,
        });
        Keep(new VerifierRow { Slot = -1, Kind = VerifierKind.Filler, TargetNs = p.TargetNs, ActualNs = shown, SubmitNs = p.SubmitNs });
    }

    /// <summary>Its first pixel for the record: none for an untimed present (its time is unknown) or one not shown.</summary>
    private static long Shown(in Entry e) => e.HasPresent && !e.Present.Untimed ? e.Present.ShownNs : 0;

    /// <summary>When it was submitted: none for a present that never was (given up, or refused).</summary>
    private static long Submitted(in Entry e) => e.HasPresent && e.Present.Outcome is not (PresentOutcome.DroppedLate or PresentOutcome.DroppedRefused or PresentOutcome.DroppedAbsorbed)
        ? e.Present.SubmitNs
        : 0;

    private void Keep(in VerifierRow row)
    {
        if (_rowCount < _rows.Length)
        {
            _rows[_rowCount++] = row;
        }
        else
        {
            VerifierRowsDropped++;
        }
    }

    /// <summary>Intervals between timed shows that could hold a refresh without a present but did not fit the buffer.</summary>
    public long GapsDropped { get; private set; }

    /// <summary>
    /// The refreshes without a new present between <paramref name="fromNs"/> and <paramref name="toNs"/> (no later than the
    /// last show plus half a refresh, as the verifier judges): between consecutive timed shows, the refreshes the untimed
    /// shows in between cannot account for (<see cref="PlaybackStatistics.RefreshesWithoutPresent"/>); those no missed slot
    /// explains (one scheduled for it, or for the refresh before it) are counted as the pipeline's
    /// (<see cref="MissKind.MissedRefresh"/>). Returns how many. At the end of a run, with the last refresh period.
    /// </summary>
    public int ExplainGaps(long fromNs, long toNs)
    {
        if (_gapCount == 0 || _periodNs <= 0 || fromNs == long.MaxValue)
        {
            return 0;
        }

        var half = _periodNs / 2;
        var lastShow = long.MinValue;
        var missedTargets = new List<long>();
        for (var i = 0; i < _rowCount; i++)
        {
            lastShow = Math.Max(lastShow, _rows[i].ActualNs > 0 ? _rows[i].ActualNs : long.MinValue);
            if (_rows[i].Slot >= 0 && _rows[i].LiveMiss)
            {
                missedTargets.Add(_rows[i].TargetNs);
            }
        }

        toNs = Math.Min(toNs, lastShow == long.MinValue ? long.MinValue : lastShow + half);
        missedTargets.Sort();
        var unexplained = 0;
        for (var g = 0; g < _gapCount; g++)
        {
            if (_gapTo[g] < fromNs || _gapFrom[g] > toNs)
            {
                continue;
            }

            var refreshes = (int)Math.Round((_gapTo[g] - _gapFrom[g]) / (double)_periodNs) - _gapUntimed[g];
            for (var k = 1; k < refreshes; k++)
            {
                Statistics.RefreshesWithoutPresent++;
                var refresh = _gapFrom[g] + (long)Math.Round(k * (double)_periodNs);
                var from = missedTargets.BinarySearch(refresh - _periodNs - half);
                from = from >= 0 ? from : ~from;
                if (from >= missedTargets.Count || missedTargets[from] > refresh + half)
                {
                    unexplained++;
                    Statistics.AddMiss(refresh - _originNs, MissKind.MissedRefresh, 1);
                }
            }
        }

        return unexplained;
    }

    private struct Entry
    {
        public long SlotIndex;
        public bool HasDecision;
        public bool HasWrite;
        public bool HasPresent;
        public DecisionRecord Decision;
        public WriteRecord Write;
        public PresentRecord Present;

        // An untimed present waits for the next timed show to be judged.
        public bool AwaitingNeighbour;
        public long PreviousTimedNs;
        public bool UntimedMiss;
        public bool Unjudged;
    }
}
