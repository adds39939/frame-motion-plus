using FrameMotionPlus.Core.Scheduling;

namespace FrameMotionPlus.Core.Telemetry;

/// <summary>
/// One output slot's timeline: the scheduler's decision plus every timestamp along the path from capture to
/// first pixel. All times are ns on CLOCK_MONOTONIC; 0 means not measured (yet) by this pipeline.
/// </summary>
public readonly record struct FrameTimelineRecord
{
    public long SlotIndex { get; init; }

    public SlotKind Kind { get; init; }

    public SlotReason Reason { get; init; }

    public long FrameA { get; init; }

    public long FrameB { get; init; }

    public double Timestep { get; init; }

    /// <summary>Target display time V.</summary>
    public long TargetDisplayNs { get; init; }

    /// <summary>First pixel out (present feedback, or the simulated display).</summary>
    public long ActualDisplayNs { get; init; }

    public long IdealContentNs { get; init; }

    public long ContentNs { get; init; }

    public long PipelineDelayNs { get; init; }

    public int Multiplier { get; init; }

    /// <summary>Capture timestamp of the newest real frame used (FrameB).</summary>
    public long CaptureNs { get; init; }

    public long DequeueNs { get; init; }

    public long UploadDoneNs { get; init; }

    public long ConvertDoneNs { get; init; }

    /// <summary>When FrameB became available to the scheduler.</summary>
    public long ArrivalNs { get; init; }

    public long DecisionNs { get; init; }

    public long GenerationStartNs { get; init; }

    public long GenerationEndNs { get; init; }

    public long PresentSubmitNs { get; init; }

    /// <summary>The slot's image was not ready by its target time.</summary>
    public bool Missed { get; init; }

    /// <summary>
    /// Presented, but nothing can say whether it was shown at its target (no first-pixel time, and no timed show took its
    /// refresh): neither a miss nor a show (playback on a display that reports first-pixel times only).
    /// </summary>
    public bool Unjudged { get; init; }
}

/// <summary>Receives timeline records. Implementations used on the hot path must not allocate.</summary>
public interface ITelemetrySink
{
    void Write(in FrameTimelineRecord record);
}
