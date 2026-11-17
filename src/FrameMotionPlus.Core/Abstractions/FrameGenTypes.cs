using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

public enum FrameGenQuality
{
    /// <summary>The backend's own default (Medium for VFG).</summary>
    Default = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>What a frame generator can do. The scheduler and pipeline adapt to these, never the reverse.</summary>
public readonly record struct FrameGenCapabilities
{
    /// <summary>Highest output-to-source frame ratio the backend supports (VFG: 8).</summary>
    public int MaxMultiplier { get; init; }

    /// <summary>True when any timestep in (0, 1) is accepted (VFG explicit timestep mode).</summary>
    public bool SupportsArbitraryTimestep { get; init; }

    /// <summary>True when t outside (0, 1) is accepted (predicting past the newest frame).</summary>
    public bool SupportsExtrapolation { get; init; }

    /// <summary>True when output size may differ from input size. VFG: false.</summary>
    public bool SupportsScaling { get; init; }

    public PixelFormatSet Formats { get; init; }

    /// <summary>Memory domain the generator reads and writes.</summary>
    public MemoryDomain Domain { get; init; }

    /// <summary>
    /// True when <see cref="IFrameGenerator.Enqueue"/> may block its caller until the frame is generated, including
    /// until the GPU-side dependencies it enqueues first are met (the frames' <see cref="GpuFrame.Ready"/> signals, among
    /// them the output slot's release). False (the default) when the call only enqueues. See
    /// <see cref="IFrameGenerator.Enqueue"/> for what a pipeline must do when it is set. VFG with automatic shot-change
    /// detection: true (measured: the call returns when the generation has finished).
    /// </summary>
    public bool EnqueueMayBlock { get; init; }
}

/// <summary>Per-stream generator configuration. Changing it may rebuild engines off-thread.</summary>
public readonly record struct FrameGenConfig
{
    public int Width { get; init; }

    public int Height { get; init; }

    public PixelFormat Format { get; init; }

    public FrameGenQuality Quality { get; init; }

    /// <summary>Colour of the frames to be generated; generation happens in the source's own colour space.</summary>
    public ColorMetadata Color { get; init; }
}
