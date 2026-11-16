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

    /// <summary>Swapchain images allowed in flight. The design caps this at 2.</summary>
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

public readonly record struct PresenterConfig
{
    public ClockMode ClockMode { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

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

    /// <summary>Enqueues a present for <see cref="PresentRequest.TargetTimeNs"/>; false if no image is available.</summary>
    bool TryPresent(in PresentRequest request);

    /// <summary>Non-blocking: returns feedback for a completed present, if any.</summary>
    bool TryGetFeedback(out PresentFeedback feedback);
}
