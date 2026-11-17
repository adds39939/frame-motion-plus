using System.Diagnostics;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.App.Vfg;

/// <summary>
/// The source frames a VFG benchmark cell interpolates between: <see cref="Count"/> consecutive frames of moving
/// content in device memory, used as a ping-pong sequence of pairs (0→1, 1→2, …, then back), so the motion between
/// every pair is continuous and never a jump that shot-change detection would take for a cut. Pattern and pan content
/// also have the true frame at every quarter between neighbours (<see cref="DrawTruth"/>).
/// </summary>
internal sealed class VfgContent : IDisposable
{
    private readonly CudaDeviceBuffer[] _frames;
    private readonly ImageKernels _kernels;
    private readonly uint _patternBase;
    private readonly SyntheticTexture? _texture;
    private readonly int _panStep;

    private VfgContent(VfgContentKind kind, CudaDeviceBuffer[] frames, ImageKernels kernels, uint patternBase, SyntheticTexture? texture, int panStep)
    {
        Kind = kind;
        _frames = frames;
        _kernels = kernels;
        _patternBase = patternBase;
        _texture = texture;
        _panStep = panStep;
    }

    public VfgContentKind Kind { get; }

    public int Count => _frames.Length;

    public int Width => _frames[0].Width;

    public int Height => _frames[0].Height;

    public PixelFormat Format => _frames[0].Format;

    /// <summary>True when <see cref="DrawTruth"/> can draw the exact intermediate frames.</summary>
    public bool HasTruth => Kind != VfgContentKind.Video;

    /// <summary>Rows where the content moves (the pattern's box band; every row otherwise).</summary>
    public (int Y0, int Y1) MotionRows => Kind == VfgContentKind.Pattern ? (Height * 7 / 12, Height * 9 / 12) : (0, Height);

    /// <summary>Pixels a pair of neighbours moves apart (the pan's step; the pattern box's 4 × W/240), 0 for video.</summary>
    public int MotionPixels => Kind switch
    {
        VfgContentKind.Pattern => 4 * Math.Max(Width / 240, 1),
        VfgContentKind.Pan => _panStep,
        _ => 0,
    };

    public GpuFrame Frame(int index) => _frames[index].AsFrame();

    /// <summary>Pair <paramref name="sequence"/> of the ping-pong order: (from, to) indices of neighbouring frames.</summary>
    public (int From, int To) Pair(long sequence)
    {
        var lap = 2 * (Count - 1);
        var k = (int)(sequence % lap);
        return k < Count - 1 ? (k, k + 1) : (lap - k, lap - k - 1);
    }

    public static VfgContent Create(VfgContentKind kind, string? video, CudaContext context, ImageKernels kernels, int width, int height, PixelFormat format, int count = 8)
    {
        var frames = new CudaDeviceBuffer[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                frames[i] = CudaDeviceBuffer.AllocateImage(context, width, height, format);
            }

            using var stream = CudaStream.Create(context);
            switch (kind)
            {
                case VfgContentKind.Pattern:
                {
                    // Frames 4i of the pattern from a start where the box does not wrap within the sequence.
                    const uint start = 8;
                    for (var i = 0; i < count; i++)
                    {
                        kernels.EnqueueTestPattern(stream, frames[i].AsFrame(), start + (4 * (uint)i));
                    }

                    stream.Synchronize();
                    return new VfgContent(kind, frames, kernels, start, null, 0);
                }

                case VfgContentKind.Pan:
                {
                    var step = 4 * Math.Max(1, (width + 479) / 480);
                    var texture = new SyntheticTexture(width + (count * step), height);
                    var pixels = new uint[width * height];
                    for (var i = 0; i < count; i++)
                    {
                        Crop(texture, i * step, width, height, format, pixels);
                        stream.CopyFrameFromHost(pixels, frames[i].AsFrame());
                        stream.Synchronize();
                    }

                    return new VfgContent(kind, frames, kernels, 0, texture, step);
                }

                default:
                {
                    var decoded = FfmpegFrames.Decode(video!, width, height, format, count);
                    for (var i = 0; i < count; i++)
                    {
                        stream.CopyFrameFromHost(decoded[i], frames[i].AsFrame());
                        stream.Synchronize();
                    }

                    return new VfgContent(kind, frames, kernels, 0, null, 0);
                }
            }
        }
        catch
        {
            foreach (var f in frames)
            {
                f?.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Draws the true frame at t = <paramref name="quarter"/> / 4 between neighbours <paramref name="from"/> and
    /// <paramref name="to"/> into <paramref name="target"/> (pattern: the kernel's frame in between; pan: the texture at
    /// the offset in between). <paramref name="scratch"/> holds W × H pixels.
    /// </summary>
    public void DrawTruth(CudaStream stream, int from, int to, int quarter, in GpuFrame target, uint[] scratch)
    {
        Debug.Assert(HasTruth && Math.Abs(to - from) == 1 && quarter is >= 1 and <= 3);
        var direction = to - from;
        if (Kind == VfgContentKind.Pattern)
        {
            var index = (long)_patternBase + (4L * from) + (direction * quarter);
            _kernels.EnqueueTestPattern(stream, target, (uint)index);
            return;
        }

        var offset = (from * _panStep) + (direction * quarter * _panStep / 4);
        Crop(_texture!, offset, Width, Height, Format, scratch);
        stream.CopyFrameFromHost(scratch, target);
    }

    public void Dispose()
    {
        foreach (var f in _frames)
        {
            f.Dispose();
        }
    }

    private static void Crop(SyntheticTexture texture, int offsetX, int width, int height, PixelFormat format, uint[] pixels)
    {
        var tenBit = format == PixelFormat.Rgb10A2;
        var max = tenBit ? 1023.0 : 255.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = texture.At(x + offsetX, y);
                pixels[(y * width) + x] = PixelCodes.Pack((uint)Math.Round(r * max), (uint)Math.Round(g * max), (uint)Math.Round(b * max), format);
            }
        }
    }
}

