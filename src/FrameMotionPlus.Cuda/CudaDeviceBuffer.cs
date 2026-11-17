using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// Device memory from <c>cuMemAlloc</c>, or a pitched image from <c>cuMemAllocPitch</c>. Disposal frees with
/// <c>cuMemFree</c>, which blocks until all queued GPU work in the context has finished: never on the pacing thread.
/// </summary>
public sealed unsafe class CudaDeviceBuffer : CudaObject
{
    private ulong _address;

    private CudaDeviceBuffer(CudaContext context, ulong address, long sizeBytes, int width, int height, int pitch, PixelFormat format)
        : base(context)
    {
        _address = address;
        SizeBytes = sizeBytes;
        Width = width;
        Height = height;
        PitchBytes = pitch;
        Format = format;
    }

    /// <summary>The <c>CUdeviceptr</c>.</summary>
    public ulong Address => _address;

    public long SizeBytes { get; }

    /// <summary>Image width in pixels; 0 for a linear allocation.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Row pitch chosen by the driver for an image; 0 for a linear allocation.</summary>
    public int PitchBytes { get; }

    public PixelFormat Format { get; }

    public static CudaDeviceBuffer Allocate(CudaContext context, long bytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            ulong address;
            Cu.Check(CuNative.cuMemAlloc_v2(&address, (nuint)bytes), nameof(CuNative.cuMemAlloc_v2));
            return new CudaDeviceBuffer(context, address, bytes, 0, 0, 0, PixelFormat.Unknown);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>A pitch-linear image with the row pitch the driver prefers for coalesced access.</summary>
    public static CudaDeviceBuffer AllocateImage(CudaContext context, int width, int height, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format.BytesPerPixel() != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Only 32-bit pixel formats are supported.");
        }

        var rowBytes = (nuint)format.MinimumPitch(width);
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            ulong address;
            nuint pitch;
            Cu.Check(CuNative.cuMemAllocPitch_v2(&address, &pitch, rowBytes, (nuint)height, 4), nameof(CuNative.cuMemAllocPitch_v2));
            if (pitch > int.MaxValue)
            {
                _ = CuNative.cuMemFree_v2(address);
                throw new ArgumentOutOfRangeException(nameof(width), width, "Row pitch exceeds 2 GB.");
            }

            return new CudaDeviceBuffer(context, address, (long)pitch * height, width, height, (int)pitch, format);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>This image as a <see cref="GpuFrame"/> in CUDA device memory.</summary>
    public GpuFrame AsFrame(ColorMetadata color = default, long contentTimeNs = 0, GpuSignal ready = default)
    {
        ThrowIfDisposed();
        if (Width == 0)
        {
            throw new InvalidOperationException("A linear buffer has no image shape; use the overload that takes one.");
        }

        return AsFrame(Width, Height, Format, PitchBytes, 0, color, contentTimeNs, ready);
    }

    /// <summary>A <see cref="GpuFrame"/> view of part of this buffer, checked to lie inside it.</summary>
    public GpuFrame AsFrame(int width, int height, PixelFormat format, int pitchBytes, long offset = 0,
        ColorMetadata color = default, long contentTimeNs = 0, GpuSignal ready = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var frame = new GpuFrame
        {
            Domain = MemoryDomain.CudaDevice,
            Address = _address,
            Offset = (ulong)offset,
            SizeBytes = SizeBytes,
            Width = width,
            Height = height,
            PitchBytes = pitchBytes,
            Format = format,
            Color = color,
            ContentTimeNs = contentTimeNs,
            Ready = ready,
        };
        FrameChecks.ValidateCudaImage(frame, nameof(width));
        return frame;
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuMemFree_v2(_address);
        _address = 0;
    }
}
