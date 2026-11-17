using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// A CUDA event (<c>CUevent</c>). Synchronisation events are created without timing, which makes record and wait
/// cheaper; timing events (<see cref="CreateForTiming"/>) also give GPU-side durations.
/// </summary>
public sealed unsafe class CudaEvent : CudaObject
{
    private nint _handle;
    private ulong _generation;

    private CudaEvent(CudaContext context, nint handle, bool timing)
        : base(context)
    {
        _handle = handle;
        TimingEnabled = timing;
        _generation = LiveHandles.AddEvent(handle);
    }

    /// <summary>The <c>CUevent</c>. Throws <see cref="ObjectDisposedException"/> once disposed.</summary>
    public nint Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    public bool TimingEnabled { get; }

    /// <summary>
    /// This event as a <see cref="GpuSignal"/>: a wait covers its most recent record. <see cref="GpuSignal.Value"/>
    /// carries this event's generation tag, so a wait on or record of the signal after the event is disposed throws
    /// <see cref="ObjectDisposedException"/>, even if the driver has reused the handle for a new event.
    /// </summary>
    public GpuSignal Signal
    {
        get
        {
            ThrowIfDisposed();
            return new GpuSignal { Kind = GpuSignalKind.CudaEvent, Domain = MemoryDomain.CudaDevice, Handle = (ulong)_handle, Value = _generation };
        }
    }

    /// <summary>A synchronisation-only event (<c>CU_EVENT_DISABLE_TIMING</c>).</summary>
    public static CudaEvent Create(CudaContext context) => Create(context, timing: false);

    /// <summary>An event that records GPU timestamps, for <see cref="ElapsedNanoseconds"/>.</summary>
    public static CudaEvent CreateForTiming(CudaContext context) => Create(context, timing: true);

    private static CudaEvent Create(CudaContext context, bool timing)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            nint handle;
            Cu.Check(
                CuNative.cuEventCreate(&handle, timing ? CuConstants.EventDefault : CuConstants.EventDisableTiming),
                nameof(CuNative.cuEventCreate));
            return new CudaEvent(context, handle, timing);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>Captures the work enqueued on <paramref name="stream"/> so far.</summary>
    public void Record(CudaStream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        var streamHandle = stream.Handle;
        Context.EnsureCurrent();
        Cu.Check(CuNative.cuEventRecord(_handle, streamHandle), nameof(CuNative.cuEventRecord));
    }

    /// <summary>Non-blocking: true when the captured work has finished (or the event was never recorded).</summary>
    public bool IsComplete
    {
        get
        {
            ThrowIfDisposed();
            Context.EnsureCurrent();
            var result = CuNative.cuEventQuery(_handle);
            if (result == CudaResult.ErrorNotReady)
            {
                return false;
            }

            Cu.Check(result, nameof(CuNative.cuEventQuery));
            return true;
        }
    }

    /// <summary>Blocks the calling thread until the captured work has finished. Not for the hot path.</summary>
    public void Synchronize()
    {
        ThrowIfDisposed();
        Context.EnsureCurrent();
        Cu.Check(CuNative.cuEventSynchronize(_handle), nameof(CuNative.cuEventSynchronize));
    }

    /// <summary>GPU time from <paramref name="start"/> to <paramref name="end"/> (both timing events, both complete), ns.</summary>
    public static long ElapsedNanoseconds(CudaEvent start, CudaEvent end)
    {
        if (!TryGetElapsedNanoseconds(start, end, out var ns))
        {
            Cu.Throw(CudaResult.ErrorNotReady, nameof(CuNative.cuEventElapsedTime), "The end event has not completed.");
        }

        return ns;
    }

    /// <summary>Non-blocking: false while either event is still pending. Resolution is about 0.5 µs.</summary>
    public static bool TryGetElapsedNanoseconds(CudaEvent start, CudaEvent end, out long nanoseconds)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        start.ThrowIfDisposed();
        end.ThrowIfDisposed();
        start.Context.EnsureCurrent();
        float ms;
        var result = CuNative.cuEventElapsedTime(&ms, start._handle, end._handle);
        if (result == CudaResult.ErrorNotReady)
        {
            nanoseconds = 0;
            return false;
        }

        Cu.Check(result, nameof(CuNative.cuEventElapsedTime));
        nanoseconds = (long)Math.Round(ms * 1e6);
        return true;
    }

    private protected override void Unregister() => LiveHandles.RemoveEvent(_handle);

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuEventDestroy_v2(_handle);
        _handle = 0;
        _generation = 0;
    }
}
