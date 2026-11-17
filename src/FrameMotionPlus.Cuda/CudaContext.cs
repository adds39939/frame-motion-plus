using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>Scheduling of host threads that block in CUDA synchronisation calls (CU_CTX_SCHED_*).</summary>
public enum CudaSchedulingMode
{
    Auto = 0,
    Spin = 1,
    Yield = 2,
    BlockingSync = 4,
}

/// <summary>
/// A retained reference to a device's primary context: the one the CUDA runtime (and so the VFX SDK) and FFmpeg's
/// CUDA hwaccel use, so all of them share allocations, streams and events without extra plumbing.
/// <para>
/// Every object created from a context holds a reference to it. The primary context is released when the context
/// and everything created from it are disposed, in any order, so disposal order never matters.
/// </para>
/// <para>
/// Each wrapper call makes this context current on the calling thread if it is not already (one cheap driver call),
/// so any thread may use these objects. <see cref="Handle"/> is what other CUDA users of the same process take.
/// </para>
/// </summary>
public sealed unsafe class CudaContext : IDisposable
{
    private nint _handle;
    private int _references = 1;
    private int _disposed;

    private CudaContext(CudaDevice device, nint handle)
    {
        Device = device;
        _handle = handle;
    }

    public CudaDevice Device { get; }

    /// <summary>The <c>CUcontext</c>. Zero once the primary context has been released.</summary>
    public nint Handle => _handle;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Retains the primary context of device <paramref name="ordinal"/>.</summary>
    public static CudaContext RetainPrimary(int ordinal = 0) => RetainPrimary(CudaDriver.GetDevice(ordinal));

    /// <summary>
    /// Retains the primary context of <paramref name="device"/>. <paramref name="scheduling"/>, when given, is applied
    /// with <c>cuDevicePrimaryCtxSetFlags</c> first; it affects only threads that block in synchronisation calls.
    /// </summary>
    public static CudaContext RetainPrimary(CudaDevice device, CudaSchedulingMode? scheduling = null)
    {
        CudaDriver.Initialize();
        if (scheduling is { } mode)
        {
            Cu.Check(CuNative.cuDevicePrimaryCtxSetFlags_v2(device.Handle, (uint)mode), nameof(CuNative.cuDevicePrimaryCtxSetFlags_v2));
        }

        nint handle;
        Cu.Check(CuNative.cuDevicePrimaryCtxRetain(&handle, device.Handle), nameof(CuNative.cuDevicePrimaryCtxRetain));
        var context = new CudaContext(device, handle);
        try
        {
            context.MakeCurrent();
        }
        catch
        {
            context.Dispose();
            throw;
        }

        return context;
    }

    /// <summary>Binds this context to the calling thread (<c>cuCtxSetCurrent</c>).</summary>
    public void MakeCurrent()
    {
        ThrowIfReleased();
        Cu.Check(CuNative.cuCtxSetCurrent(_handle), nameof(CuNative.cuCtxSetCurrent));
    }

    /// <summary>Binds this context to the calling thread unless it already is.</summary>
    public void EnsureCurrent()
    {
        var handle = _handle;
        if (handle == 0)
        {
            ThrowIfReleased();
        }

        nint current;
        Cu.Check(CuNative.cuCtxGetCurrent(&current), nameof(CuNative.cuCtxGetCurrent));
        if (current != handle)
        {
            Cu.Check(CuNative.cuCtxSetCurrent(handle), nameof(CuNative.cuCtxSetCurrent));
        }
    }

    /// <summary>As <see cref="EnsureCurrent"/>, without throwing (for release paths).</summary>
    internal bool TryEnsureCurrent()
    {
        var handle = _handle;
        nint current;
        return handle != 0 &&
               CuNative.cuCtxGetCurrent(&current) == CudaResult.Success &&
               (current == handle || CuNative.cuCtxSetCurrent(handle) == CudaResult.Success);
    }

    /// <summary>True when this context is the calling thread's current context.</summary>
    public bool IsCurrent
    {
        get
        {
            nint current;
            return CuNative.cuCtxGetCurrent(&current) == CudaResult.Success && current != 0 && current == _handle;
        }
    }

    /// <summary>Blocks until all work in the context is done. Not for the hot path.</summary>
    public void Synchronize()
    {
        EnsureCurrent();
        Cu.Check(CuNative.cuCtxSynchronize(), nameof(CuNative.cuCtxSynchronize));
    }

    /// <summary>
    /// Stream priorities this context accepts. Numerically lower is higher priority: <c>Greatest</c> is the highest
    /// priority (e.g. -5), <c>Least</c> the normal one (0).
    /// </summary>
    public (int Least, int Greatest) StreamPriorityRange
    {
        get
        {
            EnsureCurrent();
            int least, greatest;
            Cu.Check(CuNative.cuCtxGetStreamPriorityRange(&least, &greatest), nameof(CuNative.cuCtxGetStreamPriorityRange));
            return (least, greatest);
        }
    }

    public (long FreeBytes, long TotalBytes) MemoryInfo
    {
        get
        {
            EnsureCurrent();
            nuint free, total;
            Cu.Check(CuNative.cuMemGetInfo_v2(&free, &total), nameof(CuNative.cuMemGetInfo_v2));
            return ((long)free, (long)total);
        }
    }

    /// <summary>Drops this object's reference. The primary context is released once nothing created from it is alive.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>
    /// Taken for every object created from this context, before the factory allocates it, so the context outlives
    /// it. Fails once the context is disposed or released; a released context is never revived (which would release
    /// the primary context twice).
    /// </summary>
    internal void AddReference()
    {
        while (true)
        {
            var references = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(references == 0 || IsDisposed, this);

            if (Interlocked.CompareExchange(ref _references, references + 1, references) == references)
            {
                return;
            }
        }
    }

    /// <summary>References currently held: this object's own until disposed, plus one per live object created from it.</summary>
    internal int ReferenceCount => Volatile.Read(ref _references);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            _ = CuNative.cuDevicePrimaryCtxRelease_v2(Device.Handle);

            // If that was the last retain anywhere in the process, do not leave a destroyed context bound here.
            uint flags;
            int active;
            nint current;
            if (CuNative.cuDevicePrimaryCtxGetState(Device.Handle, &flags, &active) == CudaResult.Success && active == 0 &&
                (CuNative.cuCtxGetCurrent(&current) != CudaResult.Success || current == handle))
            {
                _ = CuNative.cuCtxSetCurrent(0);
            }
        }
    }

    private void ThrowIfReleased()
    {
        if (_handle == 0)
        {
            throw new ObjectDisposedException(nameof(CudaContext), "The primary context has been released.");
        }
    }
}
