using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

public enum CudaStreamPriority
{
    /// <summary>The context's least (default) priority.</summary>
    Normal = 0,

    /// <summary>The context's greatest priority, for latency-critical work such as frame generation.</summary>
    High = 1,
}

/// <summary>
/// A CUDA stream (<c>CUstream</c>). Everything here only enqueues and returns, except <see cref="Synchronize"/> and the
/// copies to or from pageable (ordinary managed) memory, which the driver completes before returning.
/// Addresses are device pointers or, under unified addressing, pinned/mapped host pointers.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "CUDA's own term for a CUstream; not a System.IO.Stream.")]
public sealed unsafe class CudaStream : CudaObject
{
    private nint _handle;

    private CudaStream(CudaContext context, nint handle, int priority)
        : base(context)
    {
        _handle = handle;
        Priority = priority;
    }

    /// <summary>
    /// The <c>CUstream</c>. Throws <see cref="ObjectDisposedException"/> once disposed: a zero handle would mean the
    /// legacy default stream to the driver, so work would silently run there instead of failing.
    /// </summary>
    public nint Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    /// <summary>The driver priority value; numerically lower runs first.</summary>
    public int Priority { get; }

    /// <summary>Signals waited on through this stream, per kind (diagnostics for tests: which paths ran).</summary>
    internal long EventWaits { get; private set; }

    internal long SemaphoreWaits { get; private set; }

    internal long EventSignals { get; private set; }

    internal long SemaphoreSignals { get; private set; }

    /// <summary>
    /// Creates a stream. <paramref name="nonBlocking"/> (the default) keeps it from synchronising implicitly with the
    /// legacy default stream, which other libraries in the process may use.
    /// </summary>
    public static CudaStream Create(CudaContext context, CudaStreamPriority priority = CudaStreamPriority.Normal, bool nonBlocking = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        var (least, greatest) = context.StreamPriorityRange;
        return CreateWithPriority(context, priority == CudaStreamPriority.High ? greatest : least, nonBlocking);
    }

