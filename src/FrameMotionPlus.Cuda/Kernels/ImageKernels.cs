using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda.Kernels;

/// <summary>
/// The kernels in native/kernels, loaded from the PTX embedded in this assembly: test pattern, blend, format
/// conversion, YCbCr 4:2:0 and packed 4:2:2 to RGB, the capture cadence metric and a GPU delay. Each Enqueue method validates its frames on the host, launches on the given stream
/// and returns; none waits on <see cref="GpuFrame.Ready"/> (callers that need it do <see cref="CudaStream.Wait(in GpuSignal)"/>).
/// Allocation-free per call.
/// </summary>
public sealed unsafe class ImageKernels : IDisposable
{
    /// <summary>Embedded module names (native/kernels/*.cu).</summary>
    public static readonly IReadOnlyList<string> ModuleNames = ["blend", "cadence", "convert", "pattern", "util", "yuv"];

    private static readonly CudaDim3 _block = new(32, 8);

    private readonly CudaModule[] _modules;
    private readonly CudaKernel _patternRgba8;
    private readonly CudaKernel _patternRgb10A2;
    private readonly CudaKernel _blendRgba8;
    private readonly CudaKernel _blendRgb10A2;
    private readonly CudaKernel _rgba8ToRgb10A2;
    private readonly CudaKernel _rgb10A2ToRgba8;
    private readonly CudaKernel _swapRedBlue8;
    private readonly CudaKernel _delay;
    private readonly CudaKernel _yuvToRgb;
    private readonly CudaKernel _yuyvToRgb;
    private readonly CudaKernel _cadence;

    private ImageKernels(CudaModule[] modules)
    {
        _modules = modules;
        Context = modules[0].Context;
        CudaModule Module(string name) => modules[ModuleIndex(name)];
        var blend = Module("blend");
        var cadence = Module("cadence");
        var convert = Module("convert");
        var pattern = Module("pattern");
        var util = Module("util");
        var yuv = Module("yuv");
        _patternRgba8 = pattern.GetKernel("fmp_pattern_rgba8");
        _patternRgb10A2 = pattern.GetKernel("fmp_pattern_rgb10a2");
        _blendRgba8 = blend.GetKernel("fmp_blend_rgba8");
        _blendRgb10A2 = blend.GetKernel("fmp_blend_rgb10a2");
        _rgba8ToRgb10A2 = convert.GetKernel("fmp_rgba8_to_rgb10a2");
        _rgb10A2ToRgba8 = convert.GetKernel("fmp_rgb10a2_to_rgba8");
        _swapRedBlue8 = convert.GetKernel("fmp_swap_red_blue8");
        _delay = util.GetKernel("fmp_delay_ns");
        _yuvToRgb = yuv.GetKernel("fmp_yuv420_to_rgb");
        _yuyvToRgb = yuv.GetKernel("fmp_yuyv_to_rgb");
        _cadence = cadence.GetKernel("fmp_cadence_metric");
    }

    private static int ModuleIndex(string name)
    {
        for (var i = 0; i < ModuleNames.Count; i++)
        {
            if (ModuleNames[i] == name)
            {
                return i;
            }
        }

        throw new ArgumentException($"No kernel module '{name}'.", nameof(name));
    }

    /// <summary>The context the modules are loaded in; launches must use streams of the same context.</summary>
    public CudaContext Context { get; }

    /// <summary>Loads every embedded module into <paramref name="context"/> (the driver JIT-compiles and caches them).</summary>
    public static ImageKernels Load(CudaContext context) => Load(context, GetEmbeddedPtx);

    /// <summary>Loads the modules from another source of PTX, e.g. a different toolchain's build (tests).</summary>
    internal static ImageKernels Load(CudaContext context, Func<string, byte[]> ptxForModule)
    {
        ArgumentNullException.ThrowIfNull(context);
        var modules = new CudaModule[ModuleNames.Count];
        try
        {
            for (var i = 0; i < modules.Length; i++)
            {
                modules[i] = CudaModule.Load(context, ptxForModule(ModuleNames[i]), ModuleNames[i]);
            }

            return new ImageKernels(modules);
        }
        catch
        {
            foreach (var module in modules)
            {
                module?.Dispose();
            }

            throw;
        }
    }

