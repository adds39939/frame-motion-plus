using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Scheduling;

/// <summary>
/// Display-time frame scheduler. For each output slot with target display time V it computes the content
/// time τ = V − D, finds the unique real frames N and N+1 that bracket τ, and answers with either a real
/// frame or the generated frame at t = (τ − tsₙ) / (tsₙ₊₁ − tsₙ). Timesteps near 0 or 1 show the real
/// frame. The one rule covers integer and non-integer ratios, 59.94 sources on 120.00 Hz panels and uneven
/// frame times.
/// <para>
/// Usage, on one thread: feed unique frames with <see cref="OnFrameArrived"/> as they become available (or
/// earlier, with their future arrival time), report measured generation times with
/// <see cref="ReportGenerationTime"/>, wait until <see cref="NextDecisionTime"/>, then call
/// <see cref="Schedule"/>. Waking late is tolerated: the lateness is measured and added to the decision lead.
/// <see cref="NextDecisionTime"/> has no side effects. All per-slot and per-frame methods are allocation-free.
/// </para>
/// </summary>
public sealed class FrameScheduler
{
    private const double RatioTolerance = 1e-6;

    // Grid instants are floored to whole nanoseconds, so slot offsets that are exact multiples of the refresh
    // period can come out a nanosecond short; quantisation and its hold use this one tolerance everywhere.
    private const long QuantisationToleranceNs = 1_000;

    // Wake-lateness samples are clipped so a rare stall cannot dominate the lead.
    private const long MaxWakeSampleNs = 5_000_000;

    private readonly ClockMode _mode;
    private readonly double _epsilon;
    private readonly int _maxMultiplier;
    private readonly double _holdFactor;
    private readonly int _startupFrames;
    private readonly double _fallbackHeadroom;
    private readonly long _fallbackDwellNs;
    private readonly long _marginNs;
    private readonly long _alignGuardNs;
    private readonly double _percentile;
    private readonly double _vrrTolerance;

    private readonly UniqueFrameTimeline _timeline;
    private readonly SourceIntervalEstimator _intervals;
    private readonly RollingPercentile _arrivalLags;
    private readonly RollingPercentile _wakeLateness;
    private readonly PipelineDelayTuner _tuner;
    private readonly PhaseCoherenceEstimator _phase;

    private FrameRate _displayRate;
    private double _periodNs;
    private long _vblankAnchorNs;

    private long _lastContentNs = long.MinValue;
    private bool _started;
    private bool _pacingInitialized;

    private int _stride = 1;
    private int _multiplier = 1;
    private long _relaxSinceNs = long.MinValue;
    private bool _generationEnabled;
    private bool _cadenceDirty = true;
    private int _slotGcd = 1;
    private int _maxCadenceSlots = 1;
    private long _intervalNs;
    private long _holdIntervalNs;
    private long _holdNs;
    private long _arrivalLagNs;
    private long _wakeLagNs;
    private long _generationLeadNs;
    private long _realLeadNs;
    private long _leadNs;
    private long _targetNs;
    private long _alignmentNs;
    private long _coherentSnapNs;
    private double _alignPhaseNs = double.NaN;

    private long _vblankIndex = -1;
    private long _slotCounter;
    private long _lastTargetNs = long.MinValue;
    private long _lastDecisionNs = long.MinValue;
    private long _lastGenerationTargetNs = long.MinValue;
    private long _enableSinceNs = long.MinValue;
    private long _cursor = -1;

    private bool _hasPrevious;
    private long _prevA;
    private long _prevB;
    private double _prevT;
    private long _prevContentNs = long.MinValue;

    private long _vrrBase = -1;
    private int _vrrK;
    private int _vrrSlots;

    private long _framesAccepted;
    private long _framesRejected;
    private long _slots;
    private long _empty;
    private long _real;
    private long _generated;
    private long _repeated;
    private long _skipped;
    private long _starvedSlots;
    private long _lateSlots;
    private long _holdSlots;
    private long _discontinuitySlots;
    private long _fallbackSlots;
    private long _holdsDetected;
    private long _discontinuities;
    private long _framesDroppedLate;

    public FrameScheduler(SchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        _mode = options.ClockMode;
        _epsilon = options.SnapEpsilon;
        _maxMultiplier = options.MaxMultiplier;
        _holdFactor = options.HoldIntervalFactor;
        _startupFrames = options.StartupFrames;
        _fallbackHeadroom = options.FallbackHeadroom;
        _fallbackDwellNs = options.FallbackDwellNs;
        _marginNs = options.MarginNs;
        _alignGuardNs = options.MarginNs / 2;
        _percentile = options.GenerationTimePercentile;
        _vrrTolerance = options.VrrRateTolerance;

        _timeline = new UniqueFrameTimeline(options.TimelineCapacity);
        _intervals = new SourceIntervalEstimator(
            options.SourceIntervalWindow,
            options.HoldIntervalFactor,
            options.DropIntervalFactor,
            options.SourceIntervalPercentile,
            options.MaxInterpolatedIntervalNs,
            options.HoldMinSamples,
            options.ReseedAfterConsecutiveHolds);
        _arrivalLags = new RollingPercentile(options.SourceIntervalWindow);
        _wakeLateness = new RollingPercentile(options.GenerationTimeWindow);
        _tuner = new PipelineDelayTuner(
            options.GenerationTimeWindow,
            options.GenerationTimePercentile,
            options.InitialGenerationTimeNs,
            options.MarginNs,
            options.SlewUpNsPerSecond,
            options.SlewDownNsPerSecond,
            options.DelayDeadbandNs,
            options.GenerationTimeMaxAgeNs);
        _phase = new PhaseCoherenceEstimator(
            options.CoherenceWindow,
            options.CoherenceMinFrames,
            options.CoherenceMaxDriftPpm,
            options.CoherenceMaxJitterNs);

        SetDisplayTiming(options.VblankAnchorNs, options.DisplayRate);
    }

