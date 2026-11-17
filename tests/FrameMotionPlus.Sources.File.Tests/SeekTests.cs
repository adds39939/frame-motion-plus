using System.Diagnostics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// Seeks and loops in every place the decoder can be: near the end of the file and while it drains (the end of the stream
/// signalled, frames still queued inside it), for each codec; elementary streams, which have no time index; and a file
/// that cannot be seeked at all.
/// </summary>
[Trait(Category.Name, Category.Media)]
public class SeekTests
{
    private const double TargetS = 0.5;

    /// <summary>
    /// For each of the last 16 positions of a clip, the consumer reads up to it, lets the decode thread run as far ahead
    /// as it can (its queue full, or the file's end reached: near the end, FFmpeg has been told the stream ended and is
    /// draining frames out of the decoder), then seeks to 0.5 s. The first frame shown must be the software decode's
    /// first frame at or after 0.5 s, marked, with the target's media time, after the last frame shown; the next five
    /// follow at the real frame intervals, each the software decode's. FFmpeg 9.0.2's HEVC decoder keeps frames its drain
    /// had queued for output across <c>avcodec_flush_buffers</c>; before the fix one of them came out first after such a
    /// seek (a frame from the end of the file, marked, then the target 1 ns later).
    /// </summary>
    [Theory]
    [InlineData("h264-3s")]
    [InlineData("hevc-2s")]
    [InlineData("hevc10-2s")]
    [InlineData("av1-2s")]
    [InlineData("vp9-2s")]
    [InlineData("ts-2s")]
    [InlineData("ts-onekey")]
    public void A_seek_near_the_end_or_while_the_decoder_drains_lands_on_the_target(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var times = env.FrameTimes(path);
        var n = times.Length;
        var media = times.Select(t => (long)Math.Round((t - times[0]) * 1e9)).ToArray();
        var target = Array.FindIndex(media, m => m >= (long)(TargetS * 1e9) - 1_000);
        (ushort[] Y, ushort[] Cb, ushort[] Cr)[]? software = null;
        long stale = 0;
        for (var k = n - 16; k < n; k++)
        {
            using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { KeepDecodedPlanes = true, Kernels = env.Kernels });
            software ??= Frames.Software(env, path, source.StreamInfo);
            Assert.Equal(n, software.Length);
            source.Start();
            var last = long.MinValue;
            for (var i = 0; i < k; i++)
            {
                Assert.True(Frames.Next(source, out var frame));
                last = frame.ContentTimeNs;
                source.Release(frame.FrameId);
            }

            Frames.WaitUntil(() => source.QueueDepth >= Math.Min(4, n - k), $"{clip}: the decode thread did not fill its queue at frame {k}");
            source.Seek((long)(TargetS * 1e9));
            Assert.True(Frames.Next(source, out var first));
            Assert.True(source.TryGetFrameInfo(first.FrameId, out var info));
            var where = $"{clip}, seek after frame {k}";
            Assert.True((first.Marks & SourceFrameMarks.Discontinuity) != 0, $"{where}: the first frame is not marked");
            Assert.True((info.Events & FileFrameEvents.Seek) != 0, $"{where}: events {info.Events}");
            Assert.True(Math.Abs(info.MediaTimeNs - media[target]) <= 1_000, $"{where}: the first frame is at media {info.MediaTimeNs} ns, the target frame {target} at {media[target]} ns");
            Assert.True(first.ContentTimeNs > last, $"{where}: content time stepped back");
            Assert.True(Frames.Planes(env, source, first).Y.AsSpan().SequenceEqual(software[target].Y), $"{where}: the first frame is not frame {target}");
            var previous = first.ContentTimeNs;
            source.Release(first.FrameId);
            for (var j = 1; j <= 5; j++)
            {
                Assert.True(Frames.Next(source, out var next));
                Assert.Equal(SourceFrameMarks.None, next.Marks);
                var step = next.ContentTimeNs - previous;
                var real = media[target + j] - media[target + j - 1];
                Assert.True(Math.Abs(step - real) <= 1_000, $"{where}: frame {j} after the seek came {step} ns after the one before, the stream says {real} ns");
                Assert.True(Frames.Planes(env, source, next).Y.AsSpan().SequenceEqual(software[target + j].Y), $"{where}: frame {j} after the seek is not frame {target + j}");
                previous = next.ContentTimeNs;
                source.Release(next.FrameId);
            }

            var stats = source.Statistics;
            Assert.Equal(0, stats.PtsRepairs);
            Assert.Equal(0, stats.TimestampDiscontinuities);
            Assert.Null(source.Failure);
            stale += stats.StaleFramesDropped;
        }

