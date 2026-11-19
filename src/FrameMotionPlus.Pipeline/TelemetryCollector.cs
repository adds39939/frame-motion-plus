using FrameMotionPlus.Core.Abstractions;
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
    private long _lastKeptTargetNs = long.MinValue;

    // Every slot the live accounting missed and the last show, kept apart from the verifier's rows (which can fill up on a
    // long run): the refreshes without a new present are explained from these over the whole run (fixed refresh only:
    // under variable refresh there are none to explain).
    private long[] _missedTargets = new long[4096];
    private int _missedCount;
    private long _lastShowNs = long.MinValue;

    // What is on screen, from the shows in present order (feedback comes in present order): a dropped repeat is judged
    // by the image the refresh it was meant for showed (the latest timed show at or before it), as the verifier does.
    private const int ScreenCapacity = 16;
    private const long PreRollImage = -2;
    private readonly long[] _screenAt = new long[ScreenCapacity];
    private readonly long[] _screenImage = new long[ScreenCapacity];
    private readonly int[] _screenUntimedAfter = new int[ScreenCapacity];
    private int _screenHead;
    private int _screenCount;
    private long _presentedImage = -1;
    private long _lastScheduledImage = -1;
    private readonly long[] _pendingRepeats = new long[PendingCapacity];
    private int _pendingRepeatCount;

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

    /// <summary>The smoothed latency of real frames from their capture timestamp to their first pixel, warm-up included (the sound follows it).</summary>
    public LatencyFollower Latency { get; } = new();

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

    /// <summary>Variable refresh: shows are not on a refresh grid (no refreshes without a present, no off-grid intervals).</summary>
    public bool Vrr { get; set; }

    /// <summary>The latest slot target among the rows kept for the verifier (long.MinValue before the first).</summary>
    public long LastKeptTargetNs => _lastKeptTargetNs;

    /// <summary>
    /// Missed slots beyond the buffer of their targets (fixed refresh; it holds as many as the verifier's rows, so these
    /// lie beyond what the verifier judged; the live explanation of refreshes after them is incomplete).
    /// </summary>
    public long MissedTargetsDropped { get; private set; }

    /// <summary>
    /// Sizes the verifier's buffer and, on fixed refresh, the missed slots' targets (as many as the rows: every slot the
    /// verifier can judge has room); before playback, no allocation while it runs (set <see cref="Vrr"/> first). Rows are
    /// 40 B and targets 8 B each, in fresh memory: their pages become resident as they are written, not here.
    /// </summary>
    public void ReserveVerifierRows(int rows)
    {
        _rows = GC.AllocateUninitializedArray<VerifierRow>(Math.Max(rows, 1024));
        _missedTargets = Vrr ? [] : GC.AllocateUninitializedArray<long>(_rows.Length);
    }

    public void Drain(PacingLoop pacing, RingWriter writer, PresentLoop presenter)
    {
        while (pacing.Decisions.TryDequeue(out var d))
        {
            Add(d);
        }

        while (writer.Records.TryDequeue(out var w))
        {
            Add(w);
        }

        while (presenter.Records.TryDequeue(out var p))
        {
            Add(p);
        }
    }

    /// <summary>A decision (pacing thread's records, in slot order).</summary>
    internal void Add(in DecisionRecord d) => OnDecision(d);

    /// <summary>A ring write's record (generator thread's, in write order).</summary>
    internal void Add(in WriteRecord w)
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

    /// <summary>A present's record (present thread's: shows in present order, slots not presented when they were given up).</summary>
    internal void Add(in PresentRecord p)
    {
        if (p.PeriodNs > 0)
        {
            _periodNs = p.PeriodNs;
        }

        var timedShow = p.Timed && p.ShownNs > 0;
        OnScreen(p);
        if (p.Kind == PresentKind.PreRoll)
        {
            // The pre-roll's black frames are not slots of the playback, but their shows are neighbours.
            JudgeUntimed(timedShow, p.ShownNs);
            return;
        }

        if (p.SlotIndex < 0)
        {
            OnFiller(p);
            JudgeUntimed(timedShow, p.ShownNs);
            return;
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

            if (p.Kind == PresentKind.Repeat && !p.IsMiss && IsDropped(p.Outcome) && _pendingRepeatCount < PendingCapacity)
            {
                // Given up, not counted as a miss because its refresh shows the image it repeats: the next timed show
                // after that refresh says whether it did.
                e.AwaitingScreen = true;
                _pendingRepeats[_pendingRepeatCount++] = p.SlotIndex;
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

    private static bool IsDropped(PresentOutcome o) => o is PresentOutcome.DroppedLate or PresentOutcome.DroppedRefused or PresentOutcome.DroppedAbsorbed;

    /// <summary>
    /// Follows the image on screen through the presents in present order: a write shows its own slot, a repeat or filler the
    /// last write presented before it. Each timed show first settles the given-up repeats whose refresh lies more than half a
    /// refresh before it: that refresh showed the latest timed show's image at or before it, unless a present without a time
    /// came after that show (it may have been shown in between: not judged, as the verifier does not).
    /// </summary>
    private void OnScreen(in PresentRecord p)
    {
        if (IsDropped(p.Outcome))
        {
            return;
        }

        var image = p.Kind switch
        {
            PresentKind.Write => p.SlotIndex,
            PresentKind.PreRoll => PreRollImage,
            _ => _presentedImage,
        };
        if (p.Kind != PresentKind.Repeat)
        {
            _presentedImage = image;
        }

        if (p.Untimed)
        {
            if (_screenCount > 0)
            {
                _screenUntimedAfter[(_screenHead + _screenCount - 1) % ScreenCapacity]++;
            }

            return;
        }

        if (!p.Timed || p.ShownNs <= 0)
        {
            return;
        }

        SettleRepeats(p.ShownNs);
        if (_screenCount == ScreenCapacity)
        {
            _screenHead = (_screenHead + 1) % ScreenCapacity;
            _screenCount--;
        }

        var at = (_screenHead + _screenCount) % ScreenCapacity;
        _screenAt[at] = p.ShownNs;
        _screenImage[at] = image;
        _screenUntimedAfter[at] = 0;
        _screenCount++;
    }

    private void SettleRepeats(long showNs)
    {
        var half = _periodNs / 2;
        var kept = 0;
        for (var i = 0; i < _pendingRepeatCount; i++)
        {
            var slot = _pendingRepeats[i];
            ref var e = ref Get(slot);
            if (e.SlotIndex != slot || !e.AwaitingScreen)
            {
                continue;
            }

            var refresh = e.Decision.RefreshNs;
            if (showNs <= refresh + half)
            {
                _pendingRepeats[kept++] = slot;
                continue;
            }

            // The latest timed show at or before its refresh (plus half a refresh), and whether an untimed present came after it.
            var found = -1;
            for (var k = _screenCount - 1; k >= 0; k--)
            {
                var j = (_screenHead + k) % ScreenCapacity;
                if (_screenAt[j] <= refresh + half)
                {
                    found = j;
                    break;
                }
            }

            e.AwaitingScreen = false;
            e.ScreenMiss = found >= 0 && _screenUntimedAfter[found] == 0 && e.ScheduledImage >= 0 && _screenImage[found] != e.ScheduledImage;
            TryComplete(ref e);
        }

        _pendingRepeatCount = kept;
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
        for (var i = 0; i < _pendingRepeatCount; i++)
        {
            ref var e = ref Get(_pendingRepeats[i]);
            if (e.SlotIndex == _pendingRepeats[i])
            {
                e.AwaitingScreen = false; // no show came after its refresh: the run stopped first (the verifier does not judge it either)
            }
        }

        _pendingRepeatCount = 0;

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

        TrackQuality(d);
        e = default;
        e.SlotIndex = slot;
        e.Decision = d;
        e.HasDecision = true;
        if (d.Decision.Kind is SlotKind.Real or SlotKind.Generated)
        {
            _lastScheduledImage = slot;
        }

        e.ScheduledImage = _lastScheduledImage;
        _newestSlot = Math.Max(_newestSlot, slot);
        TryComplete(ref e);
    }

    private FrameGenQuality _lastQuality = FrameGenQuality.Default;
    private bool _awaitingBoundary;
    private bool _switchOpen;
    private long _switchFromNs = long.MaxValue;
    private long _switchToNs = long.MinValue;

    /// <summary>
    /// Quality switches, from the decisions (in slot order): the first slot decided while a switch is prepared opens its
    /// window (a dearer quality's D rises then), the first slot decided at the new quality fixes its boundary, and the
    /// window lasts <see cref="PlaybackStatistics.SwitchWindowNs"/> after that; a switch called off drops its window. Slots
    /// in a window are counted for it and kept out of the per-quality latency and D.
    /// </summary>
    private void TrackQuality(in DecisionRecord d)
    {
        if (d.Quality == FrameGenQuality.Default)
        {
            return;
        }

        var measured = _measureFromNs != long.MaxValue && d.RefreshNs >= _measureFromNs;
        if (d.SwitchPending && !_awaitingBoundary && measured)
        {
            _ = Statistics.OpenSwitch(d.QualityEpoch + 1, d.Quality, FrameGenQuality.Default, d.RefreshNs - _originNs, long.MinValue, out _switchOpen);
            _awaitingBoundary = true;
            _switchFromNs = d.RefreshNs;
            _switchToNs = long.MaxValue;
        }

        if (_lastQuality != FrameGenQuality.Default && d.Quality != _lastQuality && measured)
        {
            if (!_awaitingBoundary)
            {
                // Committed in the turn it was asked for (a cheaper quality): no slot was decided while it was prepared.
                _ = Statistics.OpenSwitch(d.QualityEpoch, _lastQuality, d.Quality, d.RefreshNs - _originNs, d.RefreshNs - _originNs, out _switchOpen);
                _switchFromNs = d.RefreshNs;
            }

            if (_switchOpen)
            {
                ref var w = ref Statistics.LastSwitch;
                w.Epoch = d.QualityEpoch;
                w.To = d.Quality;
                w.BoundaryNs = d.RefreshNs - _originNs;
            }

            _awaitingBoundary = false;
            _switchToNs = d.RefreshNs + PlaybackStatistics.SwitchWindowNs;
        }
        else if (_awaitingBoundary && !d.SwitchPending)
        {
            // Called off (Q again before it was made): no switch, no window.
            if (_switchOpen)
            {
                Statistics.DropLastSwitch();
            }

            _awaitingBoundary = false;
            _switchOpen = false;
            _switchFromNs = long.MaxValue;
            _switchToNs = long.MinValue;
        }

        _lastQuality = d.Quality;
    }

    /// <summary>A measured slot into the quality statistics: its switch window, or its quality's latency and D.</summary>
    private void CountQuality(in Entry e, bool missed)
    {
        var d = e.Decision;
        if (d.Quality == FrameGenQuality.Default)
        {
            return;
        }

        var s = Statistics;
        var refresh = d.RefreshNs;
        if (e.HasWrite && e.Write.Outcome == WriteOutcome.Generated && e.Write.GpuNs > 0)
        {
            s.GenerationTimesAt(e.Write.Quality).Add(e.Write.GpuNs);
        }

        var inWindow = refresh >= _switchFromNs && refresh < _switchToNs;
        if (inWindow)
        {
            if (_switchOpen)
            {
                ref var w = ref s.LastSwitch;
                w.Slots++;
                w.Real += d.Decision.Kind == SlotKind.Real ? 1 : 0;
                w.Generated += d.Decision.Kind == SlotKind.Generated ? 1 : 0;
                w.Missed += missed ? 1 : 0;
                w.LateDecisions += d.Decision.Reason == SlotReason.Late ? 1 : 0;
                w.VrrSkipped += Vrr ? d.Decision.SkippedSlots : 0;
                w.Fallbacks += e.HasWrite && e.Write.Outcome == WriteOutcome.Fallback ? 1 : 0;
            }

            return;
        }

        if (d.Decision.Kind != SlotKind.Empty)
        {
            s.PipelineDelaysAt(d.Quality).Add(d.Decision.PipelineDelayNs);
        }

        if (d.Decision.Kind == SlotKind.Real && e.HasPresent && e.Present is { Outcome: PresentOutcome.Shown, ShownNs: > 0, Untimed: false } p)
        {
            s.RealFrameLatencyAt(d.Quality).Add(p.ShownNs - d.Decision.ContentTimeNs);
        }
    }

    private void TryComplete(ref Entry e)
    {
        if (!e.HasDecision)
        {
            return;
        }

        var needsWrite = e.Decision.Outcome == DecisionOutcome.Written;
        var needsPresent = e.Decision.Outcome is DecisionOutcome.Written or DecisionOutcome.Repeat;
        if ((needsWrite && !e.HasWrite) || (needsPresent && !e.HasPresent) || e.AwaitingNeighbour || e.AwaitingScreen)
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
                case SlotReason.Delayed when d.Kind == SlotKind.Generated:
                    s.VrrDelayedSlots++;
                    s.VrrDelayedTotalNs += d.DelayedByNs;
                    s.VrrDelayedMaxNs = Math.Max(s.VrrDelayedMaxNs, d.DelayedByNs);
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
            if (d.SkippedSlots > 0 && Vrr)
            {
                // Variable refresh has no refresh that goes without a slot: a generated slot the scheduler skipped (the
                // next real frame came too late for it) leaves the image before it on screen longer. Counted apart.
                s.VrrSkippedSlots += d.SkippedSlots;
                s.VrrSkippedInLongIntervals += d.SkippedInLongIntervals;
            }
            else if (d.SkippedSlots > 0)
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
                    // Whose: the present thread's attribution (a present handed over in time that the compositor
                    // discarded is the display's, and so is its give-back after a display-late show).
                    Statistics.AddMiss(refresh - _originNs, p.Miss is MissKind.Discarded or MissKind.CatchUp or MissKind.RateChange ? p.Miss : MissKind.NotShown, 1);
                }

                if (measured && e.Unjudged)
                {
                    Statistics.UnjudgedSlots++;
                }
            }

            if (e.ScreenMiss)
            {
                // A repeat given up whose refresh showed another image (the one it repeats was never shown there).
                missed = true;
                if (measured)
                {
                    Statistics.AddMiss(refresh - _originNs, p.Miss, 1);
                }
            }
            if (d.Kind == SlotKind.Real && p.Outcome == PresentOutcome.Shown && p.ShownNs > 0 && !p.Untimed)
            {
                Latency.Add(p.ShownNs - d.ContentTimeNs, p.ShownNs);
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

        if (measured)
        {
            CountQuality(e, missed);
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
                if (p.Timed && p.PeriodNs > 0 && !Vrr)
                {
                    var periods = interval / (double)p.PeriodNs;
                    s.TimedIntervals++;
                    s.OffGridIntervals += Math.Abs(periods - Math.Round(periods)) > 0.25 ? 1 : 0;
                }
            }

            if (p.Timed && p.PreviousTimedNs > 0 && p.PeriodNs > 0 && !Vrr
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
        if (row.ActualNs > 0)
        {
            _lastShowNs = Math.Max(_lastShowNs, row.ActualNs);
        }

        if (row.Slot >= 0 && row.LiveMiss && !Vrr)
        {
            if (_missedCount < _missedTargets.Length)
            {
                _missedTargets[_missedCount++] = row.TargetNs;
            }
            else
            {
                MissedTargetsDropped++;
            }
        }

        if (_rowCount < _rows.Length)
        {
            _rows[_rowCount++] = row;
            _lastKeptTargetNs = Math.Max(_lastKeptTargetNs, row.TargetNs);
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
    /// (<see cref="MissKind.MissedRefresh"/>). Returns how many; <paramref name="unexplainedThrough"/> is how many of them
    /// lie in intervals starting at or before <paramref name="compareToNs"/> (the span the verifier judged, to compare with
    /// its count, which takes an interval that starts in its span). At the end of a run, with the last refresh period.
    /// </summary>
    public int ExplainGaps(long fromNs, long toNs, long compareToNs, out int unexplainedThrough)
    {
        unexplainedThrough = 0;
        if (_gapCount == 0 || _periodNs <= 0 || fromNs == long.MaxValue)
        {
            return 0;
        }

        var half = _periodNs / 2;
        var missedTargets = _missedTargets[.._missedCount];
        Array.Sort(missedTargets);
        toNs = Math.Min(toNs, _lastShowNs == long.MinValue ? long.MinValue : _lastShowNs + half);
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
                var from = Array.BinarySearch(missedTargets, refresh - _periodNs - half);
                from = from >= 0 ? from : ~from;
                if (from >= missedTargets.Length || missedTargets[from] > refresh + half)
                {
                    unexplained++;
                    unexplainedThrough += _gapFrom[g] <= compareToNs ? 1 : 0; // as the verifier: a gap that starts in its span
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

        // The last real or generated slot scheduled at or before it (what a repeat shows), and a given-up repeat waiting for
        // the show after its refresh.
        public long ScheduledImage;
        public bool AwaitingScreen;
        public bool ScreenMiss;
    }
}