    public SchedulerOptions Options { get; }

    public ClockMode ClockMode => _mode;

    /// <summary>The pipeline delay D currently in use, ns.</summary>
    public long PipelineDelayNs => _tuner.CurrentNs;

    public UniqueFrameTimeline Timeline => _timeline;

    /// <summary>Frames older than this id are no longer referenced by any decision and may be released.</summary>
    public long OldestReferencedFrameId
    {
        get
        {
            var seq = _mode == ClockMode.Vrr ? _vrrBase : _cursor;
            return _timeline.Contains(seq) ? _timeline[seq].FrameId : -1;
        }
    }

    public SchedulerCounters Counters => new()
    {
        FramesAccepted = _framesAccepted,
        FramesRejected = _framesRejected,
        Slots = _slots,
        Empty = _empty,
        Real = _real,
        Generated = _generated,
        Repeated = _repeated,
        Skipped = _skipped,
        StarvedSlots = _starvedSlots,
        LateSlots = _lateSlots,
        HoldSlots = _holdSlots,
        DiscontinuitySlots = _discontinuitySlots,
        FallbackSlots = _fallbackSlots,
        HoldsDetected = _holdsDetected,
        Discontinuities = _discontinuities,
        FramesDroppedLate = _framesDroppedLate,
        Reseeds = _intervals.Reseeds,
    };

    public SchedulerDiagnostics Diagnostics => new()
    {
        ClockMode = _mode,
        SourceIntervalNs = _intervalNs,
        HoldIntervalNs = _holdIntervalNs,
        HoldNs = _holdNs,
        ArrivalLagNs = _arrivalLagNs,
        WakeLatenessNs = _wakeLagNs,
        LeadNs = _leadNs,
        GenerationTimeNs = _tuner.GenerationTimeNs,
        DelayTargetNs = _targetNs,
        PipelineDelayNs = _tuner.CurrentNs,
        AlignmentNs = _alignmentNs,
        GenerationEnabled = _generationEnabled,
        PhaseCoherent = _mode == ClockMode.FixedRefresh && _phase.IsCoherent,
        PhaseDriftPpm = _phase.DriftPpm,
        PhaseJitterNs = _phase.JitterNs,
        Stride = _mode == ClockMode.FixedRefresh ? _stride : 1,
        Multiplier = EffectiveMultiplier(),
        OutputRatio = _mode == ClockMode.FixedRefresh
            ? (_periodNs > 0 ? _intervalNs / _periodNs : 0)
            : _multiplier,
    };

    /// <summary>
    /// Adds a unique real frame. Frames must come in strictly increasing content-time order; others are rejected
    /// (returns false). <see cref="FrameArrival.ArrivalTimeNs"/> may lie in the future (decode-ahead): the frame
    /// is not used before then. A gap classified as a hold marks the frame so it is never interpolated across.
    /// </summary>
    public bool OnFrameArrived(in FrameArrival frame)
    {
        if (frame.ContentTimeNs <= _lastContentNs)
        {
            _framesRejected++;
            return false;
        }

        var marks = TimelineMarks.None;
        if (frame.Discontinuity)
        {
            marks |= TimelineMarks.DiscontinuityBefore;
            _discontinuities++;
            _intervals.Reset();
            _phase.Reset();
        }
        else if (_lastContentNs != long.MinValue && _intervals.Add(frame.ContentTimeNs - _lastContentNs))
        {
            marks |= TimelineMarks.HoldBefore;
            _holdsDetected++;
        }

        _timeline.Add(new TimelineFrame(frame.FrameId, frame.ContentTimeNs, frame.ArrivalTimeNs, marks));
        _arrivalLags.Add(Math.Max(0, frame.ArrivalTimeNs - frame.ContentTimeNs));
        if (_mode == ClockMode.FixedRefresh)
        {
            _phase.Add(frame.ContentTimeNs);
        }

        _cadenceDirty = true;
        _lastContentNs = frame.ContentTimeNs;
        _framesAccepted++;
        return true;
    }

    /// <summary>Reports the measured GPU time of the generation enqueued for the last decision.</summary>
    public void ReportGenerationTime(long durationNs) =>
        _tuner.AddGenerationSample(durationNs, _lastDecisionNs == long.MinValue ? 0 : _lastDecisionNs);

    /// <summary>Re-anchors the fixed-refresh vblank grid (from present feedback) or changes the VRR maximum rate.</summary>
    public void UpdateDisplayTiming(long vblankAnchorNs, FrameRate refreshRate)
    {
        SetDisplayTiming(vblankAnchorNs, refreshRate);
        if (_lastTargetNs != long.MinValue)
        {
            // The slot just scheduled is the vblank of the corrected grid nearest to it, so the next slot is the one
            // after that; flooring instead would schedule the same physical vblank twice after a small positive shift.
            _vblankIndex = _displayRate.TickIndexAtOrBefore(_lastTargetNs - _vblankAnchorNs + (long)(_periodNs / 2));
        }
    }

