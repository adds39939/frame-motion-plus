using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Cuda.Tests.Support;

/// <summary>
/// Hardware gate for the <c>Gpu</c> category: a test calls <see cref="Require"/> first and is reported skipped, never
/// failed, when the CUDA driver library or an NVIDIA device is missing (hosted CI has neither).
/// </summary>
internal static class Gpu
{
    private static readonly Lazy<(GpuEnvironment? Environment, string? Reason)> _shared = new(Create);

    /// <summary>The process-wide context, kernels and stream, or a skip when there is no GPU.</summary>
    public static GpuEnvironment Require()
    {
        var (environment, reason) = _shared.Value;
        if (environment is null)
        {
            Assert.Skip(reason ?? "CUDA unavailable");
        }

        return environment;
    }

    private static (GpuEnvironment?, string?) Create()
    {
        if (!CudaDriver.TryInitialize(out var reason))
        {
            return (null, reason);
        }

        if (CudaDriver.DeviceCount == 0)
        {
            return (null, "no CUDA device");
        }

        return (new GpuEnvironment(), null);
    }
}

/// <summary>Shared for the whole test run; never disposed (the process exit releases it).</summary>
internal sealed class GpuEnvironment
{
    public GpuEnvironment()
    {
        Context = CudaContext.RetainPrimary(0);
        Kernels = ImageKernels.Load(Context);
    }

    public CudaContext Context { get; }

    public ImageKernels Kernels { get; }

    /// <summary>A device image filled with zero.</summary>
    public CudaDeviceBuffer Image(int width, int height, PixelFormat format, CudaStream? stream = null)
    {
        var buffer = CudaDeviceBuffer.AllocateImage(Context, width, height, format);
        using var own = stream is null ? CudaStream.Create(Context) : null;
        var s = stream ?? own!;
        s.MemsetAsync(buffer.Address, 0, buffer.SizeBytes);
        s.Synchronize();
        return buffer;
    }

    /// <summary>Guard rows above and below a <see cref="PaddedImage"/>.</summary>
    public const int GuardRows = 2;

    /// <summary>Guard bytes before each row of a <see cref="PaddedImage"/> (the view starts this far into a row).</summary>
    public const int GuardLeadBytes = 16;

    /// <summary>The default fill of every byte of a <see cref="PaddedImage"/>'s allocation outside the image.</summary>
    public const byte Guard = 0xA5;

    /// <summary>
    /// Guard fills for kernel inputs. They differ from <see cref="Guard"/> and from each other, and neither blending
    /// them nor swapping their channels gives <see cref="Guard"/>, so an output row a kernel computes from input guard
    /// bytes still shows up in the output's guard.
    /// </summary>
    public const byte InputGuard = 0x5A;

    public const byte SecondInputGuard = 0x3C;

    /// <summary>
    /// A device image inside a larger allocation, to catch kernels that ignore the pitch or write outside the image:
    /// <see cref="GuardRows"/> rows above and below, <see cref="GuardLeadBytes"/> before each row (the view starts at an
    /// offset) and <paramref name="extraPitchBytes"/> after it, all filled with <see cref="Guard"/>; the image itself
    /// too until something writes it. <see cref="AssertGuardsIntact"/> checks the guards.
    /// </summary>
    public CudaDeviceBuffer PaddedImage(int width, int height, int extraPitchBytes, out GpuFrame frame, PixelFormat format, byte guard = Guard)
    {
        var pitch = GuardLeadBytes + (width * 4) + extraPitchBytes;
        var buffer = CudaDeviceBuffer.Allocate(Context, (long)pitch * (height + (2 * GuardRows)));
        frame = buffer.AsFrame(width, height, format, pitch, offset: ((long)GuardRows * pitch) + GuardLeadBytes);
        using var stream = CudaStream.Create(Context);
        stream.MemsetAsync(buffer.Address, guard, buffer.SizeBytes);
        stream.Synchronize();
        return buffer;
    }

    /// <summary>Every byte of <paramref name="buffer"/> outside <paramref name="frame"/>'s pixels still holds <paramref name="guard"/>.</summary>
    public void AssertGuardsIntact(CudaStream stream, CudaDeviceBuffer buffer, in GpuFrame frame, byte guard = Guard)
    {
        Assert.Equal(Context.Handle, stream.Context.Handle);
        Assert.Equal(buffer.Address, frame.Address);
        var raw = new byte[buffer.SizeBytes];
        stream.CopyToHost<byte>(buffer.Address, raw);
        var rowBytes = frame.Width * 4;
        var start = (long)frame.Offset;
        for (long i = 0; i < raw.Length; i++)
        {
            var inside = i >= start && (i - start) / frame.PitchBytes < frame.Height && (i - start) % frame.PitchBytes < rowBytes;
            if (!inside && raw[i] != guard)
            {
                var row = Math.Floor((double)(i - start) / frame.PitchBytes);
                Assert.Fail($"guard byte {i} (image row {row}, byte {(i - start) - ((long)row * frame.PitchBytes)} of the row) was written: 0x{raw[i]:x2}");
            }
        }
    }

    public uint[] Read(CudaStream stream, in GpuFrame frame)
    {
        Assert.Equal(Context.Handle, stream.Context.Handle);
        var pixels = new uint[frame.Width * frame.Height];
        stream.CopyFrameToHost(frame, pixels);
        return pixels;
    }
}
