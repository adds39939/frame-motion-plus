using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Simulation;

/// <summary>A generator that does no work. It validates its inputs and counts calls, for simulations and tests.</summary>
public sealed class NullFrameGenerator : IFrameGenerator
{
    public NullFrameGenerator(FrameGenCapabilities? capabilities = null)
    {
        Capabilities = capabilities ?? new FrameGenCapabilities
        {
            MaxMultiplier = 8,
            SupportsArbitraryTimestep = true,
            SupportsExtrapolation = false,
            SupportsScaling = false,
            Formats = PixelFormatSet.All,
            Domain = MemoryDomain.CudaDevice,
        };
    }

    public FrameGenCapabilities Capabilities { get; }

    public FrameGenConfig Config { get; private set; }

    public long EnqueueCount { get; private set; }

    public long DiscontinuityCount { get; private set; }

    public float LastTimestep { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Configure(in FrameGenConfig config)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Config = config;
    }

    public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!(t > 0f && t < 1f) && !Capabilities.SupportsExtrapolation)
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "Timestep must be strictly between 0 and 1.");
        }

        if (!prev.HasSameShape(curr) || !prev.HasSameShape(output))
        {
            throw new ArgumentException("Previous, current and output frames must share size and format family.", nameof(output));
        }

        LastTimestep = t;
        EnqueueCount++;
    }

    public void NotifyDiscontinuity() => DiscontinuityCount++;

    public void Dispose() => IsDisposed = true;
}