    /// <summary>
    /// When the caller should next call <see cref="Schedule"/>, ns; <see cref="long.MaxValue"/> means wait for a
    /// frame. Pure: calling it any number of times does not change any decision.
    /// </summary>
    public long NextDecisionTime(long nowNs)
    {
        if (_mode == ClockMode.FixedRefresh)
        {
            // The very first slot is decided at once (Schedule then takes the next vblank). Deriving it from "now" here
            // would let every frame delivery push it a vblank further and, with a late enough caller, never happen.
            return _lastTargetNs == long.MinValue ? nowNs : VblankTime(_vblankIndex + 1) - (_started ? _leadNs : _marginNs);
        }

        if (!_started)
        {
            return _framesAccepted >= _startupFrames ? nowNs : long.MaxValue;
        }

        var baseSeq = _vrrBase;
        var k = _vrrK;
        var slots = _vrrSlots;
        if (!_timeline.Contains(baseSeq))
        {
            // Schedule starts at the newest frame that has arrived; until one has, wake when the first arrives.
            if (_timeline.IsEmpty)
            {
                return long.MaxValue;
            }

            return NewestArrivedAtOrAfter(_timeline.FirstSequence, nowNs, long.MaxValue) >= 0
                ? nowNs
                : _timeline[_timeline.FirstSequence].ArrivalTimeNs;
        }

        ResolveVrr(ref baseSeq, ref k, ref slots);
        if (baseSeq > _timeline.LastSequence || baseSeq < _timeline.FirstSequence)
        {
            return long.MaxValue;
        }

        ref readonly var n = ref _timeline[baseSeq];
        if (k == 0)
        {
            return Math.Max(n.ArrivalTimeNs, VrrRealTarget(n.ContentTimeNs, long.MinValue) - _realLeadNs);
        }

        if (baseSeq + 1 > _timeline.LastSequence)
        {
            return long.MaxValue;
        }

        ref readonly var m = ref _timeline[baseSeq + 1];
        var v = VrrGeneratedTarget(n.ContentTimeNs, m.ContentTimeNs, k, slots);
        return Math.Max(m.ArrivalTimeNs, v - _generationLeadNs);
    }

    /// <summary>Decides the next output slot using the frames that have arrived by <paramref name="nowNs"/>.</summary>
    public SlotDecision Schedule(long nowNs)
    {
        // Wake lateness is measured against exactly what NextDecisionTime promised (it is pure, so asking again is
        // safe), before this call changes any lead or delay.
        var planned = NextDecisionTime(nowNs);
        return _mode == ClockMode.FixedRefresh ? ScheduleFixed(nowNs, planned) : ScheduleVrr(nowNs, planned);
    }

    private SlotDecision ScheduleFixed(long now, long plannedDecisionNs)
    {
        var first = _lastTargetNs == long.MinValue;
        var index = first ? FirstVblankIndexAfter(now) : _vblankIndex + 1;
        long skipped = 0;
        if (VblankTime(index) <= now)
        {
            var caughtUp = FirstVblankIndexAfter(now);
            skipped = caughtUp - index;
            index = caughtUp;
        }

        var v = VblankTime(index);
        if (!first && _pacingInitialized)
        {
            // Skipped decisions are sampled too (clipped): if a constant lateness made every decision skip and only
            // on-time ones were sampled, the lateness would never be learnt and the skipping would never stop.
            RecordWake(now, plannedDecisionNs);
        }

        _vblankIndex = index;
        var elapsed = first ? 0 : v - _lastTargetNs;
        _lastTargetNs = v;
        _lastDecisionNs = now;
        _skipped += skipped;

        if (!EnsureStarted())
        {
            return EmitEmpty(v, now, SlotReason.Startup, skipped);
        }

        RefreshPacing(now, elapsed);
        var d = _tuner.CurrentNs;
        var tau = v - d;

        var seqN = _timeline.FindAtOrBefore(tau, _cursor);
        while (seqN >= _timeline.FirstSequence && _timeline[seqN].ArrivalTimeNs > now)
        {
            seqN--;
        }

        if (seqN < _timeline.FirstSequence)
        {
            return EmitEmpty(v, now, SlotReason.Startup, skipped);
        }

        _cursor = seqN;
        ref readonly var n = ref _timeline[seqN];
        var hasNext = seqN < _timeline.LastSequence && _timeline[seqN + 1].ArrivalTimeNs <= now;

        if (!hasNext)
        {
            // The newest usable frame is all there is. That is starvation only if this slot needed the next frame.
            var gap = tau - n.ContentTimeNs;
            var reason = NeedsNextFrame(gap, n.BreaksContinuity)
                ? (gap > _holdFactor * _intervalNs ? SlotReason.Hold : SlotReason.Starved)
                : SlotReason.None;
            return EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, reason, skipped);
        }

        ref readonly var m = ref _timeline[seqN + 1];
        var interval = m.ContentTimeNs - n.ContentTimeNs;
        var offset = tau - n.ContentTimeNs;
        var snap = SnapNs(interval);

