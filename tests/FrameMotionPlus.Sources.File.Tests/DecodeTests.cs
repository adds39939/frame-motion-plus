using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File.Interop;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// <see cref="FileFrameSource"/> on clips the ffmpeg CLI makes at test time: decoded pictures bit-exact with the
/// software decode for every codec, size, bit depth, range and matrix; content times from the timestamps; loops, the end
/// of the file and seeks as discontinuities; colour and HDR metadata; FFmpeg on the application's CUDA context.
/// </summary>
[Trait(Category.Name, Category.Media)]
public class DecodeTests
{
    /// <summary>
    /// Every decoded NV12/P010 picture equals the ffmpeg CLI's software decode (conformant decoders are bit-exact), and
    /// every converted frame equals the CPU reference of it: H.264, HEVC 8-bit and Main10 (PQ, BT.2020, top-left chroma),
    /// AV1 (10-bit), VP9, B-frames, variable frame rate, a non-zero start, an elementary stream, sizes up to 2160p and not a multiple of
    /// 16, full range, BT.601.
    /// </summary>
    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("hevc10-pq")]
    [InlineData("av1")]
    [InlineData("vp9")]
    [InlineData("vfr")]
    [InlineData("start-offset")]
    [InlineData("raw-h264")]
    [InlineData("h264-odd")]
    [InlineData("hevc10-odd")]
    [InlineData("h264-full")]
    [InlineData("h264-bt601")]
    [InlineData("h264-sd-untagged")]
    [InlineData("h264-1080p")]
    [InlineData("h264-1440p")]
    [InlineData("h264-2160p")]
    [InlineData("hevc10-2160p")]
    [InlineData("h264-ts")]
    [InlineData("ts-concat")]
    [InlineData("ts-wrap")]
    public void Decoded_frames_are_bit_exact_with_the_software_decode(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var frames = Frames.CheckAgainstSoftwareDecode(env, path);
        Log.WriteLine($"{clip}: {frames} frames bit-exact (planes and RGB)");
        Assert.Equal(env.FrameTimes(path).Length, frames);
    }

    /// <summary>
    /// Content times are the presentation timestamps (in presentation order, B-frames reordered by the decoder) minus the
    /// stream's start, in ns: compared with ffprobe's best-effort timestamps (within 1 µs, its printed precision), for
    /// B-frames, variable frame rate, a 1.4 s start (MPEG-TS) and an elementary stream without container timestamps.
    /// </summary>
    [Theory]
    [InlineData("h264")]
    [InlineData("vfr")]
    [InlineData("start-offset")]
    [InlineData("raw-h264")]
    [InlineData("ts-wrap")]
    public void Content_times_are_the_presentation_timestamps(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var expected = env.FrameTimes(path);
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
        source.Start();
        var times = new List<long>();
        while (Frames.Next(source, out var frame))
        {
            times.Add(frame.ContentTimeNs);
            Assert.Equal(SourceFrameMarks.None, frame.Marks);
            source.Release(frame.FrameId);
        }

        Assert.Equal(expected.Length, times.Count);

        // An elementary stream has no timestamps at all: frames are placed one frame duration (here 1/25 s) apart.
        var untimed = expected.All(double.IsNaN);
        var start = untimed ? 0 : expected[0];
        for (var i = 0; i < times.Count; i++)
        {
            var want = untimed ? i * 40_000_000L : (long)Math.Round((expected[i] - start) * 1e9);
            Assert.True(Math.Abs(times[i] - want) <= 1_000, $"frame {i}: content {times[i]} ns, expected {want} ns");
            Assert.True(i == 0 || times[i] > times[i - 1]);
        }

        Assert.Equal(untimed ? times.Count : 0, source.Statistics.PtsMissing);
        Assert.Equal(0, source.Statistics.PtsRepairs);
        Assert.Equal(0, source.Statistics.TimestampDiscontinuities); // ts-wrap: libavformat unwraps the 33-bit clock

        var intervals = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        Log.WriteLine($"{clip}: {times.Count} frames, start {start:0.000} s, intervals {intervals.Min() / 1e6:0.000}..{intervals.Max() / 1e6:0.000} ms; stream start ticks {source.StreamInfo.StartTicks}");
        if (clip == "vfr")
        {
            Assert.Contains(intervals, d => Math.Abs(d - 43_000_000) <= 1_000_000);
            Assert.Contains(intervals, d => Math.Abs(d - 23_000_000) <= 1_000_000);
        }

        if (clip == "start-offset")
        {
            Assert.True(start > 1, $"the MPEG-TS clip was expected to start after 1 s, at {start}");
        }
    }

    /// <summary>
    /// A broken file whose timestamps collide in pairs still gets distinct, strictly increasing content times: each
    /// collision is isolated (the frame after it advances again), so it is moved 1 ns after the previous frame, counted
    /// and flagged on the frame; it is not a discontinuity, and no two frames in a row are repaired.
    /// </summary>
    [Fact]
    public void Colliding_timestamps_are_repaired_to_strictly_increasing_content_times()
    {
        var env = Media.Require();
        var path = env.Clip("dup-pts");
        var expected = env.FrameTimes(path);
        Assert.Contains(expected.Zip(expected.Skip(1)), p => p.First == p.Second); // the clip really is broken
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
        source.Start();
        var last = long.MinValue;
        var n = 0;
        var repairedBefore = false;
        var flagged = 0;
        while (Frames.Next(source, out var frame))
        {
            Assert.True(frame.ContentTimeNs > last);
            Assert.Equal(SourceFrameMarks.None, frame.Marks);
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var info));
            var repaired = (info.Events & FileFrameEvents.TimestampRepaired) != 0;
            Assert.False(repaired && repairedBefore, $"frame {frame.FrameId}: two repaired frames in a row");
            Assert.Equal(repaired, frame.ContentTimeNs - last == 1);
            flagged += repaired ? 1 : 0;
            repairedBefore = repaired;
            last = frame.ContentTimeNs;
            source.Release(frame.FrameId);
            n++;
        }

        Assert.Equal(expected.Length, n);
        Assert.Equal(expected.Zip(expected.Skip(1)).Count(p => p.Second <= p.First), source.Statistics.PtsRepairs);
        Assert.Equal(source.Statistics.PtsRepairs, flagged);
        Assert.Equal(0, source.Statistics.TimestampDiscontinuities);
    }

    /// <summary>
    /// Two recordings concatenated (MPEG-TS, `cat a.ts b.ts`): the second one's timestamps start again at 1.47 s after the
    /// first ended at 2.43 s; another has them jump 99 s forward. Both are timestamp discontinuities, not collisions:
    /// the first frame of the second part is marked and continues one frame interval after the last of the first, every
    /// other step is the real frame interval, nothing is "repaired" (the old behaviour squeezed the whole second part
    /// into nanosecond steps), and the frames are those of the software decode, which goes straight through.
    /// </summary>
    [Theory]
    [InlineData("ts-concat")]
    [InlineData("ts-concat-late")]
    public void Timestamp_jumps_in_the_stream_are_discontinuities_with_real_intervals(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var expected = env.FrameTimes(path);
        Assert.Equal(60, expected.Length);
        Assert.True(Math.Abs(expected[30] - expected[29]) > 0.5, "the clip has no jump at frame 30");
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
        source.Start();
        var interval = 1_000_000_000L / 30;
        var times = new List<long>();
        while (Frames.Next(source, out var frame))
        {
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var info));
            var jump = frame.FrameId == 30;
            Assert.Equal(jump ? SourceFrameMarks.Discontinuity : SourceFrameMarks.None, frame.Marks);
            Assert.Equal(jump ? FileFrameEvents.TimestampJump : FileFrameEvents.None, info.Events);
            Assert.True(Math.Abs(info.MediaTimeNs - (long)Math.Round((expected[frame.FrameId] - expected[0]) * 1e9)) <= 1_000);
            times.Add(frame.ContentTimeNs);
            source.Release(frame.FrameId);
        }

        Assert.Equal(60, times.Count);
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(Math.Abs(times[i] - times[i - 1] - interval) <= 1_000_000, $"frame {i}: content time advanced {times[i] - times[i - 1]} ns, one frame is {interval} ns");
        }

        var stats = source.Statistics;
        Assert.Equal(1, stats.TimestampDiscontinuities);
        Assert.Equal(0, stats.PtsRepairs);
        Assert.Equal(60, Frames.CheckAgainstSoftwareDecode(env, path));
    }

    /// <summary>
    /// A 12 s still in a container with a continuous timeline (Matroska, MP4) is content: the frame after it is marked (it
    /// is a cut for interpolation) but keeps its time, 12.033 s after the previous frame; every other step is the
    /// stream's. (In MPEG-TS the same gap is collapsed to one interval: <c>ts-concat-late</c> above.)
    /// </summary>
    [Theory]
    [InlineData("gap-mkv")]
    [InlineData("gap-mp4")]
    public void A_long_gap_on_a_continuous_timeline_is_marked_and_kept(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var expected = env.FrameTimes(path);
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
        Assert.False(source.StreamInfo.DiscontinuousTimestamps);
        source.Start();
        var times = new List<long>();
        while (Frames.Next(source, out var frame))
        {
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var info));
            var gap = frame.FrameId == 30;
            Assert.Equal(gap ? SourceFrameMarks.Discontinuity : SourceFrameMarks.None, frame.Marks);
            Assert.Equal(gap ? FileFrameEvents.TimestampJump : FileFrameEvents.None, info.Events);
            times.Add(frame.ContentTimeNs);
            source.Release(frame.FrameId);
        }

        Assert.Equal(expected.Length, times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            var want = (long)Math.Round((expected[i] - expected[0]) * 1e9);
            Assert.True(Math.Abs(times[i] - want) <= 1_000, $"frame {i}: content {times[i]} ns, the stream says {want} ns");
        }

        Assert.True(times[30] - times[29] > 12_000_000_000);
        Assert.Equal(1, source.Statistics.TimestampDiscontinuities);
    }

    /// <summary>
    /// Without looping the queue drains at the end and <see cref="FileFrameSource.IsEndOfStream"/> becomes true, with no
    /// marks. Looping, the first frame of every new lap is a discontinuity, and content time goes on increasing: the lap's
    /// first frame comes one frame interval after the last frame of the previous lap.
    /// </summary>
    [Fact]
    public void The_end_of_the_file_and_loops_are_reported_through_the_frames()
    {
        var env = Media.Require();
        var path = env.Clip("h264-3s");
        var frameCount = env.FrameTimes(path).Length;
        using (var once = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels }))
        {
            once.Start();
            var n = 0;
            while (Frames.Next(once, out var frame))
            {
                Assert.Equal(SourceFrameMarks.None, frame.Marks);
                once.Release(frame.FrameId);
                n++;
            }

            Assert.Equal(frameCount, n);
            Assert.True(once.IsEndOfStream);
            Assert.False(once.TryRead(out _));
        }

        using var looping = new FileFrameSource(env.Context, path, new FileSourceOptions { Loop = true, Kernels = env.Kernels });
        looping.Start();
        var interval = (long)looping.StreamInfo.NominalRate.PeriodNs;
        var last = long.MinValue;
        var laps = new List<long>();
        for (var i = 0; i < (2 * frameCount) + 10; i++)
        {
            Assert.True(Frames.Next(looping, out var frame));
            if ((frame.Marks & SourceFrameMarks.Discontinuity) != 0)
            {
                laps.Add(frame.FrameId);
                Assert.True(Math.Abs(frame.ContentTimeNs - last - interval) <= 1_000_000, $"lap starts {frame.ContentTimeNs - last} ns after the last frame; one interval is {interval}");
            }

            Assert.True(frame.ContentTimeNs > last);
            last = frame.ContentTimeNs;
            looping.Release(frame.FrameId);
        }

        Assert.Equal([frameCount, 2L * frameCount], laps);
        Assert.Equal(2, looping.Statistics.Loops);
        Assert.False(looping.IsEndOfStream);
    }

    /// <summary>
    /// A seek drops what was queued, restarts at the keyframe before the target, drops the frames before it, and marks the
    /// first frame shown: forward to 2 s (frame 60 of a 30 fps clip), then back to 0.5 s (frame 15). Each frame after a
    /// seek is the software decode's frame at that position, bit-exact, and content time never steps back.
    /// </summary>
    [Fact]
    public void Seeks_are_discontinuities_and_land_on_the_right_frame()
    {
        var env = Media.Require();
        var path = env.Clip("h264-3s");
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { KeepDecodedPlanes = true, Kernels = env.Kernels });
        var info = source.StreamInfo;
        var software = Frames.Software(env, path, info);
        source.Start();
        long last = long.MinValue;
        for (var i = 0; i < 10; i++)
        {
            Assert.True(Frames.Next(source, out var frame));
            last = frame.ContentTimeNs;
            source.Release(frame.FrameId);
        }

        Thread.Sleep(50); // the queue fills ahead with frames 10.. that the seek must drop
        foreach (var (target, index) in new[] { (2.0, 60), (0.5, 15) })
        {
            source.Seek((long)(target * 1e9));
            Assert.True(Frames.Next(source, out var first));
            Assert.True((first.Marks & SourceFrameMarks.Discontinuity) != 0, $"the first frame after the seek to {target} s is not marked");
            Assert.True(first.ContentTimeNs > last, "content time stepped back at a seek");
            var (y, cb, cr) = Frames.Planes(env, source, first);
            Assert.True(software[index].Y.AsSpan().SequenceEqual(y) && software[index].Cb.AsSpan().SequenceEqual(cb), $"the frame after the seek to {target} s is not frame {index}");
            source.Release(first.FrameId);
            Assert.True(Frames.Next(source, out var second));
            Assert.Equal(SourceFrameMarks.None, second.Marks);
            var (y2, _, _) = Frames.Planes(env, source, second);
            Assert.True(software[index + 1].Y.AsSpan().SequenceEqual(y2), $"the second frame after the seek to {target} s is not frame {index + 1}");
            last = second.ContentTimeNs;
            source.Release(second.FrameId);
        }

        Assert.Equal(2, source.Statistics.Seeks);
        Log.WriteLine($"seeks: {source.Statistics.FramesSkippedBySeek} frames decoded and dropped on the way to the targets");
    }

    /// <summary>A seek past the end of a looping file shows the next lap from its start (marked), rather than skipping for ever.</summary>
    [Fact]
    public void A_seek_past_the_end_of_a_looping_file_continues_with_the_next_lap()
    {
        var env = Media.Require();
        using var source = new FileFrameSource(env.Context, env.Clip("h264-3s"), new FileSourceOptions { Loop = true, Kernels = env.Kernels });
        source.Start();
        Assert.True(Frames.Next(source, out var first));
        source.Release(first.FrameId);
        source.Seek(60_000_000_000);
        Assert.True(Frames.Next(source, out var next, timeoutS: 10));
        Assert.True((next.Marks & SourceFrameMarks.Discontinuity) != 0);
        Assert.True(next.ContentTimeNs > first.ContentTimeNs);
        source.Release(next.FrameId);
    }

    /// <summary>
    /// Stream and frame colour metadata, with the defaults for what a stream leaves unspecified. The frames are RGB and
    /// their metadata says so (identity matrix, full range) with the content's primaries, transfer and HDR metadata; the
    /// YCbCr matrix, range and siting they were converted from are the stream's (its conversion), also per frame.
    /// </summary>
    [Fact]
    public void Colour_and_hdr_metadata_reach_the_frames()
    {
        var env = Media.Require();
        ColorMetadata Of(string clip, out FileStreamInfo info)
        {
            using var source = new FileFrameSource(env.Context, env.Clip(clip), new FileSourceOptions { Kernels = env.Kernels });
            info = source.StreamInfo;
            source.Start();
            Assert.True(Frames.Next(source, out var frame));
            Assert.Equal(source.Info.Color, frame.Frame.Color);
            Assert.Equal(info.Color, frame.Frame.Color);
            Assert.Equal(info.OutputFormat, frame.Frame.Format);
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var position));
            Assert.Equal(info.Conversion, position.Conversion);
            Assert.Equal(MatrixCoefficients.Identity, frame.Frame.Color.Matrix); // RGB pixels
            Assert.Equal(ColorRange.Full, frame.Frame.Color.Range);
            source.Release(frame.FrameId);
            return frame.Frame.Color;
        }

        var pq = Of("hevc10-pq", out var pqInfo);
        Assert.Equal(TransferFunction.Pq, pq.Transfer);
        Assert.Equal(ColorPrimaries.Bt2020, pq.Primaries);
        Assert.True(pq.IsHdr);
        Assert.Equal(new Chromaticity(0.68f, 0.32f), pq.Mastering.Red);
        Assert.Equal(new Chromaticity(0.265f, 0.69f), pq.Mastering.Green);
        Assert.Equal(new Chromaticity(0.15f, 0.06f), pq.Mastering.Blue);
        Assert.Equal(new Chromaticity(0.3127f, 0.329f), pq.Mastering.WhitePoint);
        Assert.Equal(1000f, pq.Mastering.MaxLuminanceNits);
        Assert.Equal(0.0001f, pq.Mastering.MinLuminanceNits, 1e-7f);
        Assert.Equal(new ContentLightLevel(1000, 400), pq.LightLevel);
        Assert.Equal(PixelFormat.Rgb10A2, pqInfo.OutputFormat);
        Assert.Equal(10, pqInfo.BitDepth);
        Assert.Equal(YuvSampleFormat.P010, pqInfo.SampleFormat);
        Assert.Equal(MatrixCoefficients.Bt2020NonConstant, pqInfo.Conversion.Matrix);
        Assert.Equal(ColorRange.Limited, pqInfo.Conversion.Range);
        Assert.Equal(ChromaSiting.TopLeft, pqInfo.Conversion.Siting);
        Assert.Equal("Main 10", pqInfo.Profile);

        var sdr = Of("h264", out var sdrInfo);
        Assert.Equal(ColorMetadata.SdrBt709, sdr);
        Assert.Equal(PixelFormat.Rgba8, sdrInfo.OutputFormat);
        Assert.Equal(MatrixCoefficients.Bt709, sdrInfo.Conversion.Matrix);
        Assert.Equal(ColorRange.Limited, sdrInfo.Conversion.Range);
        Assert.Equal(ChromaSiting.Left, sdrInfo.Conversion.Siting);
        Assert.Equal(new Core.Time.FrameRate(30, 1), sdrInfo.NominalRate);

        Of("h264-full", out var full);
        Assert.Equal(ColorRange.Full, full.Conversion.Range);
        var bt601 = Of("h264-bt601", out var bt601Info);
        Assert.Equal(MatrixCoefficients.Bt601, bt601Info.Conversion.Matrix);
        Assert.Equal(ColorPrimaries.Bt601Line525, bt601.Primaries); // SMPTE 170M
        var sd = Of("h264-sd-untagged", out var sdInfo);
        Assert.Equal(MatrixCoefficients.Bt601, sdInfo.Conversion.Matrix);
        Assert.Equal(ColorPrimaries.Unspecified, sd.Primaries); // 525 or 625 lines: not said
        var hd = Of("h264-hd-untagged", out var hdInfo);
        Assert.Equal(MatrixCoefficients.Bt709, hdInfo.Conversion.Matrix);
        Assert.Equal(ColorRange.Limited, hdInfo.Conversion.Range);
        Assert.Equal(ColorPrimaries.Bt709, hd.Primaries);
    }

    /// <summary>
    /// Back-pressure: a consumer that holds three frames and reads slowly (20 ms per frame) gets every frame, in order,
    /// none skipped; the decode thread waits for it, never queueing more than its decode-ahead.
    /// </summary>
    [Fact]
    public void A_slow_consumer_loses_no_frames()
    {
        var env = Media.Require();
        var path = env.Clip("h264-3s");
        var expected = env.FrameTimes(path).Length;
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { DecodeAhead = 2, HistoryDepth = 3, Kernels = env.Kernels });
        source.Start();
        var held = new Queue<long>();
        var ids = new List<long>();
        long last = long.MinValue;
        while (Frames.Next(source, out var frame))
        {
            ids.Add(frame.FrameId);
            Assert.True(frame.ContentTimeNs > last);
            last = frame.ContentTimeNs;
            held.Enqueue(frame.FrameId);
            if (held.Count == 3)
            {
                source.Release(held.Dequeue());
            }

            Assert.True(source.QueueDepth <= 2, $"{source.QueueDepth} frames queued ahead of a decode-ahead of 2");
            if (ids.Count <= 30)
            {
                Thread.Sleep(20);
            }
        }

        Assert.Equal(Enumerable.Range(0, expected).Select(i => (long)i), ids);
        var stats = source.Statistics;
        Assert.True(stats.BackPressureWaits > 0, "the decode thread never waited for the consumer");
        Assert.True(stats.MaxQueueDepth <= 2);
        Log.WriteLine($"slow consumer: {ids.Count} frames, {stats.BackPressureWaits} back-pressure waits, max queue {stats.MaxQueueDepth}");
    }

    /// <summary>
    /// A history buffer is written again only after the GPU work the consumer named at its release has finished: the
    /// consumer reads a frame, enqueues a reader that copies it after a 300 ms GPU delay, records an event and releases the
    /// frame with that event, then reads and releases the next frames at once, so the decoder cycles through its three
    /// buffers well within the delay. The delayed copy still sees the frame. Control: released with no signal, the buffer
    /// is overwritten before the delayed copy runs.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_buffer_is_not_overwritten_until_its_release_point(bool releaseWithReader)
    {
        var env = Media.Require();
        using var source = new FileFrameSource(env.Context, env.Clip("h264-3s"), new FileSourceOptions { DecodeAhead = 1, HistoryDepth = 2, Kernels = env.Kernels });
        source.Start();
        using var reader = CudaStream.Create(env.Context);
        using var readDone = CudaEvent.Create(env.Context);
        Assert.True(Frames.Next(source, out var frame));
        var original = Frames.Rgb(env, frame);
        using var copy = CudaDeviceBuffer.AllocateImage(env.Context, frame.Frame.Width, frame.Frame.Height, frame.Frame.Format);
        reader.Wait(frame.Ready);
        env.Kernels.EnqueueDelay(reader, 300_000_000);
        reader.CopyFrameAsync(frame.Frame, copy.AsFrame());
        readDone.Record(reader);
        source.Release(frame.FrameId, releaseWithReader ? readDone.Signal : GpuSignal.None);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(Frames.Next(source, out var next));
            source.Release(next.FrameId);
        }

        reader.Synchronize();
        var seen = new uint[original.Length];
        reader.CopyFrameToHost(copy.AsFrame(), seen);
        if (releaseWithReader)
        {
            Assert.True(seen.AsSpan().SequenceEqual(original), "the buffer was overwritten before its last reader finished");
        }
        else
        {
            Assert.False(seen.AsSpan().SequenceEqual(original), "control: released with no signal, the buffer was expected to be overwritten early");
        }
    }
}