    /// <summary><c>cuStreamCreateWithPriority</c>; the driver clamps <paramref name="priority"/> to the context's range.</summary>
    public static CudaStream CreateWithPriority(CudaContext context, int priority, bool nonBlocking = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            nint handle;
            Cu.Check(
                CuNative.cuStreamCreateWithPriority(&handle, nonBlocking ? CuConstants.StreamNonBlocking : CuConstants.StreamDefault, priority),
                nameof(CuNative.cuStreamCreateWithPriority));
            int actual;
            var result = CuNative.cuStreamGetPriority(handle, &actual);
            if (result != CudaResult.Success)
            {
                _ = CuNative.cuStreamDestroy_v2(handle);
                Cu.Throw(result, nameof(CuNative.cuStreamGetPriority));
            }

            return new CudaStream(context, handle, actual);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>Blocks the calling thread until all work on the stream is done. Not for the hot path.</summary>
    public void Synchronize()
    {
        Enter();
        Cu.Check(CuNative.cuStreamSynchronize(_handle), nameof(CuNative.cuStreamSynchronize));
    }

    /// <summary>Non-blocking: true when all enqueued work has finished.</summary>
    public bool IsIdle
    {
        get
        {
            Enter();
            var result = CuNative.cuStreamQuery(_handle);
            if (result == CudaResult.ErrorNotReady)
            {
                return false;
            }

            Cu.Check(result, nameof(CuNative.cuStreamQuery));
            return true;
        }
    }

    /// <summary>Later work on this stream waits, GPU-side, for <paramref name="cudaEvent"/>'s most recent record.</summary>
    public void Wait(CudaEvent cudaEvent)
    {
        ArgumentNullException.ThrowIfNull(cudaEvent);
        Enter();
        Cu.Check(CuNative.cuStreamWaitEvent(_handle, cudaEvent.Handle, 0), nameof(CuNative.cuStreamWaitEvent));
        EventWaits++;
    }

    /// <summary>
    /// Later work on this stream waits, GPU-side, for <paramref name="signal"/>: a CUDA event (its most recent record)
    /// or a CUDA timeline semaphore at its value. <see cref="GpuSignal.None"/> does nothing. Signals of other domains
    /// cannot be waited on in CUDA; the bridge hands out their CUDA-domain view instead. A signal whose event or
    /// semaphore has been destroyed throws <see cref="ObjectDisposedException"/> (see <see cref="GpuSignal"/>).
    /// </summary>
    public void Wait(in GpuSignal signal)
    {
        ThrowIfDisposed();
        if (signal.Kind == GpuSignalKind.None)
        {
            return;
        }

        ValidateCudaSignal(signal, nameof(signal));
        Context.EnsureCurrent();
        WaitValidated(signal);
    }

    /// <summary>
    /// Signals <paramref name="signal"/> once the work enqueued so far has finished: records a CUDA event, or sets a
    /// CUDA timeline semaphore to its value. <see cref="GpuSignal.None"/> does nothing. A signal whose event or
    /// semaphore has been destroyed throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Signal(in GpuSignal signal)
    {
        ThrowIfDisposed();
        if (signal.Kind == GpuSignalKind.None)
        {
            return;
        }

        ValidateCudaSignal(signal, nameof(signal));
        Context.EnsureCurrent();
        SignalValidated(signal);
    }

    /// <summary>Wait on a signal already checked with <see cref="ValidateCudaSignal"/>, with the context current.</summary>
    internal void WaitValidated(in GpuSignal signal)
    {
        if (signal.Kind == GpuSignalKind.CudaEvent)
        {
            Cu.Check(CuNative.cuStreamWaitEvent(_handle, (nint)signal.Handle, 0), nameof(CuNative.cuStreamWaitEvent));
            EventWaits++;
            return;
        }

        if (signal.Kind == GpuSignalKind.TimelineSemaphore)
        {
            var semaphore = (nint)signal.Handle;
            CuExternalSemaphoreWaitParams parameters = default;
            parameters.FenceValue = signal.Value;
            Cu.Check(
                CuNative.cuWaitExternalSemaphoresAsync(&semaphore, &parameters, 1, _handle),
                nameof(CuNative.cuWaitExternalSemaphoresAsync));
            SemaphoreWaits++;
        }
    }

    /// <summary>Signal already checked with <see cref="ValidateCudaSignal"/>, with the context current.</summary>
    internal void SignalValidated(in GpuSignal signal)
    {
        if (signal.Kind == GpuSignalKind.CudaEvent)
        {
            Cu.Check(CuNative.cuEventRecord((nint)signal.Handle, _handle), nameof(CuNative.cuEventRecord));
            EventSignals++;
            return;
        }

        if (signal.Kind == GpuSignalKind.TimelineSemaphore)
        {
            var semaphore = (nint)signal.Handle;
            CuExternalSemaphoreSignalParams parameters = default;
            parameters.FenceValue = signal.Value;
            Cu.Check(
                CuNative.cuSignalExternalSemaphoresAsync(&semaphore, &parameters, 1, _handle),
                nameof(CuNative.cuSignalExternalSemaphoresAsync));
            SemaphoreSignals++;
        }
    }

    /// <summary>Copies <paramref name="bytes"/> between device or pinned host addresses (<c>cuMemcpyAsync</c>).</summary>
    public void CopyAsync(ulong destination, ulong source, long bytes)
    {
        Enter();
        ValidateCopy(destination, source, bytes);
        Cu.Check(CuNative.cuMemcpyAsync(destination, source, (nuint)bytes, _handle), nameof(CuNative.cuMemcpyAsync));
    }

    /// <summary>Host to device. Asynchronous for pinned <paramref name="source"/>; staged before returning for pageable.</summary>
    public void CopyFromHostAsync(ulong destination, void* source, long bytes)
    {
        Enter();
        ValidateCopy(destination, (ulong)source, bytes);
        Cu.Check(CuNative.cuMemcpyHtoDAsync_v2(destination, source, (nuint)bytes, _handle), nameof(CuNative.cuMemcpyHtoDAsync_v2));
    }

    /// <summary>Device to host. Asynchronous for pinned <paramref name="destination"/>; complete on return for pageable.</summary>
    public void CopyToHostAsync(void* destination, ulong source, long bytes)
    {
        Enter();
        ValidateCopy((ulong)destination, source, bytes);
        Cu.Check(CuNative.cuMemcpyDtoHAsync_v2(destination, source, (nuint)bytes, _handle), nameof(CuNative.cuMemcpyDtoHAsync_v2));
    }

    /// <summary>Copies from managed memory, which is pageable: the data is staged before this returns.</summary>
    public void CopyFromHost<T>(ReadOnlySpan<T> source, ulong destination)
        where T : unmanaged
    {
        fixed (T* p = source)
        {
            CopyFromHostAsync(destination, p, (long)source.Length * sizeof(T));
        }
    }

    /// <summary>
    /// Copies into managed memory, which is pageable: the copy has finished (after all earlier work on the stream)
    /// when this returns.
    /// </summary>
    public void CopyToHost<T>(ulong source, Span<T> destination)
        where T : unmanaged
    {
        fixed (T* p = destination)
        {
            CopyToHostAsync(p, source, (long)destination.Length * sizeof(T));
        }

        Synchronize();
    }

    /// <summary>
    /// Pitched 2D copy between device or pinned/mapped host addresses (<c>cuMemcpy2DAsync</c>, unified addressing):
    /// <paramref name="height"/> rows of <paramref name="widthBytes"/> bytes.
    /// </summary>
    public void Copy2DAsync(ulong destination, long destinationPitch, ulong source, long sourcePitch, long widthBytes, long height)
    {
        Enter();
        Validate2D(destination, destinationPitch, source, sourcePitch, widthBytes, height);
        CuMemcpy2D copy = default;
        copy.SrcMemoryType = CuMemoryType.Unified;
        copy.SrcDevice = source;
        copy.SrcPitch = (nuint)sourcePitch;
        copy.DstMemoryType = CuMemoryType.Unified;
        copy.DstDevice = destination;
        copy.DstPitch = (nuint)destinationPitch;
        copy.WidthInBytes = (nuint)widthBytes;
        copy.Height = (nuint)height;
        Cu.Check(CuNative.cuMemcpy2DAsync_v2(&copy, _handle), nameof(CuNative.cuMemcpy2DAsync_v2));
    }

    /// <summary>Pitched 2D copy from any host memory (pageable or pinned) to a device or pinned address.</summary>
    public void Copy2DFromHostAsync(ulong destination, long destinationPitch, void* source, long sourcePitch, long widthBytes, long height)
    {
        Enter();
        Validate2D(destination, destinationPitch, (ulong)source, sourcePitch, widthBytes, height);
        CuMemcpy2D copy = default;
        copy.SrcMemoryType = CuMemoryType.Host;
        copy.SrcHost = source;
        copy.SrcPitch = (nuint)sourcePitch;
        copy.DstMemoryType = CuMemoryType.Unified;
        copy.DstDevice = destination;
        copy.DstPitch = (nuint)destinationPitch;
        copy.WidthInBytes = (nuint)widthBytes;
        copy.Height = (nuint)height;
        Cu.Check(CuNative.cuMemcpy2DAsync_v2(&copy, _handle), nameof(CuNative.cuMemcpy2DAsync_v2));
    }

    /// <summary>Pitched 2D copy from a device or pinned address to any host memory (pageable or pinned).</summary>
    public void Copy2DToHostAsync(void* destination, long destinationPitch, ulong source, long sourcePitch, long widthBytes, long height)
    {
        Enter();
        Validate2D((ulong)destination, destinationPitch, source, sourcePitch, widthBytes, height);
        CuMemcpy2D copy = default;
        copy.SrcMemoryType = CuMemoryType.Unified;
        copy.SrcDevice = source;
        copy.SrcPitch = (nuint)sourcePitch;
        copy.DstMemoryType = CuMemoryType.Host;
        copy.DstHost = destination;
        copy.DstPitch = (nuint)destinationPitch;
        copy.WidthInBytes = (nuint)widthBytes;
        copy.Height = (nuint)height;
        Cu.Check(CuNative.cuMemcpy2DAsync_v2(&copy, _handle), nameof(CuNative.cuMemcpy2DAsync_v2));
    }

    /// <summary>Copies the pixels of <paramref name="source"/> into <paramref name="destination"/> (same size and format).</summary>
    public void CopyFrameAsync(in GpuFrame source, in GpuFrame destination)
    {
        FrameChecks.ValidateCudaImage(source, nameof(source));
        FrameChecks.ValidateCudaImage(destination, nameof(destination));
        if (source.Width != destination.Width || source.Height != destination.Height || source.Format != destination.Format)
        {
            ThrowShapeMismatch();
        }

        Copy2DAsync(
            destination.Address + destination.Offset,
            destination.PitchBytes,
            source.Address + source.Offset,
            source.PitchBytes,
            (long)source.Width * source.Format.BytesPerPixel(),
            source.Height);
    }

    /// <summary>Reads <paramref name="source"/> back into tightly packed pixels; complete on return.</summary>
    public void CopyFrameToHost(in GpuFrame source, Span<uint> destination)
    {
        FrameChecks.ValidateCudaImage(source, nameof(source));
        var count = (long)source.Width * source.Height;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, count, nameof(destination));
        fixed (uint* p = destination)
        {
            Copy2DToHostAsync(p, source.Width * 4L, source.Address + source.Offset, source.PitchBytes, source.Width * 4L, source.Height);
        }

        Synchronize();
    }