        Log.WriteLine($"{clip}: {n} frames, 16 seeks to frame {target}; {stale} frames from before a seek dropped by the decoder");
        if (clip.StartsWith("hevc", StringComparison.Ordinal))
        {
            Assert.True(stale > 0, "the sweep never caught the HEVC decoder with drained frames queued (it would not have tested the fix)");
        }
    }

    /// <summary>
    /// Looping, every lap starts with the file's first frame (the decoder has output everything at the end of the file,
    /// so nothing stale can come first), marked as a loop, one frame interval after the previous lap's last frame; every
    /// other step is the stream's own interval. An elementary stream, which has no timestamps and no time index, loops
    /// too: the demuxer goes back to its first byte.
    /// </summary>
    [Theory]
    [InlineData("h264-3s")]
    [InlineData("hevc-2s")]
    [InlineData("hevc10-2s")]
    [InlineData("av1-2s")]
    [InlineData("vp9-2s")]
    [InlineData("raw-h264")]
    [InlineData("ts-2s")]
    [InlineData("ts-onekey")]
    public void Every_lap_starts_with_the_first_frame(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var times = env.FrameTimes(path);
        var n = times.Length;
        var untimed = times.All(double.IsNaN);
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Loop = true, KeepDecodedPlanes = true, Kernels = env.Kernels });
        var software = Frames.Software(env, path, source.StreamInfo);
        var interval = (long)source.StreamInfo.NominalRate.PeriodNs;
        source.Start();
        var last = long.MinValue;
        for (var i = 0; i < (2 * n) + 3; i++)
        {
            Assert.True(Frames.Next(source, out var frame));
            Assert.True(source.TryGetFrameInfo(frame.FrameId, out var info));
            var index = i % n;
            var lapStart = i > 0 && index == 0;
            Assert.Equal(lapStart ? SourceFrameMarks.Discontinuity : SourceFrameMarks.None, frame.Marks);
            Assert.Equal(lapStart ? FileFrameEvents.Loop : FileFrameEvents.None, info.Events);
            Assert.Equal(i / n, info.Lap);
            if (i > 0)
            {
                var step = frame.ContentTimeNs - last;
                var real = lapStart || untimed ? interval : (long)Math.Round((times[index] - times[index - 1]) * 1e9);
                Assert.True(Math.Abs(step - real) <= 1_000_000, $"frame {i}: content advanced {step} ns, expected {real} ns");
            }

            if (index <= 1)
            {
                Assert.True(Frames.Planes(env, source, frame).Y.AsSpan().SequenceEqual(software[index].Y), $"frame {i} (lap {i / n}) is not frame {index}");
            }

            last = frame.ContentTimeNs;
            source.Release(frame.FrameId);
        }

        var stats = source.Statistics;
        Assert.Equal(2, stats.Loops);
        Assert.Equal(untimed || path.EndsWith(".ts", StringComparison.Ordinal) ? 2 : 0, stats.SeeksFromStart); // TS and raw restart by byte
        Assert.Equal(0, stats.FailedSeeks);
        Assert.Null(source.Failure);
    }

    /// <summary>
    /// An elementary stream has no time index, so a seek restarts it from its first byte and decodes forward to the target
    /// (its frames are timed one interval apart from the start): the first frame shown is the one at 0.52 s (25 fps).
    /// </summary>
    [Fact]
    public void Seeking_an_elementary_stream_decodes_forward_from_its_start()
    {
        var env = Media.Require();
        var path = env.Clip("raw-h264");
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { KeepDecodedPlanes = true, Kernels = env.Kernels });
        var software = Frames.Software(env, path, source.StreamInfo);
        source.Start();
        for (var i = 0; i < 20; i++)
        {
            Assert.True(Frames.Next(source, out var frame));
            source.Release(frame.FrameId);
        }

        source.Seek((long)(TargetS * 1e9));
        Assert.True(Frames.Next(source, out var first));
        Assert.True(source.TryGetFrameInfo(first.FrameId, out var info));
        Assert.Equal(SourceFrameMarks.Discontinuity, first.Marks);
        Assert.Equal(FileFrameEvents.Seek, info.Events);
        Assert.Equal(520_000_000, info.MediaTimeNs);
        Assert.True(Frames.Planes(env, source, first).Y.AsSpan().SequenceEqual(software[13].Y), "the first frame after the seek is not frame 13");
        source.Release(first.FrameId);
        Assert.Equal(1, source.Statistics.SeeksFromStart);
        Assert.Null(source.Failure);
    }

    /// <summary>
    /// MPEG-TS seeks: FFmpeg lands on whatever packet is near the target's DTS, keyframe or not, so a seek to 1.0 s in a
    /// file with one keyframe (at the start) used to deliver nothing at all, and in a file with a keyframe every second it
    /// lost the frames up to the next one. The seek now goes back until the first packet is a keyframe at or before the
    /// target (here: to the start, by byte), and the first frame shown is the one at 1.0 s.
    /// </summary>
    [Theory]
    [InlineData("ts-onekey")]
    [InlineData("ts-2s")]
    public void A_seek_in_mpeg_ts_lands_on_the_target_frame(string clip)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var times = env.FrameTimes(path);
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { KeepDecodedPlanes = true, Kernels = env.Kernels });
        var software = Frames.Software(env, path, source.StreamInfo);
        source.Start();
        Assert.True(Frames.Next(source, out var frame));
        source.Release(frame.FrameId);
        foreach (var (targetS, index) in new[] { (1.0, 30), (0.3, 9), (1.9, 57) })
        {
            source.Seek((long)(targetS * 1e9));
            Assert.True(Frames.Next(source, out var first), $"nothing after the seek to {targetS} s");
            Assert.True(source.TryGetFrameInfo(first.FrameId, out var info));
            Assert.Equal(SourceFrameMarks.Discontinuity, first.Marks);
            Assert.True(Math.Abs(info.MediaTimeNs - (long)Math.Round((times[index] - times[0]) * 1e9)) <= 1_000, $"seek to {targetS} s: media {info.MediaTimeNs} ns");
            Assert.True(Frames.Planes(env, source, first).Y.AsSpan().SequenceEqual(software[index].Y), $"the first frame after the seek to {targetS} s is not frame {index}");
            source.Release(first.FrameId);
        }

        Assert.Null(source.Failure);
    }

    /// <summary>
    /// A file that cannot be seeked at all (read from a named pipe): a seek is dropped and the next frame is marked
    /// (<see cref="FileFrameEvents.SeekFailed"/>), decoding goes on, and the loop at the end cannot be made, so the stream
    /// ends as if played once; both are counted and <see cref="FileFrameSource.SeekError"/> says why. The decode thread
    /// never fails.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_seeked_goes_on_or_ends_instead_of_failing()
    {
        var env = Media.Require();
        var mkfifo = OperatingSystem.IsLinux() ? Cuda.Tests.Support.Tools.Which("mkfifo") : null;
        if (mkfifo is null)
        {
            Assert.Skip("needs mkfifo (Linux)");
        }

        var clip = env.Clip("raw-h264");
        var n = env.FrameTimes(clip).Length;
        var fifo = Path.Combine(Clips.Directory, $"pipe-{Guid.NewGuid():N}.h264");
        var (made, output) = Cuda.Tests.Support.Tools.Run(mkfifo, [fifo]);
        Assert.True(made == 0, output);
        using var writer = Process.Start(new ProcessStartInfo("sh", ["-c", $"cat '{clip}' > '{fifo}'"]) { UseShellExecute = false })!;
        try
        {
            using var source = new FileFrameSource(env.Context, fifo, new FileSourceOptions { Loop = true, Kernels = env.Kernels });
            source.Start();
            var delivered = 0;
            var failedSeekMarked = false;
            while (Frames.Next(source, out var frame))
            {
                Assert.True(source.TryGetFrameInfo(frame.FrameId, out var info));
                failedSeekMarked |= (info.Events & FileFrameEvents.SeekFailed) != 0 && (frame.Marks & SourceFrameMarks.Discontinuity) != 0;
                source.Release(frame.FrameId);
                if (++delivered == 5)
                {
                    source.Seek(0);
                }
            }

            Assert.Null(source.Failure);
            Assert.True(source.IsEndOfStream);
            Assert.True(failedSeekMarked, "the frame after the failed seek is not marked");
            Assert.InRange(delivered, 6, n);
            var stats = source.Statistics;
            Assert.Equal(2, stats.FailedSeeks); // the seek, and the loop at the end
            Assert.Equal(0, stats.Loops);
            Assert.Contains("cannot seek", source.SeekError, StringComparison.Ordinal);
            Log.WriteLine($"pipe: {delivered} of {n} frames; {source.SeekError}");
        }
        finally
        {
            writer.WaitForExit(5000);
            System.IO.File.Delete(fifo);
        }
    }
}
