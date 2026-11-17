using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda.Generators;

/// <summary>
/// Shared plumbing of the CUDA-domain generators. <see cref="Enqueue"/> runs on the calling thread, enqueues on
/// <see cref="Stream"/> and returns without waiting on the GPU:
/// <list type="number">
/// <item>GPU-side waits on <c>prev.Ready</c>, <c>curr.Ready</c> and <c>output.Ready</c> (CUDA events or CUDA
/// timeline semaphores; see <see cref="GpuFrame.Ready"/>),</item>
/// <item>a timed span with the generator's work,</item>
/// <item>the <c>done</c> signal.</item>
/// </list>
/// Zero managed allocations per call. The GPU time of each call can be read back with <see cref="TryReadGpuTime"/>.
/// </summary>
public abstract class CudaFrameGenerator : IFrameGenerator
{
    private readonly bool _ownsStream;
    private readonly CudaSpanTimerRing _timer;
    private readonly DiscontinuityLatch _discontinuity = new();
    private FrameGenConfig _config;
    private bool _disposed;

    private protected CudaFrameGenerator(CudaContext context, CudaStream? stream, CudaStreamPriority priority)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        if (stream is not null)
        {
            stream.ThrowIfDisposed();
            if (stream.Context.Handle != context.Handle)
            {
                throw new ArgumentException("The stream belongs to another CUDA context.", nameof(stream));
            }
        }

        Context = context;
        _ownsStream = stream is null;
        Stream = stream ?? CudaStream.Create(context, priority);
        try
        {
            _timer = CudaSpanTimerRing.Create(context, 32);
        }
        catch
        {
            if (_ownsStream)
            {
                Stream.Dispose();
            }

            throw;
        }
    }

    public CudaContext Context { get; }

    /// <summary>The stream all generation work runs on (created and owned by the generator unless one was passed in).</summary>
    public CudaStream Stream { get; }

    public abstract FrameGenCapabilities Capabilities { get; }

    public FrameGenConfig Config => _config;

    /// <summary>
    /// <see cref="Enqueue"/> calls that were not timed because <see cref="TryReadGpuTime"/> was the timer ring's capacity
    /// behind (unread times are kept; new ones are dropped).
    /// </summary>
    public long DroppedGpuTimes => _timer.Dropped;

    public void Configure(in FrameGenConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.Width, nameof(config));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.Height, nameof(config));
        if (!Capabilities.Formats.Contains(config.Format))
        {
            throw new ArgumentException($"{GetType().Name} does not support {config.Format}.", nameof(config));
        }

        _config = config;
    }

    public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
    {
        if (_disposed)
        {
            ThrowDisposed();
        }

        if (!(t >= 0f && t <= 1f))
        {
            ThrowTimestep(t);
        }

        Validate(prev, nameof(prev));
        Validate(curr, nameof(curr));
        Validate(output, nameof(output));
        DiscontinuityLatch.ValidatePair(prev, curr);
        CudaStream.ValidateCudaSignal(prev.Ready, nameof(prev));
        CudaStream.ValidateCudaSignal(curr.Ready, nameof(curr));
        CudaStream.ValidateCudaSignal(output.Ready, nameof(output));
        CudaStream.ValidateCudaSignal(done, nameof(done));
        var stream = Stream;
        stream.ThrowIfDisposed();
        Context.EnsureCurrent();

        // Everything is validated above, so nothing below is enqueued for a call that then fails on its arguments.
        stream.WaitValidated(prev.Ready);
        if (curr.Ready != prev.Ready)
        {
            stream.WaitValidated(curr.Ready);
        }

        stream.WaitValidated(output.Ready);

        // A pair that spans a notified cut is discontinuous for every call with it (Core's rule); t = 0 is prev itself
        // either way, so only t > 0 changes.
        var discontinuity = _discontinuity.Observe(prev, curr) && t > 0f;
        _timer.Begin(stream);
        try
        {
            EnqueueWork(prev, curr, t, output, discontinuity);
        }
        catch
        {
            _timer.Abort();
            throw;
        }

        _timer.End(stream);
        stream.SignalValidated(done);
    }

    /// <summary>
    /// The frame at <paramref name="contentTimeNs"/> starts a new shot (see <see cref="IFrameGenerator.NotifyDiscontinuity"/>):
    /// every <see cref="Enqueue"/> whose pair spans it outputs the current frame unchanged, except at t = 0 (the previous
    /// frame); the first pair that starts at or after it ends the notification. Safe from any thread.
    /// </summary>
    public void NotifyDiscontinuity(long contentTimeNs) => _discontinuity.Notify(contentTimeNs);

    /// <summary>Non-blocking: GPU time of the oldest unread finished <see cref="Enqueue"/>, ns.</summary>
    public bool TryReadGpuTime(out long nanoseconds) => _timer.TryRead(out nanoseconds);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        if (_ownsStream)
        {
            Stream.Dispose();
        }
    }

    /// <summary>Enqueues the generation itself on <see cref="Stream"/>; frames are already validated.</summary>
    private protected abstract void EnqueueWork(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, bool discontinuity);

    private void Validate(in GpuFrame frame, string paramName)
    {
        var config = _config;
        if (config.Width == 0)
        {
            ThrowNotConfigured();
        }

        FrameChecks.ValidateCudaImage(frame, paramName);
        if (frame.Width != config.Width || frame.Height != config.Height || frame.Format != config.Format)
        {
            ThrowShape(frame, paramName);
        }
    }

    [DoesNotReturn]
    private void ThrowDisposed() => throw new ObjectDisposedException(GetType().Name);

    [DoesNotReturn]
    private static void ThrowTimestep(float t) =>
        throw new ArgumentOutOfRangeException(nameof(t), t, "The timestep must lie in [0, 1]; these generators do not extrapolate.");

    [DoesNotReturn]
    private void ThrowNotConfigured() => throw new InvalidOperationException($"{GetType().Name}.Configure has not been called.");

    [DoesNotReturn]
    private void ThrowShape(in GpuFrame frame, string paramName) =>
        throw new ArgumentException(
            $"{frame.Width}x{frame.Height} {frame.Format} does not match the configured {_config.Width}x{_config.Height} {_config.Format}.",
            paramName);
}

