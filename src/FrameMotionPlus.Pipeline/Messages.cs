using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>What the generator thread writes into a ring slot.</summary>
public enum WriteKind : byte
{
    /// <summary>A solid fill (the pre-roll's black frames).</summary>
    Fill = 0,

    /// <summary>A real frame copied in.</summary>
    Real = 1,

    /// <summary>The generator's frame at a timestep between two real frames.</summary>
    Generated = 2,

    /// <summary>
    /// No write: switch the generator to <see cref="WriteJob.Quality"/> (an instance loaded at start, so no load and no CUDA
    /// call), between the writes queued before it and those after it. <see cref="WriteJob.SlotIndex"/> is the switch's epoch.
    /// </summary>
    Quality = 3,
}

/// <summary>One ring write, handed from the pacing thread to the generator thread (in write order).</summary>
public readonly record struct WriteJob
{
    public long SlotIndex { get; init; }

    public WriteKind Kind { get; init; }

    public BridgeSlot Slot { get; init; }

    /// <summary>Real: the frame. Generated: the earlier frame.</summary>
    public GpuFrame Prev { get; init; }

    /// <summary>Generated: the later frame.</summary>
    public GpuFrame Curr { get; init; }

    public float Timestep { get; init; }

    /// <summary><see cref="WriteKind.Quality"/>: the quality to switch to.</summary>
    public FrameGenQuality Quality { get; init; }
}

/// <summary>Generator thread → pacing thread: one generation's GPU time, and the quality epoch it ran in.</summary>
internal readonly record struct GenerationSample(long Nanoseconds, int Epoch);

/// <summary>Generator thread → control thread: a quality switch carried out (or refused) between two writes.</summary>
internal readonly record struct QualityApplied
{
    public int Epoch { get; init; }

    public FrameGenQuality Quality { get; init; }

    public bool Switched { get; init; }

    /// <summary>Host time of the switch (CLOCK_MONOTONIC).</summary>
    public long AtNs { get; init; }

    /// <summary>How long the switch call took on the generator thread.</summary>
    public long CallNs { get; init; }

    /// <summary>The last write enqueued before it (every later write runs at the new quality).</summary>
    public ulong AfterWrite { get; init; }
}

/// <summary>What the present thread shows for one output slot.</summary>
public enum PresentKind : byte
{
    /// <summary>A ring write (a real or generated frame).</summary>
    Write = 0,

    /// <summary>The scheduler's Repeat: the last image again (keeps the FIFO fed one present per refresh).</summary>
    Repeat = 1,

    /// <summary>A pre-roll frame: shown as soon as possible, to put the window on an output and learn its refreshes.</summary>
    PreRoll = 2,
}

/// <summary>One present, handed from the pacing thread (or the pre-roll) to the present thread, in slot order.</summary>
public readonly record struct PresentJob
{
    public long SlotIndex { get; init; }

    public PresentKind Kind { get; init; }

    /// <summary>For <see cref="PresentKind.Write"/> and pre-roll presents: the ring write's request.</summary>
    public PresentRequest Request { get; init; }

    public ulong Write { get; init; }

    /// <summary>The refresh it is for: the scheduler's V plus the present lead (fixed refresh: the refresh; variable: the
    /// first pixel expected).</summary>
    public long TargetNs { get; init; }

    /// <summary>The scheduler's V: when its content is due. Variable refresh hands the present over then.</summary>
    public long DueNs { get; init; }

    public SlotKind SlotKind { get; init; }

    public long FrameA { get; init; }

    public long FrameB { get; init; }

    public double Timestep { get; init; }

    public long ContentNs { get; init; }

    public long ContentA { get; init; }

    public long ContentB { get; init; }

    /// <summary>FrameB follows a discontinuity (a loop, seek or timestamp jump): the pair must never be interpolated.</summary>
    public bool BreakBeforeB { get; init; }
}

/// <summary>
/// The display's refresh grid as present feedback shows it, and the present lead (how long before its refresh a slot's
/// content must be ready; it adapts to the output), from the present thread to the pacing thread.
/// </summary>
internal readonly record struct DisplayUpdate(long AnchorNs, FrameRate Rate, long LeadNs);

/// <summary>What the pacing thread did with a decision.</summary>
internal enum DecisionOutcome : byte
{
    Empty = 0,
    Written = 1,
    Repeat = 2,

    /// <summary>The bridge refused a slot (too many writes unfinished on the GPU): not produced.</summary>
    SkippedBackPressure = 3,

    /// <summary>The present thread was too far behind (a stalled presenter): not produced.</summary>
    SkippedPresenterBacklog = 4,
}

/// <summary>Pacing thread → collector: one scheduler decision and what became of it.</summary>
internal readonly record struct DecisionRecord
{
    public SlotDecision Decision { get; init; }

    public DecisionOutcome Outcome { get; init; }

    public ulong Write { get; init; }

    /// <summary>Content time of the newest real frame used (FrameB), 0 for none.</summary>
    public long CaptureNs { get; init; }

    /// <summary>When FrameB became available (the decode thread published it).</summary>
    public long ArrivalNs { get; init; }

    public int Multiplier { get; init; }

    /// <summary>Writes the bridge had handed out and the GPU had not finished, right after this one.</summary>
    public long Outstanding { get; init; }

    /// <summary>The present lead: the slot's refresh is <see cref="SlotDecision.TargetTimeNs"/> plus this.</summary>
    public long PresentLeadNs { get; init; }

    /// <summary>The generator quality this slot was decided for (committed switches: its write runs at it).</summary>
    public FrameGenQuality Quality { get; init; }

    /// <summary>A quality switch was being prepared when this slot was decided (D rising for a dearer generator).</summary>
    public bool SwitchPending { get; init; }

    /// <summary>Quality switches committed before this slot was decided.</summary>
    public int QualityEpoch { get; init; }

    /// <summary>The refresh this slot is shown at (the scheduler's V is that minus the present lead).</summary>
    public long RefreshNs => Decision.TargetTimeNs + PresentLeadNs;
}