    /// <summary>The embedded PTX text of <paramref name="module"/>, as checked in under native/kernels/ptx.</summary>
    public static byte[] GetEmbeddedPtx(string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        using var stream = typeof(ImageKernels).Assembly.GetManifestResourceStream($"FrameMotionPlus.Cuda.Kernels.{module}.ptx")
            ?? throw new ArgumentException($"No embedded kernel module '{module}'.", nameof(module));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The blend weight the kernels use for timestep <paramref name="t"/>: round(t × 65536), clamped to [0, 65536].
    /// </summary>
    public static uint BlendWeight(float t)
    {
        if (!(t >= 0f))
        {
            return 0;
        }

        return t >= 1f ? 65536u : (uint)MathF.Round(t * 65536f, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Draws test pattern frame <paramref name="frameIndex"/> into <paramref name="target"/> (RGBA8, BGRA8 or RGB10A2).
    /// Layout: see native/kernels/pattern.cu.
    /// </summary>
    public void EnqueueTestPattern(CudaStream stream, in GpuFrame target, uint frameIndex)
    {
        FrameChecks.ValidateCudaImage(target, nameof(target));
        var dst = target.Address + target.Offset;
        var pitch = (uint)target.PitchBytes;
        var width = target.Width;
        var height = target.Height;
        var grid = Grid(width, height);
        if (target.Format == PixelFormat.Rgb10A2)
        {
            var args = stackalloc void*[5] { &dst, &pitch, &width, &height, &frameIndex };
            _patternRgb10A2.Launch(stream, grid, _block, 0, args);
        }
        else
        {
            var swap = target.Format == PixelFormat.Bgra8 ? 1 : 0;
            var args = stackalloc void*[6] { &dst, &pitch, &width, &height, &frameIndex, &swap };
            _patternRgba8.Launch(stream, grid, _block, 0, args);
        }
    }

    /// <summary>
    /// <paramref name="output"/> = (1 − t)·<paramref name="prev"/> + t·<paramref name="curr"/> per channel, rounded
    /// half up at weight <see cref="BlendWeight"/>(t). All three frames must have the same size and pixel format.
    /// </summary>
    public void EnqueueBlend(CudaStream stream, in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output)
    {
        if (!(t >= 0f && t <= 1f))
        {
            ThrowTimestep(t);
        }

        EnqueueBlendWeight(stream, prev, curr, BlendWeight(t), output);
    }

    /// <summary>As <see cref="EnqueueBlend"/> with an explicit weight in [0, 65536].</summary>
    public void EnqueueBlendWeight(CudaStream stream, in GpuFrame prev, in GpuFrame curr, uint weight, in GpuFrame output)
    {
        FrameChecks.ValidateCudaImage(prev, nameof(prev));
        FrameChecks.ValidateCudaImage(curr, nameof(curr));
        FrameChecks.ValidateCudaImage(output, nameof(output));
        if (!SameShape(prev, curr) || !SameShape(prev, output))
        {
            ThrowShape();
        }

        if (weight > 65536u)
        {
            ThrowWeight(weight);
        }

        var a = prev.Address + prev.Offset;
        var aPitch = (uint)prev.PitchBytes;
        var b = curr.Address + curr.Offset;
        var bPitch = (uint)curr.PitchBytes;
        var dst = output.Address + output.Offset;
        var dstPitch = (uint)output.PitchBytes;
        var width = output.Width;
        var height = output.Height;
        var args = stackalloc void*[9] { &a, &aPitch, &b, &bPitch, &dst, &dstPitch, &width, &height, &weight };
        var kernel = output.Format == PixelFormat.Rgb10A2 ? _blendRgb10A2 : _blendRgba8;
        kernel.Launch(stream, Grid(width, height), _block, 0, args);
    }

    /// <summary>
    /// Converts between pixel formats of the same size: RGBA8/BGRA8 to RGB10A2, RGB10A2 to RGBA8/BGRA8, and RGBA8 to
    /// BGRA8 or back (rounding: see native/kernels/convert.cu). Same-format copies belong to
    /// <see cref="CudaStream.CopyFrameAsync"/>.
    /// </summary>
    public void EnqueueConvert(CudaStream stream, in GpuFrame source, in GpuFrame destination)
    {
        FrameChecks.ValidateCudaImage(source, nameof(source));
        FrameChecks.ValidateCudaImage(destination, nameof(destination));
        if (source.Width != destination.Width || source.Height != destination.Height || source.Format == destination.Format)
        {
            ThrowConvert(source.Format, destination.Format);
        }

        var src = source.Address + source.Offset;
        var srcPitch = (uint)source.PitchBytes;
        var dst = destination.Address + destination.Offset;
        var dstPitch = (uint)destination.PitchBytes;
        var width = destination.Width;
        var height = destination.Height;
        var grid = Grid(width, height);
        if (source.Format == PixelFormat.Rgb10A2)
        {
            var bgra = destination.Format == PixelFormat.Bgra8 ? 1 : 0;
            var args = stackalloc void*[7] { &src, &srcPitch, &dst, &dstPitch, &width, &height, &bgra };
            _rgb10A2ToRgba8.Launch(stream, grid, _block, 0, args);
        }
        else if (destination.Format == PixelFormat.Rgb10A2)
        {
            var bgra = source.Format == PixelFormat.Bgra8 ? 1 : 0;
            var args = stackalloc void*[7] { &src, &srcPitch, &dst, &dstPitch, &width, &height, &bgra };
            _rgba8ToRgb10A2.Launch(stream, grid, _block, 0, args);
        }
        else
        {
            var args = stackalloc void*[6] { &src, &srcPitch, &dst, &dstPitch, &width, &height };
            _swapRedBlue8.Launch(stream, grid, _block, 0, args);
        }
    }

    /// <summary>
    /// Converts the 4:2:0 YCbCr image <paramref name="source"/> into <paramref name="destination"/> (pitch-linear RGBA8 or
    /// RGB10A2 of the same size) by <paramref name="conversion"/>: chroma upsampled bilinearly at its siting, then the
    /// stream's matrix and range in exact integer arithmetic (native/kernels/yuv.cu). Allocation-free.
    /// </summary>
    public void EnqueueYuvToRgb(CudaStream stream, in YuvPlanes source, in GpuFrame destination, in YuvConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(stream);
        FrameChecks.ValidateCudaImage(destination, nameof(destination));
        if (!conversion.IsValid || destination.Format != conversion.Output)
        {
            ThrowYuvOutput(destination.Format, conversion.Output);
        }

        ValidatePlanes(source, conversion.SourceBits);
        if (source.Width != destination.Width || source.Height != destination.Height)
        {
            ThrowYuvSize(source, destination);
        }

        var luma = source.Luma;
        var lumaPitch = (uint)source.LumaPitch;
        var chroma = source.Chroma;
        var chromaPitch = (uint)source.ChromaPitch;
        var dst = destination.Address + destination.Offset;
        var dstPitch = (uint)destination.PitchBytes;
        var width = destination.Width;
        var height = destination.Height;
        var parameters = conversion.Parameters;
        var args = stackalloc void*[9] { &luma, &lumaPitch, &chroma, &chromaPitch, &dst, &dstPitch, &width, &height, &parameters };
        _yuvToRgb.Launch(stream, Grid(width, height), _block, 0, args);
    }

    /// <summary>
    /// Converts packed 4:2:2 8-bit YCbCr (V4L2 YUYV: Y0 Cb Y1 Cr per pixel pair) at <paramref name="source"/> (device or
    /// mapped address, <paramref name="sourcePitch"/> bytes per row) into <paramref name="destination"/> by
    /// <paramref name="conversion"/> (an 8-bit conversion; only its horizontal siting applies: chroma is full height).
    /// Same arithmetic as <see cref="EnqueueYuvToRgb"/>. Allocation-free.
    /// </summary>
    public void EnqueueYuyvToRgb(CudaStream stream, ulong source, int sourcePitch, in GpuFrame destination, in YuvConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(stream);
        FrameChecks.ValidateCudaImage(destination, nameof(destination));
        if (!conversion.IsValid || destination.Format != conversion.Output)
        {
            ThrowYuvOutput(destination.Format, conversion.Output);
        }

        if (conversion.SourceBits != 8)
        {
            ThrowYuvFormat(YuvSampleFormat.Nv12, conversion.SourceBits);
        }

        var width = destination.Width;
        var height = destination.Height;
        if (source == 0 || (width & 1) != 0 || sourcePitch < 2L * width)
        {
            ThrowPacked(source, sourcePitch, width);
        }

        var src = source;
        var srcPitch = (uint)sourcePitch;
        var dst = destination.Address + destination.Offset;
        var dstPitch = (uint)destination.PitchBytes;
        var parameters = conversion.Parameters;
        var args = stackalloc void*[7] { &src, &srcPitch, &dst, &dstPitch, &width, &height, &parameters };
        _yuyvToRgb.Launch(stream, Grid(width, height), _block, 0, args);
    }

    /// <summary>
    /// The capture cadence metric of one frame's luma (native/kernels/cadence.cu): sums its 4x4-sample cells into
    /// <paramref name="current"/> (<see cref="CadenceGrid.Cells"/> 32-bit sums), compares them with
    /// <paramref name="reference"/> (the last unique frame's grid) and writes into <paramref name="result"/> (two 64-bit
    /// words, zeroed here first) the largest 32x32-pixel tile's mean absolute difference per luma sample in 1/65536 code
    /// units, and the largest cell sum. All addresses are device memory. Allocation-free.
    /// </summary>
    public void EnqueueCadenceMetric(CudaStream stream, ulong luma, int lumaPitch, in CadenceGrid grid, ulong reference, ulong current, ulong result)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (luma == 0 || reference == 0 || current == 0 || result == 0 || grid.CellsX <= 0 || grid.CellsY <= 0 ||
            lumaPitch < (long)grid.CellsX * CadenceGrid.CellSize * grid.Sampling.SampleBytes || (result & 7) != 0)
        {
            ThrowCadence(grid, lumaPitch);
        }

        stream.MemsetAsync(result, 0, 16);
        var src = luma;
        var pitch = (uint)lumaPitch;
        var refGrid = reference;
        var curGrid = current;
        var res = result;
        var parameters = new CadenceKernelParameters
        {
            CellsX = grid.CellsX,
            CellsY = grid.CellsY,
            SampleBytes = grid.Sampling.SampleBytes,
            Sixteen = grid.Sampling.Sixteen ? 1 : 0,
            Shift = grid.Sampling.Shift,
        };
        var args = stackalloc void*[6] { &src, &pitch, &refGrid, &curGrid, &res, &parameters };
        var blocks = new CudaDim3((uint)((grid.CellsX + CadenceGrid.TileCells - 1) / CadenceGrid.TileCells), (uint)((grid.CellsY + CadenceGrid.TileCells - 1) / CadenceGrid.TileCells));
        _cadence.Launch(stream, blocks, new CudaDim3(CadenceGrid.TileCells, CadenceGrid.TileCells), 0, args);
    }

    /// <summary>Keeps <paramref name="stream"/> busy for <paramref name="nanoseconds"/> of GPU time (diagnostics, tests).</summary>
    public void EnqueueDelay(CudaStream stream, long nanoseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);
        var ns = (ulong)nanoseconds;
        var args = stackalloc void*[1] { &ns };
        _delay.Launch(stream, new CudaDim3(1), new CudaDim3(1), 0, args);
    }

    public void Dispose()
    {
        foreach (var module in _modules)
        {
            module.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CudaDim3 Grid(int width, int height) =>
        new((uint)((width + (int)_block.X - 1) / (int)_block.X), (uint)((height + (int)_block.Y - 1) / (int)_block.Y));

    private static bool SameShape(in GpuFrame a, in GpuFrame b) =>
        a.Width == b.Width && a.Height == b.Height && a.Format == b.Format;

    private static void ValidatePlanes(in YuvPlanes source, int sourceBits)
    {
        var expected = sourceBits > 8 ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12;
        if (source.Format != expected)
        {
            ThrowYuvFormat(source.Format, sourceBits);
        }

        var rowBytes = (long)source.Width * source.BytesPerSample;
        var chromaRowBytes = (long)((source.Width + 1) / 2) * 2 * source.BytesPerSample;
        if (source.Width <= 0 || source.Height <= 0 || source.Luma == 0 || source.Chroma == 0 ||
            source.LumaPitch < rowBytes || source.ChromaPitch < chromaRowBytes ||
            source.Luma % (ulong)source.BytesPerSample != 0 || source.Chroma % (ulong)source.BytesPerSample != 0 ||
            source.LumaPitch % source.BytesPerSample != 0 || source.ChromaPitch % source.BytesPerSample != 0)
        {
            ThrowPlanes(source);
        }
    }

    [DoesNotReturn]
    private static void ThrowYuvOutput(PixelFormat destination, PixelFormat conversion) =>
        throw new ArgumentException($"The destination is {destination}; the conversion writes {conversion}.", nameof(destination));

    [DoesNotReturn]
    private static void ThrowYuvFormat(YuvSampleFormat source, int bits) =>
        throw new ArgumentException($"{source} samples do not match a {bits}-bit conversion (NV12 is 8-bit, P010 10-bit).", nameof(source));

    [DoesNotReturn]
    private static void ThrowYuvSize(in YuvPlanes source, in GpuFrame destination) =>
        throw new ArgumentException(
            $"The source is {source.Width}x{source.Height} and the destination {destination.Width}x{destination.Height}; the conversion does not scale.",
            nameof(destination));

    [DoesNotReturn]
    private static void ThrowPlanes(in YuvPlanes source) =>
        throw new ArgumentException($"Invalid planes: {source}.", nameof(source));

    [DoesNotReturn]
    private static void ThrowPacked(ulong source, int pitch, int width) =>
        throw new ArgumentException($"Invalid packed 4:2:2 source: address 0x{source:x}, pitch {pitch} for width {width} (an even width, at least 2 bytes per pixel).", nameof(source));

    [DoesNotReturn]
    private static void ThrowCadence(in CadenceGrid grid, int pitch) =>
        throw new ArgumentException($"Invalid cadence metric arguments: {grid}, luma pitch {pitch} (non-zero device addresses, an 8-byte aligned result).");

    [DoesNotReturn]
    private static void ThrowTimestep(float t) =>
        throw new ArgumentOutOfRangeException(nameof(t), t, "The blend timestep must lie in [0, 1].");

    [DoesNotReturn]
    private static void ThrowWeight(uint weight) =>
        throw new ArgumentOutOfRangeException(nameof(weight), weight, "The blend weight must lie in [0, 65536].");

    [DoesNotReturn]
    private static void ThrowShape() =>
        throw new ArgumentException("Previous, current and output frames must have the same size and pixel format.");

    [DoesNotReturn]
    private static void ThrowConvert(PixelFormat source, PixelFormat destination) =>
        throw new ArgumentException($"Cannot convert {source} to {destination}: sizes must match and the formats must differ.");
}
