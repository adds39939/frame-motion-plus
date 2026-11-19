namespace FrameMotionPlus.Core.Scheduling;

public enum ClockMode
{
    /// <summary>Output slots are the panel's vblanks.</summary>
    FixedRefresh = 0,

    /// <summary>Output slots follow the source: V = ts + D + kT/M. The display refreshes when we present.</summary>
    Vrr = 1,
}

public enum SlotKind
{
    /// <summary>Nothing to show yet (startup, or VRR waiting for the next frame).</summary>
    Empty = 0,

    /// <summary>Present real frame <see cref="SlotDecision.FrameA"/> unchanged.</summary>
    Real = 1,

    /// <summary>Generate the frame at <see cref="SlotDecision.Timestep"/> between FrameA and FrameB.</summary>
    Generated = 2,

    /// <summary>Keep the previous slot's image. No GPU work; re-present it if the presenter needs a frame.</summary>
    Repeat = 3,
}

/// <summary>Why a slot is not showing the exact display-time content.</summary>
public enum SlotReason
{
    None = 0,

    /// <summary>Fewer than the start-up number of unique frames have arrived.</summary>
    Startup = 1,

    /// <summary>The next real frame had not arrived in time (a late or dropped source frame).</summary>
    Starved = 2,

    /// <summary>No new frame for longer than the hold threshold (menus, pause, loading): generation stopped.</summary>
    Hold = 3,

    /// <summary>The next frame follows a source discontinuity: never interpolated across.</summary>
    Discontinuity = 4,

    /// <summary>Multiplier fallback (or generation disabled) leaves this slot on the previous image.</summary>
    Fallback = 5,

    /// <summary>The decision came too late for generation to finish by the target time, so the newest real frame
    /// at or before τ is shown instead of a generated frame that would miss its slot.</summary>
    Late = 6,

    /// <summary>VRR: the real frame after this generated slot came too late for its planned time, so it is shown up to
    /// <see cref="SchedulerOptions.VrrLateGeneratedNs"/> later instead of being skipped (its content moved by the same
    /// amount, so τ = V − D still holds; <see cref="SlotDecision.DelayedByNs"/> says by how much).</summary>
    Delayed = 7,
}

/// <summary>A unique source frame as the scheduler sees it. Only timestamps and ids, never pixels.</summary>
public readonly record struct FrameArrival(long FrameId, long ContentTimeNs, long ArrivalTimeNs, bool Discontinuity = false);

/// <summary>The scheduler's answer for one output slot.</summary>
public readonly record struct SlotDecision
{
    /// <summary>Monotonic output slot counter.</summary>
    public long SlotIndex { get; init; }

    public SlotKind Kind { get; init; }

    public SlotReason Reason { get; init; }

    /// <summary>Target display time V (first pixel), ns.</summary>
    public long TargetTimeNs { get; init; }

    /// <summary>The ideal content time τ = V − D for this slot.</summary>
    public long IdealContentTimeNs { get; init; }

    /// <summary>The content time actually shown: τ for exact generated frames, the frame time for real frames.</summary>
    public long ContentTimeNs { get; init; }

    /// <summary>When the decision was made, ns.</summary>
    public long DecisionTimeNs { get; init; }

    /// <summary>Earlier frame id (for Real and Repeat of a real frame: the frame shown).</summary>
    public long FrameA { get; init; }

    /// <summary>Later frame id (equal to FrameA for real frames).</summary>
    public long FrameB { get; init; }

    /// <summary>Timestep t in (0, 1) for generated frames; 0 for real frames.</summary>
    public double Timestep { get; init; }

    /// <summary>Pipeline delay D used for this slot, ns.</summary>
    public long PipelineDelayNs { get; init; }

    /// <summary>Fixed refresh: vblanks that passed without a decision because the caller was late.
    /// VRR: generated slots skipped because their inputs arrived too late.</summary>
    public long SkippedSlots { get; init; }

    /// <summary>VRR: of <see cref="SkippedSlots"/>, those in an interval with more slots than the multiplier (the source
    /// made it longer than its own, by repeating or dropping a frame: the next frame could not have come in time).</summary>
    public long SkippedInLongIntervals { get; init; }

    /// <summary>VRR, <see cref="SlotReason.Delayed"/>: how far after its planned time this generated slot is shown, ns.</summary>
    public long DelayedByNs { get; init; }

    public bool NeedsGeneration => Kind == SlotKind.Generated;
}