/// <summary>What the generator thread wrote.</summary>
internal enum WriteOutcome : byte
{
    Fill = 0,
    Copied = 1,
    Generated = 2,

    /// <summary>A generated slot written with the nearest real frame: the generator threw or is faulted.</summary>
    Fallback = 3,
}

/// <summary>Generator thread → collector: one ring write, once its GPU work has finished.</summary>
internal readonly record struct WriteRecord
{
    public long SlotIndex { get; init; }

    public ulong Write { get; init; }

    public WriteOutcome Outcome { get; init; }

    /// <summary>Host time the generator thread started the job.</summary>
    public long HostStartNs { get; init; }

    /// <summary>Host time the job's last enqueue returned (for VFG with shot-change detection: after the generation).</summary>
    public long HostEndNs { get; init; }

    /// <summary>GPU span of the generation or copy itself (behind its waits), ns; 0 when not timed.</summary>
    public long GpuNs { get; init; }

    /// <summary>GPU time the write was signalled, on CLOCK_MONOTONIC (anchored CUDA events); 0 when unknown.</summary>
    public long GpuEndNs { get; init; }

    /// <summary>The generator's quality when the write was enqueued.</summary>
    public FrameGenQuality Quality { get; init; }
}

/// <summary>What became of a present.</summary>
internal enum PresentOutcome : byte
{
    /// <summary>Shown (present feedback).</summary>
    Shown = 0,

    /// <summary>The present thread inserted the last image again because nothing was ready for a refresh.</summary>
    Filler = 1,

    /// <summary>Not presented: it would have been shown after its refresh (the FIFO was behind).</summary>
    DroppedLate = 2,

    /// <summary>Not presented: the presenter kept refusing it until its refresh had passed.</summary>
    DroppedRefused = 3,

    /// <summary>
    /// Not presented: a repeat of the real frame shown a refresh late just before it, so its refresh showed that image
    /// anyway (catching up cost no image).
    /// </summary>
    DroppedAbsorbed = 4,

    /// <summary>Submitted knowing it would be shown a refresh late (a new real frame, kept rather than dropped).</summary>
    ShownLate = 5,

    /// <summary>A probe of the compositor's latch before playback (the last pre-roll image, handed over at a chosen time).</summary>
    Probe = 6,
}

/// <summary>Present thread → collector: a present that was shown, or a slot that was not presented.</summary>
internal readonly record struct PresentRecord
{
    public long SlotIndex { get; init; }

    public PresentKind Kind { get; init; }

    public PresentOutcome Outcome { get; init; }

    public ulong Write { get; init; }

    public long TargetNs { get; init; }

    public long SubmitNs { get; init; }

    /// <summary>The refresh the present thread expected it to be shown at.</summary>
    public long PredictedNs { get; init; }

    /// <summary>
    /// When it was shown (0 when not shown): its first pixel's time where the presenter reports it (present timing), else
    /// when its completion was seen.
    /// </summary>
    public long ShownNs { get; init; }

    /// <summary>When the present thread saw its completion reported (present-wait), whatever <see cref="ShownNs"/> is.</summary>
    public long ReportedNs { get; init; }

    /// <summary>True when <see cref="ShownNs"/> is the first-pixel time from the presenter, not the report's.</summary>
    public bool Timed { get; init; }

    /// <summary>When the present shown before it was reported shown (0 for the first, or when that one had no time).</summary>
    public long PreviousShownNs { get; init; }

    /// <summary>The last show with a first-pixel time before it (0 for none): with <see cref="UntimedBefore"/>, which refreshes in between had no present.</summary>
    public long PreviousTimedNs { get; init; }

    /// <summary>Shows without a first-pixel time since <see cref="PreviousTimedNs"/> (each may have taken a refresh in between).</summary>
    public int UntimedBefore { get; init; }

    /// <summary>The refresh period the present thread was working with.</summary>
    public long PeriodNs { get; init; }

    /// <summary>Presents in flight (submitted, not yet reported shown) right after it was submitted.</summary>
    public int InFlight { get; init; }

    /// <summary>When the present thread first saw its content written (a repeat or filler: when it was submitted), ns.</summary>
    public long ReadyNs { get; init; }

    /// <summary>The refresh it was meant for minus max(submitted, written), ns (shown presents with a first-pixel time).</summary>
    public long HandoverLeadNs { get; init; }

    /// <summary>The hand-over lead it needed to count as in time when it was judged (long.MaxValue: no evidence), ns.</summary>
    public long RequiredLeadNs { get; init; }

    /// <summary>The present lead at the time, ns.</summary>
    public long LeadNs { get; init; }

    /// <summary>Its slot was not shown at its refresh; <see cref="Miss"/> says whose.</summary>
    public bool IsMiss { get; init; }

    public MissKind Miss { get; init; }

    /// <summary>Shown, but without a first-pixel time although the surface reports them: its time is unknown.</summary>
    public bool Untimed { get; init; }
}