/// <summary>Packing and unpacking of the 32-bit formats' code values.</summary>
internal static class PixelCodes
{
    public static uint Pack(uint r, uint g, uint b, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => r | (g << 10) | (b << 20) | (3u << 30),
        PixelFormat.Bgra8 => b | (g << 8) | (r << 16) | (255u << 24),
        _ => r | (g << 8) | (b << 16) | (255u << 24),
    };

    public static (uint R, uint G, uint B) Unpack(uint pixel, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => (pixel & 0x3FF, (pixel >> 10) & 0x3FF, (pixel >> 20) & 0x3FF),
        PixelFormat.Bgra8 => ((pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF),
        _ => (pixel & 0xFF, (pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF),
    };

    public static uint MaxCode(PixelFormat format) => format == PixelFormat.Rgb10A2 ? 1023u : 255u;
}

/// <summary>Frames decoded by the ffmpeg command-line tool into tightly packed 32-bit pixels.</summary>
internal static class FfmpegFrames
{
    /// <summary>
    /// The first frame of <paramref name="input"/> (a picture or a video) scaled to cover <paramref name="width"/> ×
    /// <paramref name="height"/> and cropped to it, as 16-bit RGB samples in the input's own encoding (sRGB for a picture).
    /// </summary>
    public static ushort[] DecodeRgb48(string input, int width, int height)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var filter = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height}");
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", input, "-frames:v", "1", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "rgb48le", "pipe:1" })
        {
            info.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("cannot start ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new HardwareUnavailableException($"needs the ffmpeg command-line tool for the photo scenes: {e.Message}", e);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            using var memory = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(memory);
            process.WaitForExit(60_000);
            var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
            if (bytes.Length < width * height * 6)
            {
                var message = stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result.ReplaceLineEndings(" ").Trim() : string.Empty;
                throw new Cli.UsageException($"ffmpeg could not decode '{input}'{(message.Length > 0 ? ": " + message : string.Empty)}");
            }

            var samples = new ushort[width * height * 3];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (ushort)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
            }

            return samples;
        }
    }

    /// <summary>
    /// The first <paramref name="count"/> frames of <paramref name="input"/> (a file, or "lavfi:SPEC" for an ffmpeg lavfi
    /// source), scaled to <paramref name="width"/> × <paramref name="height"/>: 8-bit RGBA, or 16-bit RGB reduced to
    /// RGB10A2 code values (no transfer-function change: what the input holds, in its own encoding).
    /// </summary>
    public static uint[][] Decode(string input, int width, int height, PixelFormat format, int count, string? extraFilter = null)
    {
        var tenBit = format == PixelFormat.Rgb10A2;
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin" })
        {
            info.ArgumentList.Add(a);
        }

        if (input.StartsWith("lavfi:", StringComparison.Ordinal))
        {
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("lavfi");
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(input["lavfi:".Length..]);
        }
        else
        {
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(input);
        }

        var filter = $"{(extraFilter is null ? string.Empty : extraFilter + ",")}scale={width}:{height}:flags=bicubic";
        foreach (var a in new[] { "-frames:v", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "-vf", filter, "-f", "rawvideo", "-pix_fmt", tenBit ? "rgb48le" : "rgba", "pipe:1" })
        {
            info.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("cannot start ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new HardwareUnavailableException($"needs the ffmpeg command-line tool for video content: {e.Message}", e);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            var bytesPerPixel = tenBit ? 6 : 4;
            var frameBytes = width * height * bytesPerPixel;
            var frames = new List<uint[]>();
            var buffer = new byte[frameBytes];
            var output = process.StandardOutput.BaseStream;
            while (frames.Count < count)
            {
                var read = 0;
                while (read < frameBytes)
                {
                    var n = output.Read(buffer, read, frameBytes - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < frameBytes)
                {
                    break;
                }

                var pixels = new uint[width * height];
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (tenBit)
                    {
                        var o = i * 6;
                        var r = (uint)(buffer[o] | (buffer[o + 1] << 8)) >> 6;
                        var g = (uint)(buffer[o + 2] | (buffer[o + 3] << 8)) >> 6;
                        var b = (uint)(buffer[o + 4] | (buffer[o + 5] << 8)) >> 6;
                        pixels[i] = PixelCodes.Pack(r, g, b, PixelFormat.Rgb10A2);
                    }
                    else
                    {
                        var o = i * 4;
                        pixels[i] = PixelCodes.Pack(buffer[o], buffer[o + 1], buffer[o + 2], format);
                    }
                }

                frames.Add(pixels);
            }

            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
            }

            if (frames.Count < count)
            {
                var message = stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result.ReplaceLineEndings(" ").Trim() : string.Empty;
                throw new Cli.UsageException($"ffmpeg gave {frames.Count} of {count} frames from '{input}'{(message.Length > 0 ? ": " + message : string.Empty)}");
            }

            return [.. frames];
        }
    }
}