/// <summary>Running totals since construction.</summary>
public readonly record struct SchedulerCounters
{
    public long FramesAccepted { get; init; }

    public long FramesRejected { get; init; }

    public long Slots { get; init; }

    public long Empty { get; init; }

    public long Real { get; init; }

    public long Generated { get; init; }

    public long Repeated { get; init; }

    public long Skipped { get; init; }

    public long StarvedSlots { get; init; }

    public long LateSlots { get; init; }

    public long HoldSlots { get; init; }

    public long DiscontinuitySlots { get; init; }

    public long FallbackSlots { get; init; }

    public long HoldsDetected { get; init; }

    public long Discontinuities { get; init; }

    /// <summary>VRR: frames never shown because presenting them would have queued latency (newest frame wins).</summary>
    public long FramesDroppedLate { get; init; }

    /// <summary>Times the interval estimate was re-seeded after the source rate dropped.</summary>
    public long Reseeds { get; init; }

    /// <summary>VRR: of <see cref="Skipped"/>, those in intervals longer than the source's own (see
    /// <see cref="SlotDecision.SkippedInLongIntervals"/>).</summary>
    public long SkippedInLongIntervals { get; init; }

    /// <summary>VRR: generated slots shown late instead of skipped (<see cref="SlotReason.Delayed"/>).</summary>
    public long DelayedSlots { get; init; }
}

/// <summary>Snapshot of the scheduler's derived pacing state, for the HUD, telemetry and tests.</summary>
public readonly record struct SchedulerDiagnostics
{
    public ClockMode ClockMode { get; init; }

    /// <summary>T: median unique-frame interval (holds excluded), ns.</summary>
    public long SourceIntervalNs { get; init; }

    /// <summary>Interval percentile the hold covers, ns.</summary>
    public long HoldIntervalNs { get; init; }

    /// <summary>Hold H: how far behind the newest frame τ must sit so generation inputs exist, ns.</summary>
    public long HoldNs { get; init; }

    /// <summary>p99 of arrival lag (content time to availability: capture, upload, convert), ns.</summary>
    public long ArrivalLagNs { get; init; }

    /// <summary>p99 of how late the caller woke for a decision, ns (added to the lead).</summary>
    public long WakeLatenessNs { get; init; }

    /// <summary>Decision lead: p99(d) + p99(wake lateness) + margin when generating, without p99(d) otherwise, ns.</summary>
    public long LeadNs { get; init; }

    /// <summary>Rolling p99 of generation time d (or the initial estimate), ns.</summary>
    public long GenerationTimeNs { get; init; }

    public long DelayTargetNs { get; init; }

    public long PipelineDelayNs { get; init; }

    /// <summary>Fixed refresh: extra delay over hold + lag + lead that lands real frames exactly on vblanks, ns.</summary>
    public long AlignmentNs { get; init; }

    public bool GenerationEnabled { get; init; }

    /// <summary>Fixed refresh: source timestamps lie on the vblank grid within drift/jitter tolerance.</summary>
    public bool PhaseCoherent { get; init; }

    public double PhaseDriftPpm { get; init; }

    public double PhaseJitterNs { get; init; }

    /// <summary>Fixed refresh: a new image at most every Stride vblanks (1 = no fallback).</summary>
    public int Stride { get; init; }

    /// <summary>Effective multiplier M: output frames per source frame (rounded for non-integer ratios).</summary>
    public int Multiplier { get; init; }

    /// <summary>Output frames per source frame before quantisation (T / P for fixed refresh, M for VRR).</summary>
    public double OutputRatio { get; init; }
}
