using System.Diagnostics;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Sources.File.Tests.Support;

/// <summary>Reading frames back from a <see cref="FileFrameSource"/> and comparing them with the software decode.</summary>
internal static class Frames
{
    /// <summary>Reads the next frame, waiting (bounded) for one; false at the end of the stream.</summary>
    public static bool Next(FileFrameSource source, out SourceFrame frame, double timeoutS = 30)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutS * Stopwatch.Frequency);
        while (true)
        {
            if (source.TryRead(out frame))
            {
                return true;
            }

            if (source.Failure is { } failure)
            {
                throw new InvalidOperationException("the decode thread failed", failure);
            }

            if (source.IsEndOfStream)
            {
                return false;
            }

            Assert.True(Stopwatch.GetTimestamp() < deadline, $"no frame within {timeoutS} s");
            Thread.Sleep(0);
        }
    }

    /// <summary>The decoded planes of a held frame as code values (P010: the high 10 bits; the low 6 checked zero).</summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr) Planes(CudaEnvironment env, FileFrameSource source, in SourceFrame frame)
    {
        Assert.True(source.TryGetPlanes(frame.FrameId, out var planes), "the source kept no decoded planes");
        var w = planes.Width;
        var h = planes.Height;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var bps = planes.BytesPerSample;
        using var stream = CudaStream.Create(env.Context);
        using var luma = CudaDeviceBuffer.Allocate(env.Context, (long)w * h * bps);
        using var chroma = CudaDeviceBuffer.Allocate(env.Context, (long)cw * 2 * ch * bps);
        stream.Wait(frame.Ready);
        stream.Copy2DAsync(luma.Address, (long)w * bps, planes.Luma, planes.LumaPitch, (long)w * bps, h);
        stream.Copy2DAsync(chroma.Address, (long)cw * 2 * bps, planes.Chroma, planes.ChromaPitch, (long)cw * 2 * bps, ch);
        var l = new byte[luma.SizeBytes];
        var c = new byte[chroma.SizeBytes];
        stream.CopyToHost<byte>(luma.Address, l);
        stream.CopyToHost<byte>(chroma.Address, c);
        var y = new ushort[w * h];
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        for (var i = 0; i < y.Length; i++)
        {
            var v = bps == 1 ? l[i] : BitConverter.ToUInt16(l, 2 * i);
            Assert.True(bps == 1 || (v & 0x3F) == 0, $"P010 luma sample {i} has low bits set: 0x{v:x4}");
            y[i] = (ushort)(bps == 1 ? v : v >> 6);
        }

        for (var i = 0; i < cb.Length; i++)
        {
            cb[i] = bps == 1 ? c[2 * i] : (ushort)(BitConverter.ToUInt16(c, 4 * i) >> 6);
            cr[i] = bps == 1 ? c[(2 * i) + 1] : (ushort)(BitConverter.ToUInt16(c, (4 * i) + 2) >> 6);
        }

        return (y, cb, cr);
    }

    /// <summary>The RGB pixels of a held frame.</summary>
    public static uint[] Rgb(CudaEnvironment env, in SourceFrame frame)
    {
        using var stream = CudaStream.Create(env.Context);
        stream.Wait(frame.Ready);
        var pixels = new uint[frame.Frame.Width * frame.Frame.Height];
        stream.CopyFrameToHost(frame.Frame, pixels);
        return pixels;
    }

    /// <summary>Planar code values of one software-decoded frame (the raw layout of yuv420p / yuv420p10le).</summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr) Split(byte[] raw, int w, int h, int bps)
    {
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        ushort At(int sample) => bps == 1 ? raw[sample] : BitConverter.ToUInt16(raw, sample * 2);
        var y = new ushort[w * h];
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        for (var i = 0; i < y.Length; i++)
        {
            y[i] = At(i);
        }

        for (var i = 0; i < cb.Length; i++)
        {
            cb[i] = At(y.Length + i);
            cr[i] = At(y.Length + cb.Length + i);
        }

        return (y, cb, cr);
    }

    /// <summary>
    /// Plays <paramref name="path"/> to its end with decoded planes kept and checks every frame: planes bit-exact with the
    /// ffmpeg CLI's software decode, RGB bit-exact with the CPU reference of those planes (converted as the source says it
    /// converted that frame: its matrix, range and siting), content times increasing. Returns the frames compared.
    /// </summary>
    public static int CheckAgainstSoftwareDecode(MediaEnvironment env, string path, int maxFrames = int.MaxValue)
    {
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { KeepDecodedPlanes = true, Kernels = env.Kernels });
        var info = source.StreamInfo;
        var bps = info.BitDepth > 8 ? 2 : 1;
        using var software = SoftwareDecode.Start(env.Ffmpeg!, path, info.Width, info.Height, bps);
        var raw = new byte[software.FrameBytes];
        var references = new Dictionary<(MatrixCoefficients, ColorRange, ChromaSiting), YuvReference>();
        source.Start();
        var count = 0;
        var last = long.MinValue;
        while (count < maxFrames && Next(source, out var frame))
        {
            Assert.True(frame.ContentTimeNs > last, $"frame {frame.FrameId}: content time {frame.ContentTimeNs} after {last}");
            last = frame.ContentTimeNs;
            Assert.Equal(count, frame.FrameId);
            Assert.True(software.Read(raw), $"frame {count}: the software decode has fewer frames ({software.Errors})");
            var (sy, scb, scr) = Split(raw, info.Width, info.Height, bps);
            var (y, cb, cr) = Planes(env, source, frame);
            Assert.True(sy.AsSpan().SequenceEqual(y), $"frame {count}: luma differs from the software decode at sample {sy.AsSpan().CommonPrefixLength(y)}");
            Assert.True(scb.AsSpan().SequenceEqual(cb), $"frame {count}: Cb differs from the software decode at sample {scb.AsSpan().CommonPrefixLength(cb)}");
            Assert.True(scr.AsSpan().SequenceEqual(cr), $"frame {count}: Cr differs from the software decode at sample {scr.AsSpan().CommonPrefixLength(cr)}");
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var position));
            var c = position.Conversion;
            if (!references.TryGetValue((c.Matrix, c.Range, c.Siting), out var reference))
            {
                reference = YuvReference.Create(c.Matrix, c.Range, info.BitDepth, c.Siting, info.OutputFormat);
                references[(c.Matrix, c.Range, c.Siting)] = reference;
            }

            var expected = new uint[info.Width * info.Height];
            reference.Convert(y, cb, cr, info.Width, info.Height, expected);
            var rgb = Rgb(env, frame);
            Assert.True(expected.AsSpan().SequenceEqual(rgb), "frame " + count + ": " + global::FrameMotionPlus.Sources.File.Tests.Planes.FirstDifference(expected, rgb, info.Width));
            source.Release(frame.FrameId);
            count++;
        }

        if (count < maxFrames)
        {
            Assert.False(software.Read(raw), "the software decode has more frames than NVDEC delivered");
        }

        Assert.Null(source.Failure);
        Assert.Equal(0, source.Statistics.PoolGrowth);
        return count;
    }

    /// <summary>Every frame of the ffmpeg CLI's software decode of <paramref name="path"/>, as planar code values.</summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr)[] Software(MediaEnvironment env, string path, FileStreamInfo info)
    {
        var bps = info.BitDepth > 8 ? 2 : 1;
        using var software = SoftwareDecode.Start(env.Ffmpeg!, path, info.Width, info.Height, bps);
        var raw = new byte[software.FrameBytes];
        var frames = new List<(ushort[], ushort[], ushort[])>();
        while (software.Read(raw))
        {
            frames.Add(Split(raw, info.Width, info.Height, bps));
        }

        return [.. frames];
    }

    /// <summary>Waits (bounded) until <paramref name="condition"/> holds.</summary>
    public static void WaitUntil(Func<bool> condition, string what, double timeoutS = 10)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutS * Stopwatch.Frequency);
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"{what}: not within {timeoutS} s");
            Thread.Sleep(1);
        }
    }
}