    /// <summary>Writes tightly packed pixels into <paramref name="destination"/>; the data is staged on return.</summary>
    public void CopyFrameFromHost(ReadOnlySpan<uint> source, in GpuFrame destination)
    {
        FrameChecks.ValidateCudaImage(destination, nameof(destination));
        var count = (long)destination.Width * destination.Height;
        ArgumentOutOfRangeException.ThrowIfLessThan(source.Length, count, nameof(source));
        fixed (uint* p = source)
        {
            Copy2DFromHostAsync(
                destination.Address + destination.Offset, destination.PitchBytes, p, destination.Width * 4L, destination.Width * 4L, destination.Height);
        }
    }

    public void MemsetAsync(ulong destination, byte value, long bytes)
    {
        Enter();
        ValidateCopy(destination, destination, bytes);
        Cu.Check(CuNative.cuMemsetD8Async(destination, value, (nuint)bytes, _handle), nameof(CuNative.cuMemsetD8Async));
    }

    /// <summary>Sets every pixel of <paramref name="frame"/> to the packed 32-bit <paramref name="pixel"/>.</summary>
    public void FillFrameAsync(in GpuFrame frame, uint pixel)
    {
        FrameChecks.ValidateCudaImage(frame, nameof(frame));
        Enter();
        Cu.Check(
            CuNative.cuMemsetD2D32Async(frame.Address + frame.Offset, (nuint)frame.PitchBytes, pixel, (nuint)frame.Width, (nuint)frame.Height, _handle),
            nameof(CuNative.cuMemsetD2D32Async));
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuStreamDestroy_v2(_handle);
        _handle = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enter()
    {
        ThrowIfDisposed();
        Context.EnsureCurrent();
    }

    /// <summary>
    /// Throws unless <paramref name="signal"/> is none, or a CUDA event or CUDA timeline semaphore that is alive:
    /// <see cref="ArgumentException"/> for a malformed or foreign-domain signal, <see cref="ObjectDisposedException"/>
    /// for one whose event or semaphore was not created by this library or has been destroyed.
    /// </summary>
    internal static void ValidateCudaSignal(in GpuSignal signal, string paramName)
    {
        if (signal.Kind == GpuSignalKind.None)
        {
            return;
        }

        if (signal.Domain != MemoryDomain.CudaDevice || signal.Handle == 0 ||
            signal.Kind is not (GpuSignalKind.CudaEvent or GpuSignalKind.TimelineSemaphore))
        {
            ThrowNotCudaSignal(signal, paramName);
        }

        var live = signal.Kind == GpuSignalKind.CudaEvent
            ? LiveHandles.IsLiveEvent((nint)signal.Handle, signal.Value)
            : LiveHandles.IsLiveSemaphore((nint)signal.Handle);
        if (!live)
        {
            ThrowStaleSignal(signal);
        }
    }

    private static void ValidateCopy(ulong destination, ulong source, long bytes)
    {
        if (destination == 0 || source == 0 || bytes <= 0)
        {
            ThrowInvalidCopy(destination, source, bytes);
        }
    }

    private static void Validate2D(ulong destination, long destinationPitch, ulong source, long sourcePitch, long widthBytes, long height)
    {
        if (destination == 0 || source == 0 || widthBytes <= 0 || height <= 0 || destinationPitch < widthBytes || sourcePitch < widthBytes)
        {
            ThrowInvalid2D(destinationPitch, sourcePitch, widthBytes, height);
        }
    }

    [DoesNotReturn]
    private static void ThrowNotCudaSignal(in GpuSignal signal, string paramName) =>
        throw new ArgumentException(
            $"Not a CUDA-domain event or timeline semaphore: {signal.Kind} in {signal.Domain}, handle 0x{signal.Handle:x}.", paramName);

    [DoesNotReturn]
    private static void ThrowStaleSignal(in GpuSignal signal) =>
        throw new ObjectDisposedException(
            signal.Kind == GpuSignalKind.CudaEvent ? nameof(CudaEvent) : nameof(CudaExternalSemaphore),
            $"The signal names {(signal.Kind == GpuSignalKind.CudaEvent ? "a CUDA event" : "a CUDA external semaphore")} " +
            $"(handle 0x{signal.Handle:x}) that is no longer alive or was not created by this library. The object behind a " +
            "signal must outlive every frame and request that names it.");

    [DoesNotReturn]
    private static void ThrowInvalidCopy(ulong destination, ulong source, long bytes) =>
        throw new ArgumentException($"Invalid copy: destination 0x{destination:x}, source 0x{source:x}, {bytes} bytes.");

    [DoesNotReturn]
    private static void ThrowInvalid2D(long destinationPitch, long sourcePitch, long widthBytes, long height) =>
        throw new ArgumentException(
            $"Invalid 2D copy: {widthBytes} bytes x {height} rows with pitches {sourcePitch} (source) and {destinationPitch} (destination); " +
            "both addresses must be set and each pitch must be at least the row width.");

    [DoesNotReturn]
    private static void ThrowShapeMismatch() =>
        throw new ArgumentException("Source and destination frames must have the same size and pixel format.");
}