/// <summary>
/// Shows the nearest real frame: <c>prev</c> for t &lt; 0.5, <c>curr</c> otherwise (across a discontinuity: <c>curr</c>
/// for every t &gt; 0), copied with <c>cuMemcpy2DAsync</c>. The baseline for A/B tests and the fallback when VFG cannot
/// keep up.
/// </summary>
public sealed class RepeatGenerator : CudaFrameGenerator
{
    /// <summary>Generates on a stream of its own at <paramref name="priority"/>.</summary>
    public RepeatGenerator(CudaContext context, CudaStreamPriority priority = CudaStreamPriority.High)
        : base(context, null, priority)
    {
    }

    /// <summary>Generates on <paramref name="stream"/>, which the caller keeps and disposes.</summary>
    public RepeatGenerator(CudaContext context, CudaStream stream)
        : base(context, stream ?? throw new ArgumentNullException(nameof(stream)), CudaStreamPriority.Normal)
    {
    }

    public override FrameGenCapabilities Capabilities { get; } = new()
    {
        MaxMultiplier = 8,
        SupportsArbitraryTimestep = true,
        SupportsExtrapolation = false,
        SupportsScaling = false,
        Formats = PixelFormatSet.All,
        Domain = MemoryDomain.CudaDevice,
        EnqueueMayBlock = false,
    };

    private protected override void EnqueueWork(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, bool discontinuity)
    {
        if (discontinuity || t >= 0.5f)
        {
            Stream.CopyFrameAsync(curr, output);
        }
        else
        {
            Stream.CopyFrameAsync(prev, output);
        }
    }
}

/// <summary>
/// Linear blend <c>(1 − t)·prev + t·curr</c> per channel (<see cref="Kernels.ImageKernels.EnqueueBlend"/>). The debug
/// baseline for pacing work before VFG.
/// </summary>
public sealed class BlendGenerator : CudaFrameGenerator
{
    private readonly Kernels.ImageKernels _kernels;

    /// <summary>Generates on a stream of its own at <paramref name="priority"/>.</summary>
    public BlendGenerator(CudaContext context, Kernels.ImageKernels kernels, CudaStreamPriority priority = CudaStreamPriority.High)
        : base(SameContext(context, kernels), null, priority)
    {
        _kernels = kernels;
    }

    /// <summary>Generates on <paramref name="stream"/>, which the caller keeps and disposes.</summary>
    public BlendGenerator(CudaContext context, Kernels.ImageKernels kernels, CudaStream stream)
        : base(SameContext(context, kernels), stream ?? throw new ArgumentNullException(nameof(stream)), CudaStreamPriority.Normal)
    {
        _kernels = kernels;
    }

    public override FrameGenCapabilities Capabilities { get; } = new()
    {
        MaxMultiplier = 8,
        SupportsArbitraryTimestep = true,
        SupportsExtrapolation = false,
        SupportsScaling = false,
        Formats = PixelFormatSet.All,
        Domain = MemoryDomain.CudaDevice,
        EnqueueMayBlock = false,
    };

    private protected override void EnqueueWork(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, bool discontinuity)
    {
        if (discontinuity)
        {
            Stream.CopyFrameAsync(curr, output);
        }
        else
        {
            _kernels.EnqueueBlend(Stream, prev, curr, t, output);
        }
    }

    /// <summary>Checks the kernels before the base constructor allocates a stream and timer events.</summary>
    private static CudaContext SameContext(CudaContext context, Kernels.ImageKernels kernels)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(kernels);
        return kernels.Context.Handle == context.Handle
            ? context
            : throw new ArgumentException("The kernels are loaded in another CUDA context.", nameof(kernels));
    }
}
