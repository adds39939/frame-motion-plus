using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Abstractions;

[Flags]
public enum PresenterFeatures
{
    None = 0,

    /// <summary>Absolute present times and first-pixel feedback (VK_EXT_present_timing).</summary>
    PresentTiming = 1 << 0,
    Vrr = 1 << 1,
    Hdr10 = 1 << 2,

    /// <summary>Generation happens inside present (NVIDIA Smooth Motion); pair with no <see cref="IFrameGenerator"/>.</summary>
    IntegratedFrameGeneration = 1 << 3,
    DirectDisplay = 1 << 4,
    Tearing = 1 << 5,
}

public readonly record struct PresenterCapabilities
{
    public PresenterFeatures Features { get; init; }

    /// <summary>
    /// Presents allowed in flight (submitted but not yet shown). The design caps this at 2. 0 means the presenter cannot
    /// cap it: it has no way to learn when a present is shown, so presents may queue deeper than its own limit and the
    /// added latency is not bounded.
    /// </summary>
    public int MaxFramesInFlight { get; init; }

    /// <summary>Memory domain the presenter reads ring slots from.</summary>
    public MemoryDomain Domain { get; init; }
}

/// <summary>The display's timing as currently known (refined from present feedback).</summary>
public readonly record struct DisplayTiming
{
    /// <summary>Fixed refresh rate, or the maximum refresh rate when VRR is active.</summary>
    public FrameRate RefreshRate { get; init; }

    /// <summary>A recent vblank (first-pixel) instant that anchors the fixed-refresh slot grid.</summary>
    public long VblankAnchorNs { get; init; }

    public FrameRate VrrMinimumRate { get; init; }

    public bool VrrActive { get; init; }
}

/// <summary>
/// How the presenter is set up. <see cref="Width"/>, <see cref="Height"/> and <see cref="Format"/> describe the frames
/// that will be presented (the ring slots' shape), not the output: the presenter scales them to whatever size its
/// window or display has, and <see cref="OutputColor"/> says what the output should be.
/// </summary>
public readonly record struct PresenterConfig
{
    public ClockMode ClockMode { get; init; }

    /// <summary>Width of the presented frames (ring slots) in pixels, not of the output.</summary>
    public int Width { get; init; }

    /// <summary>Height of the presented frames (ring slots) in pixels, not of the output.</summary>
    public int Height { get; init; }

    /// <summary>Pixel format of the presented frames (ring slots).</summary>
    public PixelFormat Format { get; init; }

    public ColorMetadata OutputColor { get; init; }

    public bool EnableIntegratedFrameGeneration { get; init; }
}

public readonly record struct PresentRequest
{
    public long SlotIndex { get; init; }

    public GpuFrame Frame { get; init; }

    /// <summary>GPU-side signal the present pass waits on before reading <see cref="Frame"/>.</summary>
    public GpuSignal Ready { get; init; }

    /// <summary>Absolute target display time V (first pixel), ns.</summary>
    public long TargetTimeNs { get; init; }
}

public readonly record struct PresentFeedback
{
    public long SlotIndex { get; init; }

    public long TargetTimeNs { get; init; }

    /// <summary>When the first pixel actually went out, ns; 0 when the presenter cannot tell.</summary>
    public long ActualTimeNs { get; init; }
}

/// <summary>Shows ring slots at target times. Non-blocking on the scheduling thread.</summary>
public interface IPresenter : IDisposable
{
    PresenterCapabilities Capabilities { get; }

    DisplayTiming Timing { get; }

    void Configure(in PresenterConfig config);

    /// <summary>
    /// Enqueues a present for <see cref="PresentRequest.TargetTimeNs"/>; false if it cannot be shown now (no free frame
    /// slot or image, or nothing on screen to show it on). Never blocks.
    /// <para>
    /// Slots are released GPU-side, once their content is written. A shown frame's slot is released as soon as the
    /// presenter has its own copy. When a request returns false, the newest frame handed to the presenter stays
    /// presentable, so retrying it can still show it; every older frame is released. That frame is released when a
    /// newer one is handed to the presenter (shown or not) or <see cref="Release"/> covers it. A producer is therefore
    /// held up by at most one frame that is not shown.
    /// </para>
    /// </summary>
    bool TryPresent(in PresentRequest request);

    /// <summary>
    /// Tells the presenter that the frame in <paramref name="request"/>, and every frame handed out before it, will not
    /// be presented: their slots are released GPU-side once written. For frames the pipeline decides not to show. A
    /// frame already released or presented is ignored. Never blocks.
    /// </summary>
    void Release(in PresentRequest request);

    /// <summary>Non-blocking: returns feedback for a completed present, if any.</summary>
    bool TryGetFeedback(out PresentFeedback feedback);
}
