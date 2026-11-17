using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>CU_MEMHOSTALLOC_* flags.</summary>
[Flags]
public enum CudaHostAllocOptions
{
    None = 0,

    /// <summary>Pinned for every context, not only the allocating one.</summary>
    Portable = 0x01,

    /// <summary>Mapped into the device address space (<see cref="CudaPinnedBuffer.DeviceAddress"/>).</summary>
    DeviceMap = 0x02,

    /// <summary>Write-combined: fast for the CPU to write and the GPU to read, very slow for the CPU to read.</summary>
    WriteCombined = 0x04,
}

/// <summary>CU_MEMHOSTREGISTER_* flags.</summary>
[Flags]
public enum CudaHostRegisterOptions
{
    None = 0,
    Portable = 0x01,
    DeviceMap = 0x02,
    IoMemory = 0x04,
    ReadOnly = 0x08,
}

/// <summary>
/// Page-locked host memory from <c>cuMemHostAlloc</c>: DMA-able and optionally mapped. Disposal frees with
/// <c>cuMemFreeHost</c>, which blocks until all queued GPU work in the context has finished: never on the pacing thread. The driver sub-allocates
/// pinned memory from pools, so a plain allocation is only 512-byte aligned (measured: most small and some 1 MB
/// allocations are not page aligned). Pass an <c>alignment</c> such as <see cref="Environment.SystemPageSize"/> where
/// page alignment matters (V4L2 USERPTR buffers).
/// </summary>
public sealed unsafe class CudaPinnedBuffer : CudaObject
{
    private void* _allocation;
    private void* _host;

    private CudaPinnedBuffer(CudaContext context, void* allocation, void* host, ulong device, long sizeBytes)
        : base(context)
    {
        _allocation = allocation;
        _host = host;
        DeviceAddress = device;
        SizeBytes = sizeBytes;
    }

    /// <summary>Start of the usable bytes (aligned as requested).</summary>
    public void* HostPointer => _host;

    /// <summary>The device-side address of <see cref="HostPointer"/> when allocated with <see cref="CudaHostAllocOptions.DeviceMap"/>, else 0.</summary>
    public ulong DeviceAddress { get; }

    public long SizeBytes { get; }

    /// <summary>
    /// Allocates <paramref name="bytes"/> of pinned memory. A non-zero <paramref name="alignment"/> (a power of two)
    /// aligns <see cref="HostPointer"/> to it by allocating up to that much more.
    /// </summary>
    public static CudaPinnedBuffer Allocate(CudaContext context, long bytes,
        CudaHostAllocOptions flags = CudaHostAllocOptions.Portable | CudaHostAllocOptions.DeviceMap, int alignment = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(alignment);
        if ((alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment must be a power of two.");
        }

        context.AddReference();
        try
        {
            context.EnsureCurrent();
            void* allocation;
            Cu.Check(CuNative.cuMemHostAlloc(&allocation, (nuint)(bytes + alignment), (uint)flags), nameof(CuNative.cuMemHostAlloc));
            var offset = alignment == 0 ? 0 : (alignment - (long)((nuint)allocation % (nuint)alignment)) % alignment;
            var host = (byte*)allocation + offset;
            ulong device = 0;
            if ((flags & CudaHostAllocOptions.DeviceMap) != 0)
            {
                var result = CuNative.cuMemHostGetDevicePointer_v2(&device, allocation, 0);
                if (result != CudaResult.Success)
                {
                    _ = CuNative.cuMemFreeHost(allocation);
                    Cu.Throw(result, nameof(CuNative.cuMemHostGetDevicePointer_v2));
                }

                device += (ulong)offset;
            }

            return new CudaPinnedBuffer(context, allocation, host, device, bytes);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>The whole buffer as <typeparamref name="T"/> elements. Valid until disposal.</summary>
    public Span<T> AsSpan<T>()
        where T : unmanaged
    {
        ThrowIfDisposed();
        return new Span<T>(_host, checked((int)(SizeBytes / sizeof(T))));
    }

    /// <summary>
    /// A frame view of the mapped buffer (<see cref="MemoryDomain.CudaMappedHost"/>), for kernels that read or write
    /// host memory directly. Needs <see cref="CudaHostAllocOptions.DeviceMap"/>.
    /// </summary>
    public GpuFrame AsFrame(int width, int height, PixelFormat format, int pitchBytes, long offset = 0, ColorMetadata color = default)
    {
        ThrowIfDisposed();
        if (DeviceAddress == 0)
        {
            throw new InvalidOperationException("The buffer is not mapped into the device address space (CudaHostAllocOptions.DeviceMap).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var frame = new GpuFrame
        {
            Domain = MemoryDomain.CudaMappedHost,
            Address = DeviceAddress,
            Offset = (ulong)offset,
            SizeBytes = SizeBytes,
            Width = width,
            Height = height,
            PitchBytes = pitchBytes,
            Format = format,
            Color = color,
        };
        FrameChecks.ValidateCudaImage(frame, nameof(width));
        return frame;
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuMemFreeHost(_allocation);
        _allocation = null;
        _host = null;
    }
}

/// <summary>
/// Existing host memory page-locked with <c>cuMemHostRegister</c> (e.g. V4L2 MMAP buffers). The caller keeps the
/// memory alive until this is disposed.
/// </summary>
public sealed unsafe class CudaHostRegistration : CudaObject
{
    private void* _host;

    private CudaHostRegistration(CudaContext context, void* host, ulong device, long sizeBytes)
        : base(context)
    {
        _host = host;
        DeviceAddress = device;
        SizeBytes = sizeBytes;
    }

    public void* HostPointer => _host;

    /// <summary>The device-side address when registered with <see cref="CudaHostRegisterOptions.DeviceMap"/>, else 0.</summary>
    public ulong DeviceAddress { get; }

    public long SizeBytes { get; }

    public static CudaHostRegistration Register(CudaContext context, void* hostMemory, long bytes,
        CudaHostRegisterOptions flags = CudaHostRegisterOptions.Portable | CudaHostRegisterOptions.DeviceMap)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (hostMemory == null)
        {
            throw new ArgumentNullException(nameof(hostMemory));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            Cu.Check(CuNative.cuMemHostRegister_v2(hostMemory, (nuint)bytes, (uint)flags), nameof(CuNative.cuMemHostRegister_v2));
            ulong device = 0;
            if ((flags & CudaHostRegisterOptions.DeviceMap) != 0)
            {
                var result = CuNative.cuMemHostGetDevicePointer_v2(&device, hostMemory, 0);
                if (result != CudaResult.Success)
                {
                    _ = CuNative.cuMemHostUnregister(hostMemory);
                    Cu.Throw(result, nameof(CuNative.cuMemHostGetDevicePointer_v2));
                }
            }

            return new CudaHostRegistration(context, hostMemory, device, bytes);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuMemHostUnregister(_host);
        _host = null;
    }
}