        if (m.BreaksContinuity)
        {
            // Never interpolate across a hold or cut; the next frame is shown once τ reaches it, within the snap of a
            // normal interval (the gap itself may be seconds long).
            if (interval - offset <= SnapNs(_intervalNs))
            {
                return EmitReal(v, tau, now, d, m.FrameId, m.ContentTimeNs, SlotReason.None, skipped);
            }

            var reason = (m.Marks & TimelineMarks.DiscontinuityBefore) != 0 ? SlotReason.Discontinuity : SlotReason.Hold;
            return EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, reason, skipped);
        }

        if (!_generationEnabled)
        {
            return interval - offset <= snap
                ? EmitReal(v, tau, now, d, m.FrameId, m.ContentTimeNs, SlotReason.None, skipped)
                : EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, SlotReason.None, skipped);
        }

        var contentOffset = offset;
        if (_stride > 1)
        {
            // Fallback: quantise content time to a grid of Stride vblanks measured from frame N, so generations
            // are at least Stride slots apart. The hold and NeedsNextFrame use the same threshold.
            var bucket = _stride * _periodNs;
            contentOffset = (long)Math.Round(Math.Floor((offset + QuantisationToleranceNs) / bucket) * bucket);
        }

        if (contentOffset <= snap)
        {
            return EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, SlotReason.None, skipped);
        }

        if (interval - contentOffset <= snap)
        {
            return EmitReal(v, tau, now, d, m.FrameId, m.ContentTimeNs, SlotReason.None, skipped);
        }

        var t = contentOffset / (double)interval;
        if (!IsGeneratableTimestep(t))
        {
            return t < 0.5
                ? EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, SlotReason.None, skipped)
                : EmitReal(v, tau, now, d, m.FrameId, m.ContentTimeNs, SlotReason.None, skipped);
        }

        if (_stride > 1 && _lastGenerationTargetNs != long.MinValue
            && v - _lastGenerationTargetNs < (_stride * _periodNs) - (_periodNs / 2))
        {
            // Hard guarantee of the GPU budget: never two generations closer than Stride vblanks. Kept as a time, not
            // a vblank index, so re-anchoring the grid (which renumbers vblanks) cannot stall it.
            return EmitRepeat(v, tau, now, d, SlotReason.Fallback, skipped);
        }

        if (now > v - _tuner.GenerationLeadNs - (_marginNs / 2))
        {
            // Too late for generation to finish by this vblank (a late wake, the first slot after a stall, or the slot
            // where generation switches on): show the newest real frame instead of a frame that would miss.
            return EmitReal(v, tau, now, d, n.FrameId, n.ContentTimeNs, SlotReason.Late, skipped);
        }

        var decision = EmitGenerated(v, tau, now, d, n.FrameId, m.FrameId, t, n.ContentTimeNs + contentOffset, SlotReason.None, skipped);
        if (decision.Kind == SlotKind.Generated)
        {
            _lastGenerationTargetNs = v;
        }

        return decision;
    }

    private SlotDecision ScheduleVrr(long now, long plannedDecisionNs)
    {
        if (!EnsureStarted())
        {
            return EmitEmpty(now, now, SlotReason.Startup, 0);
        }

        var elapsed = _lastDecisionNs == long.MinValue ? 0 : now - _lastDecisionNs;
        _lastDecisionNs = now;

        // Every wake counts, whatever the outcome: if late wakes made every slot skip and only emitted slots were
        // sampled, the lateness would never be learnt and the skipping would never stop.
        if (_pacingInitialized)
        {
            RecordWake(now, plannedDecisionNs);
        }

        RefreshPacing(now, elapsed);
        var d = _tuner.CurrentNs;

        if (!_timeline.Contains(_vrrBase))
        {
            // Start (or recover after falling behind the timeline) at the newest usable frame: lowest latency.
            _vrrBase = NewestArrivedAtOrAfter(_timeline.FirstSequence, now, long.MaxValue);
            _vrrK = 0;
            _vrrSlots = 0;
            if (_vrrBase < 0)
            {
                return EmitEmpty(now, now, SlotReason.Startup, 0);
            }
        }

        long skipped = 0;
        while (true)
        {
            ResolveVrr(ref _vrrBase, ref _vrrK, ref _vrrSlots);
            if (_vrrBase > _timeline.LastSequence)
            {
                _skipped += skipped;
                return EmitEmpty(now, now, SlotReason.Starved, skipped);
            }

            if (_vrrK == 0)
            {
                ref readonly var n0 = ref _timeline[_vrrBase];
                if (n0.ArrivalTimeNs > now)
                {
                    _skipped += skipped;
                    return EmitEmpty(now, now, SlotReason.Starved, skipped);
                }

                var ideal = VrrRealTarget(n0.ContentTimeNs, long.MinValue);
                var v = Math.Max(ideal, now);

                // No queued frames: if presenting N this late means a newer frame's own display time has already
                // come, show the newest such frame instead and drop the ones in between.
                var newest = NewestArrivedAtOrAfter(_vrrBase, now, v - d);
                if (newest > _vrrBase)
                {
                    _framesDroppedLate += newest - _vrrBase;
                    _vrrBase = newest;
                    ideal = VrrRealTarget(_timeline[newest].ContentTimeNs, long.MinValue);
                    v = Math.Max(ideal, now);
                }

                ref readonly var n = ref _timeline[_vrrBase];
                _vrrK = 1;
                _vrrSlots = 0;
                _lastTargetNs = v;
                _cursor = _vrrBase;
                _skipped += skipped;
                return EmitReal(v, v - d, now, d, n.FrameId, n.ContentTimeNs, SlotReason.None, skipped);
            }

            ref readonly var a = ref _timeline[_vrrBase];
            ref readonly var b = ref _timeline[_vrrBase + 1];
            if (b.ArrivalTimeNs > now)
            {
                var reason = now - a.ContentTimeNs - d > _holdFactor * _intervalNs ? SlotReason.Hold : SlotReason.Starved;
                _skipped += skipped;
                return EmitEmpty(now, now, reason, skipped);
            }

            var planned = VrrPlannedGenerated(a.ContentTimeNs, b.ContentTimeNs, _vrrK, _vrrSlots);
            var target = VrrGeneratedTarget(a.ContentTimeNs, b.ContentTimeNs, _vrrK, _vrrSlots);
            var nextReal = b.ContentTimeNs + d;

            // Lateness may eat the wake allowance and half the margin; generation (p99 of d) plus the other half,
            // kept against d outliers, must still fit, and the slot must not crowd the next real frame.
            if (now > target - _tuner.GenerationLeadNs - (_marginNs / 2) || target > nextReal - MinimumSpacingNs())
            {
                skipped++;
                _starvedSlots++;
                _vrrK++;
                continue;
            }

            var interval = b.ContentTimeNs - a.ContentTimeNs;
            var t = _vrrK / (double)_vrrSlots;
            var content = a.ContentTimeNs + (long)Math.Round(_vrrK * (double)interval / _vrrSlots);
            _vrrK++;
            _lastTargetNs = target;
            _cursor = _vrrBase;
            _skipped += skipped;
            return EmitGenerated(target, content, now, d, a.FrameId, b.FrameId, t, content, SlotReason.None, skipped);
        }
    }

    /// <summary>
    /// Advances a VRR slot position past exhausted intervals and intervals that must not be interpolated.
    /// Operates on the given variables only, so <see cref="NextDecisionTime"/> can use it without side effects.
    /// </summary>
    private void ResolveVrr(ref long baseSeq, ref int k, ref int slots)
    {
        while (k > 0 && baseSeq + 1 <= _timeline.LastSequence)
        {
            ref readonly var n = ref _timeline[baseSeq];
            ref readonly var m = ref _timeline[baseSeq + 1];
            if (m.BreaksContinuity)
            {
                baseSeq++;
                k = 0;
                slots = 0;
                continue;
            }

            if (slots == 0)
            {
                slots = VrrSlotsForInterval(m.ContentTimeNs - n.ContentTimeNs);
            }

            if (!_generationEnabled || k >= slots)
            {
                baseSeq++;
                k = 0;
                slots = 0;
                continue;
            }

            return;
        }
    }

    /// <summary>Newest frame at or after <paramref name="fromSeq"/> that has arrived and whose content is at or before
    /// <paramref name="contentLimitNs"/>; −1 if there is none.</summary>
    private long NewestArrivedAtOrAfter(long fromSeq, long now, long contentLimitNs)
    {
        var best = -1L;
        for (var s = Math.Max(fromSeq, _timeline.FirstSequence); s <= _timeline.LastSequence; s++)
        {
            ref readonly var f = ref _timeline[s];
            if (f.ArrivalTimeNs > now || f.ContentTimeNs > contentLimitNs)
            {
                break;
            }

            best = s;
        }

        return best;
    }

    /// <summary>Slots for one source interval: keeps the output rate near M / T even when frame times are uneven.</summary>
    private int VrrSlotsForInterval(long intervalNs)
    {
        if (_intervalNs <= 0)
        {
            return 1;
        }

        var slots = (int)Math.Round(intervalNs * (double)_multiplier / _intervalNs);
        var maxBySpacing = Math.Max(1, FloorToInt(intervalNs * (1.0 + _vrrTolerance) / _periodNs));
        return Math.Clamp(slots, 1, maxBySpacing);
    }

    private long MinimumSpacingNs() => (long)Math.Floor(_periodNs * (1.0 - _vrrTolerance));

    /// <summary>VRR real slot: V = ts + D, but never closer than the panel's minimum refresh period to the last slot.</summary>
    private long VrrRealTarget(long contentNs, long notBefore)
    {
        var v = contentNs + _tuner.CurrentNs;
        if (_lastTargetNs != long.MinValue)
        {
            v = Math.Max(v, _lastTargetNs + MinimumSpacingNs());
        }

        return Math.Max(v, notBefore);
    }

    private long VrrPlannedGenerated(long nContentNs, long mContentNs, int k, int slots)
    {
        var interval = mContentNs - nContentNs;
        slots = slots > 0 ? slots : VrrSlotsForInterval(interval);
        return nContentNs + _tuner.CurrentNs + (long)Math.Round(k * (double)interval / slots);
    }

    /// <summary>VRR generated slot: V = ts + D + kT/M, spaced from the last slot by the refresh limit and by p99(d)
    /// so the serial GPU queue is free when its generation starts.</summary>
    private long VrrGeneratedTarget(long nContentNs, long mContentNs, int k, int slots)
    {
        var planned = VrrPlannedGenerated(nContentNs, mContentNs, k, slots);
        if (_lastTargetNs != long.MinValue)
        {
            planned = Math.Max(planned, _lastTargetNs + Math.Max(MinimumSpacingNs(), _tuner.GenerationLeadNs));
        }

        return planned;
    }

    /// <summary>
    /// Whether a fixed-refresh slot whose content time is <paramref name="gap"/> after the newest frame N would
    /// have used frame N+1, given snapping, fallback quantisation and whether generation is enabled.
    /// </summary>
    private bool NeedsNextFrame(long gap, bool newestFollowsBreak)
    {
        if (gap <= SnapNs(_intervalNs))
        {
            return false;
        }

        if (newestFollowsBreak && gap < _intervalNs)
        {
            return false; // first frame after a hold or cut: the lag is just its new phase
        }

        if (!_generationEnabled)
        {
            return gap >= _intervalNs - SnapNs(_intervalNs);
        }

        return _stride <= 1 || gap + QuantisationToleranceNs >= _stride * _periodNs;
    }

    /// <summary>Distance from a real frame within which it is shown instead of a generated frame.</summary>
    private long SnapNs(long intervalNs) => Math.Max((long)(_epsilon * intervalNs), _coherentSnapNs);

    /// <summary>A generator is only ever asked for t strictly inside (0, 1), also after narrowing to float. The snaps
    /// normally keep t well away from 0 and 1; this is the last guard if a configuration (SnapEpsilon = 0, a source
    /// slower than 30 fps) puts a slot within a nanosecond of a frame.</summary>
    internal static bool IsGeneratableTimestep(double t)
    {
        var f = (float)t;
        return f > 0f && f < 1f;
    }

    private void RecordWake(long now, long plannedDecisionNs)
    {
        if (plannedDecisionNs != long.MaxValue)
        {
            _wakeLateness.Add(Math.Clamp(now - plannedDecisionNs, 0, MaxWakeSampleNs));
        }
    }

    private bool EnsureStarted()
    {
        if (!_started && _framesAccepted >= _startupFrames)
        {
            _started = true;
        }

        return _started;
    }

    /// <summary>Recomputes T, the multiplier or stride, the hold, the leads and the D target, then slews D.</summary>
    private void RefreshPacing(long now, long elapsedNs)
    {
        if (_intervals.NominalNs > 0)
        {
            _intervalNs = _intervals.NominalNs;
            _holdIntervalNs = _intervals.HoldIntervalNs;
        }

        _tuner.Expire(now);
        _wakeLagNs = _wakeLateness.Percentile(_percentile);
        _arrivalLagNs = _arrivalLags.Percentile(_percentile);
        _generationLeadNs = _tuner.GenerationLeadNs + _wakeLagNs + _marginNs;
        _realLeadNs = _wakeLagNs + _marginNs;

        // Whether generation fits is judged on the optimistic estimate (so a retry happens); the lead and D use the
        // budgeting one (so the retry's generation is decided early enough to finish).
        var gateLead = _tuner.GenerationTimeNs + _wakeLagNs + _marginNs;
        var hold = _mode == ClockMode.FixedRefresh
            ? RefreshFixedStride(now, gateLead)
            : RefreshVrrMultiplier(now, gateLead);

        _holdNs = (long)Math.Round(hold);
        _leadNs = _generationEnabled ? _generationLeadNs : _realLeadNs;

        // A slot decided at V − lead needs frame N+1 to have arrived: ts(N+1) + lag ≤ τ + D − lead.
        var baseDelay = _holdNs + _arrivalLagNs + _leadNs;
        var target = baseDelay;

        if (_mode == ClockMode.FixedRefresh && _phase.IsCoherent)
        {
            // Smallest D ≥ base + guard with τ = V − D on the source grid: since V ≡ anchor and ts ≡ anchor + phase
            // (mod P), that is D ≡ −phase (mod P), and real frames land exactly on vblanks. The guard keeps the
            // arrival check satisfied while D slews to the next aligned value as the two clocks drift apart.
            var x = (double)(baseDelay + _alignGuardNs);
            var aligned = x + PhaseCoherenceEstimator.PositiveMod(-AlignmentPhase() - x, _periodNs);
            target = (long)Math.Round(aligned);
        }
        else
        {
            _alignPhaseNs = double.NaN;
        }

        _alignmentNs = target - baseDelay;
        _targetNs = target;
        _tuner.Step(target, elapsedNs);
        _pacingInitialized = true;
    }

    /// <summary>
    /// The source phase the alignment uses. It follows the estimate with hysteresis: the fitted phase wobbles by tens of
    /// microseconds with timestamp jitter, and chasing that would make D oscillate. Real clock skew moves the phase
    /// steadily, so D still follows it, in small steps in one direction.
    /// </summary>
    private double AlignmentPhase()
    {
        var estimate = _phase.PhaseNs;
        var hysteresis = Math.Max(100_000.0, _phase.JitterNs / 2);
        if (double.IsNaN(_alignPhaseNs))
        {
            _alignPhaseNs = estimate;
        }
        else
        {
            var diff = PhaseCoherenceEstimator.PositiveMod(estimate - _alignPhaseNs + (_periodNs / 2), _periodNs) - (_periodNs / 2);
            if (Math.Abs(diff) > hysteresis)
            {
                _alignPhaseNs = estimate;
            }
        }

        return _alignPhaseNs;
    }

    private double RefreshFixedStride(long now, long leadGen)
    {
        var p = _periodNs;
        var nominal = (double)_intervalNs;
        var coherent = _phase.IsCoherent;
        int fStrict, fRelax, coverSlots;

        // Capture timestamps jitter around the grid: real frames still snap, and the hold covers the late ones.
        _coherentSnapNs = coherent ? (long)Math.Min(1.5 * _phase.JitterNs, p / 4) : 0;

        if (coherent)
        {
            if (_cadenceDirty)
            {
                _slotGcd = _intervals.SlotGcd(p, out _maxCadenceSlots);
                _cadenceDirty = false;
            }

            var nominalSlots = Math.Max(1, (int)Math.Round(nominal / p));
            coverSlots = Math.Max(1, (int)Math.Round(_holdIntervalNs / p));
            var fMin = Math.Max(1, CeilToInt(nominalSlots / (double)_maxMultiplier));
            fStrict = StrideFor(_slotGcd, Math.Max(fMin, CeilToInt(leadGen / p)));
            fRelax = StrideFor(_slotGcd, Math.Max(fMin, CeilToInt(leadGen / (p * (1.0 - _fallbackHeadroom)))));
        }
        else
        {
            var ratio = nominal / p;
            var fMin = Math.Max(1, CeilToInt(ratio / _maxMultiplier));
            fStrict = Math.Max(fMin, CeilToInt(leadGen / p));
            fRelax = Math.Max(fMin, CeilToInt(leadGen / (p * (1.0 - _fallbackHeadroom))));
            coverSlots = 0;
        }

        var strideInvalid = coherent && _slotGcd % _stride != 0 && _stride % _slotGcd != 0;
        if (!_pacingInitialized || strideInvalid || fStrict > _stride)
        {
            _stride = fStrict;
            _relaxSinceNs = long.MinValue;
        }
        else if (fRelax < _stride)
        {
            if (_relaxSinceNs == long.MinValue)
            {
                _relaxSinceNs = now;
            }
            else if (now - _relaxSinceNs >= _fallbackDwellNs)
            {
                _stride = fRelax;
                _relaxSinceNs = long.MinValue;
            }
        }
        else
        {
            _relaxSinceNs = long.MinValue;
        }

        // The on/off gate needs headroom and a dwell to switch on (it switches off at once), so a source near the
        // display rate cannot flap between generating and not, dragging D up and down with it.
        var canGenerate = _maxMultiplier >= 2
            && (coherent ? _stride < coverSlots : _stride * p < (1.0 - _epsilon) * nominal);
        var canGenerateWithHeadroom = _maxMultiplier >= 2
            && (coherent ? _stride < coverSlots : _stride * p < (1.0 - _epsilon) * (1.0 - _fallbackHeadroom) * nominal);
        _generationEnabled = GateWithDwell(canGenerate, canGenerateWithHeadroom, now);

        if (!_generationEnabled)
        {
            return 0;
        }

        // Coherent: generated slots sit at least Stride vblanks after frame N (less the timestamp jitter), so N+1 is
        // needed by τ + (T − Stride·P + jitter). Non-coherent without fallback: τ can be as little as one snap after
        // N, so the hold approaches (1 − ε)·T. Non-coherent fallback: Stride·P after N, less the tolerance.
        if (coherent)
        {
            return Math.Max(0, _holdIntervalNs - (_stride * p) + _phase.JitterNs);
        }

        return _stride > 1
            ? Math.Max(0, _holdIntervalNs - (_stride * p) + QuantisationToleranceNs)
            : (1.0 - _epsilon) * _holdIntervalNs;
    }

    private bool GateWithDwell(bool allowed, bool allowedWithHeadroom, long now)
    {
        if (!allowed)
        {
            _enableSinceNs = long.MinValue;
            return false;
        }

        if (_generationEnabled || !_pacingInitialized)
        {
            return _generationEnabled || allowedWithHeadroom;
        }

        if (!allowedWithHeadroom)
        {
            _enableSinceNs = long.MinValue;
            return false;
        }

        if (_enableSinceNs == long.MinValue)
        {
            _enableSinceNs = now;
        }

        return now - _enableSinceNs >= _fallbackDwellNs;
    }

    private double RefreshVrrMultiplier(long now, long leadGen)
    {
        var t = (double)_intervalNs;
        var ratio = t / _periodNs;
        var mMaxStrict = Math.Clamp(FloorToInt(ratio * (1.0 + _vrrTolerance)), 1, _maxMultiplier);
        var mMaxRelax = Math.Clamp(FloorToInt(ratio * (1.0 + (_vrrTolerance / 2))), 1, _maxMultiplier);
        var mStrict = Math.Clamp(FloorToInt(t / leadGen), 1, mMaxStrict);
        var mRelax = Math.Clamp(FloorToInt(t * (1.0 - _fallbackHeadroom) / leadGen), 1, mMaxRelax);

        if (!_pacingInitialized || mStrict < _multiplier)
        {
            _multiplier = mStrict;
            _relaxSinceNs = long.MinValue;
        }
        else if (mRelax > _multiplier)
        {
            if (_relaxSinceNs == long.MinValue)
            {
                _relaxSinceNs = now;
            }
            else if (now - _relaxSinceNs >= _fallbackDwellNs)
            {
                _multiplier = mRelax;
                _relaxSinceNs = long.MinValue;
            }
        }
        else
        {
            _relaxSinceNs = long.MinValue;
        }

        _generationEnabled = _multiplier >= 2;
        if (!_generationEnabled)
        {
            return 0;
        }

        // The first generated slot of the longest covered interval sits one of its slot spacings after the real frame.
        // An interval longer than T gets more slots (spacing stays about T/M), so this is not simply (M − 1)/M · T_hold.
        var slots = VrrSlotsForInterval(_holdIntervalNs);
        return slots >= 2 ? (double)_holdIntervalNs * (slots - 1) / slots : 0;
    }

    private int EffectiveMultiplier()
    {
        if (_mode == ClockMode.Vrr)
        {
            return _multiplier;
        }

        if (!_generationEnabled || _periodNs <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Round(Math.Max(_intervalNs, _holdIntervalNs) / (_periodNs * _stride)));
    }

    private SlotDecision EmitReal(long v, long tau, long now, long d, long frameId, long contentNs, SlotReason reason, long skipped)
    {
        if (_hasPrevious && (contentNs < _prevContentNs || (_prevA == frameId && _prevB == frameId)))
        {
            return EmitRepeat(v, tau, now, d, RepeatReason(reason), skipped);
        }

        CountReason(reason);
        _real++;
        return Emit(SlotKind.Real, reason, v, tau, now, d, frameId, frameId, 0, contentNs, skipped);
    }

    private SlotDecision EmitGenerated(long v, long tau, long now, long d, long a, long b, double t, long contentNs, SlotReason reason, long skipped)
    {
        if (_hasPrevious && (contentNs <= _prevContentNs || (_prevA == a && _prevB == b && _prevT == t)))
        {
            return EmitRepeat(v, tau, now, d, RepeatReason(reason), skipped);
        }

        CountReason(reason);
        _generated++;
        return Emit(SlotKind.Generated, reason, v, tau, now, d, a, b, t, contentNs, skipped);
    }

    private SlotDecision EmitRepeat(long v, long tau, long now, long d, SlotReason reason, long skipped)
    {
        CountReason(reason);
        _repeated++;
        return Emit(SlotKind.Repeat, reason, v, tau, now, d, _prevA, _prevB, _prevT, _prevContentNs, skipped);
    }

    private SlotDecision EmitEmpty(long v, long now, SlotReason reason, long skipped)
    {
        _empty++;
        _slots++;
        return new SlotDecision
        {
            SlotIndex = _slotCounter++,
            Kind = SlotKind.Empty,
            Reason = reason,
            TargetTimeNs = v,
            IdealContentTimeNs = v - _tuner.CurrentNs,
            ContentTimeNs = _hasPrevious ? _prevContentNs : 0,
            DecisionTimeNs = now,
            FrameA = -1,
            FrameB = -1,
            PipelineDelayNs = _tuner.CurrentNs,
            SkippedSlots = skipped,
        };
    }

    private SlotDecision Emit(SlotKind kind, SlotReason reason, long v, long tau, long now, long d, long a, long b, double t, long contentNs, long skipped)
    {
        _hasPrevious = true;
        _prevA = a;
        _prevB = b;
        _prevT = t;
        _prevContentNs = contentNs;
        _slots++;
        return new SlotDecision
        {
            SlotIndex = _slotCounter++,
            Kind = kind,
            Reason = reason,
            TargetTimeNs = v,
            IdealContentTimeNs = tau,
            ContentTimeNs = contentNs,
            DecisionTimeNs = now,
            FrameA = a,
            FrameB = b,
            Timestep = t,
            PipelineDelayNs = d,
            SkippedSlots = skipped,
        };
    }

    private SlotReason RepeatReason(SlotReason reason)
    {
        if (reason != SlotReason.None)
        {
            return reason;
        }

        return !_generationEnabled || _stride > 1 ? SlotReason.Fallback : SlotReason.None;
    }

    private void CountReason(SlotReason reason)
    {
        switch (reason)
        {
            case SlotReason.Starved:
                _starvedSlots++;
                break;
            case SlotReason.Hold:
                _holdSlots++;
                break;
            case SlotReason.Discontinuity:
                _discontinuitySlots++;
                break;
            case SlotReason.Fallback:
                _fallbackSlots++;
                break;
            case SlotReason.Late:
                _lateSlots++;
                break;
            default:
                break;
        }
    }

    private void SetDisplayTiming(long vblankAnchorNs, FrameRate refreshRate)
    {
        if (!refreshRate.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshRate), "Refresh rate must be set.");
        }

        _displayRate = refreshRate;
        _periodNs = refreshRate.PeriodNs;
        _vblankAnchorNs = vblankAnchorNs;
        _phase.SetGrid(vblankAnchorNs, _periodNs);
        _cadenceDirty = true;
    }

    private long VblankTime(long index) => _vblankAnchorNs + _displayRate.TickTimeNs(index);

    private long FirstVblankIndexAfter(long timeNs) => _displayRate.TickIndexAtOrBefore(timeNs - _vblankAnchorNs) + 1;

    private static int CeilToInt(double value) => (int)Math.Ceiling(value - RatioTolerance);

    private static int FloorToInt(double value) => (int)Math.Floor(value + RatioTolerance);

    /// <summary>Smallest divisor of <paramref name="gcd"/> that is at least <paramref name="atLeast"/>, else the
    /// smallest multiple of it: every on-grid interval then gets its generated frames at the same slots.</summary>
    private static int StrideFor(int gcd, int atLeast)
    {
        atLeast = Math.Max(1, atLeast);
        for (var f = atLeast; f <= gcd; f++)
        {
            if (gcd % f == 0)
            {
                return f;
            }
        }

        return gcd * (int)Math.Ceiling(atLeast / (double)gcd);
    }
}
