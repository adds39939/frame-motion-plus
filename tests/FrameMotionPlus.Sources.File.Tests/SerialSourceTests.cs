using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// Source tests that read process-wide state (the CUDA context's reference count, the decode thread's context switches)
/// or hold the GPU for seconds: alone, after the parallel tests, so other tests' streams, events and threads cannot show
/// in what they measure.
/// </summary>
[Trait(Category.Name, Category.Media)]
[Collection(SerialMediaDefinition.Name)]
public class SerialSourceTests
{
    /// <summary>
    /// FFmpeg's device context holds the application's primary context and works on the source's copy stream; the
    /// convert kernel, launched in that context, reads the decoded picture straight from FFmpeg's frame pool (the
    /// bit-exact RGB of DecodeTests proves the pointer is valid there). Opening, playing and closing a source leaves the
    /// context's reference count as it was (it counts every stream and event in the process: hence serial).
    /// </summary>
    [Fact]
    public void FFmpeg_uses_the_applications_context_and_the_copy_stream()
    {
        var env = Media.Require();
        var refsBefore = env.Context.ReferenceCount;
        using var source = new FileFrameSource(env.Context, env.Clip("h264"), new FileSourceOptions { Kernels = env.Kernels });
        var (context, stream) = source.FFmpegDevice;
        Assert.Equal(env.Context.Handle, context);
        Assert.Equal(source.CopyStream.Handle, stream);
        Assert.NotEqual(source.CopyStream.Handle, source.ComputeStream.Handle);
        source.Start();
        Assert.True(Frames.Next(source, out var frame));
        source.Release(frame.FrameId);
        source.Dispose();
        Assert.Equal(refsBefore, env.Context.ReferenceCount); // FFmpeg retained nothing of ours and released nothing
        Assert.False(env.Context.IsDisposed);
        using var check = CudaStream.Create(env.Context); // the context is still alive and usable
        check.Synchronize();
    }

    /// <summary>
    /// The decode thread sleeps until something changes: held back by a full queue, and at the end of the file, it makes
    /// (almost) no context switches in half a second; it used to wake every millisecond to look (about 500). Measured on
    /// the thread itself (/proc/self/task/&lt;tid&gt;/status).
    /// </summary>
    [Fact]
    public void The_decode_thread_does_not_poll_while_it_waits()
    {
        var env = Media.Require();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("reads /proc");
        }

        using var source = new FileFrameSource(env.Context, env.Clip("h264-3s"), new FileSourceOptions { Kernels = env.Kernels });
        var before = DecodeThreads();
        source.Start();
        Frames.WaitUntil(() => DecodeThreads().Except(before).Any(), "the decode thread did not appear");
        var tid = Assert.Single(DecodeThreads().Except(before));

        Frames.WaitUntil(() => source.QueueDepth == 4, "the queue did not fill");
        Thread.Sleep(50);
        var full = Switches(tid, TimeSpan.FromMilliseconds(500));

        while (Frames.Next(source, out var frame))
        {
            source.Release(frame.FrameId);
        }

        Thread.Sleep(50);
        var ended = Switches(tid, TimeSpan.FromMilliseconds(500));
        Log.WriteLine($"decode thread context switches in 500 ms: {full} with a full queue, {ended} at the end of the file");
        Assert.True(full <= 5, $"{full} context switches in 500 ms with a full queue");
        Assert.True(ended <= 5, $"{ended} context switches in 500 ms at the end of the file");
    }

    /// <summary>
    /// A release point that does not complete for a long time (here a 3 s GPU delay; for ever in a broken consumer) holds
    /// the decode thread inside NVDEC's per-frame synchronisation. <see cref="FileFrameSource.Stop"/> gives up after its
    /// bound with a <see cref="TimeoutException"/> that says why, <see cref="FileFrameSource.Dispose"/> frees nothing and
    /// says so (<see cref="FileFrameSource.Leaked"/>), and once the GPU work has completed a second Dispose frees it all.
    /// </summary>
    [Fact]
    public void A_release_point_that_never_completes_makes_dispose_report_a_leak_instead_of_hiding_it()
    {
        var env = Media.Require();
        using var reader = CudaStream.Create(env.Context);
        using var readDone = CudaEvent.Create(env.Context);
        var source = new FileFrameSource(env.Context, env.Clip("h264-3s"), new FileSourceOptions { DecodeAhead = 1, HistoryDepth = 2, Kernels = env.Kernels })
        {
            StopTimeout = TimeSpan.FromMilliseconds(500),
        };
        try
        {
            source.Start();
            Assert.True(Frames.Next(source, out var frame));
            reader.Wait(frame.Ready);
            env.Kernels.EnqueueDelay(reader, 3_000_000_000);
            readDone.Record(reader);
            source.Release(frame.FrameId, readDone.Signal);

            // Read on until the decode thread is held (no frame for 300 ms).
            var idle = Stopwatch.StartNew();
            while (idle.ElapsedMilliseconds < 300)
            {
                if (source.TryRead(out var next))
                {
                    source.Release(next.FrameId);
                    idle.Restart();
                }

                Thread.Sleep(1);
            }

            var timeout = Assert.Throws<TimeoutException>(source.Stop);
            Assert.Contains("release point", timeout.Message, StringComparison.Ordinal);
            source.Dispose();
            Assert.True(source.Leaked);

            reader.Synchronize(); // the release point completes; the decode thread sees it was told to stop
            source.StopTimeout = TimeSpan.FromSeconds(10);
            source.Dispose();
            Assert.False(source.Leaked);
        }
        finally
        {
            reader.Synchronize();
            source.StopTimeout = TimeSpan.FromSeconds(10);
            source.Dispose();
        }
    }

    /// <summary>
    /// A consumer that destroys its release point's event before the source has rewritten the buffer breaks the rule
    /// (the object behind a GpuSignal must outlive the frame): the decode thread stops with an error that names the frame
    /// and the rule, rather than an anonymous ObjectDisposedException.
    /// </summary>
    [Fact]
    public void A_release_point_destroyed_too_early_stops_the_source_with_a_clear_error()
    {
        var env = Media.Require();
        using var source = new FileFrameSource(env.Context, env.Clip("h264-3s"), new FileSourceOptions { DecodeAhead = 1, HistoryDepth = 2, Kernels = env.Kernels });
        source.Start();
        Assert.True(Frames.Next(source, out var frame));
        using var reader = CudaStream.Create(env.Context);
        using (var gone = CudaEvent.Create(env.Context))
        {
            reader.Wait(frame.Ready);
            gone.Record(reader);
            source.Release(frame.FrameId, gone.Signal);
        }

        Frames.WaitUntil(
            () =>
            {
                if (source.TryRead(out var next))
                {
                    source.Release(next.FrameId);
                }

                return source.Failure is not null;
            },
            "the decode thread did not stop");
        var failure = Assert.IsType<InvalidOperationException>(source.Failure);
        Assert.Contains("release point given for frame 0", failure.Message, StringComparison.Ordinal);
        Assert.IsType<ObjectDisposedException>(failure.InnerException);
    }

    /// <summary>
    /// A seek must not overwrite what a held frame's conversion still reads. Near the end of an HEVC clip the consumer
    /// releases a frame with an event behind a 700 ms GPU delay (so the conversion that reuses its buffer waits 700 ms)
    /// and holds the next three; then it seeks. The seek's drain receives a frame the decoder had queued before it, which
    /// FFmpeg copies into a frame-pool buffer on the copy stream: that copy must wait for the last conversion, as every
    /// other receive does, or it lands in the buffer the delayed conversion reads, and a held frame shows the next one.
    /// Each held frame's RGB must equal the CPU reference of the software decode's frame.
    /// </summary>
    [Fact]
    public void A_seek_never_overwrites_a_buffer_a_held_frame_is_still_converted_from()
    {
        var env = Media.Require();
        var path = env.Clip("hevc10-2s");
        using var reader = CudaStream.Create(env.Context);
        using var delayed = CudaEvent.Create(env.Context);
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { DecodeAhead = 1, HistoryDepth = 2, Kernels = env.Kernels });
        var info = source.StreamInfo;
        var software = Frames.Software(env, path, info);
        var total = software.Length;
        var c = info.Conversion;
        var reference = App.Decode.YuvReference.Create(c.Matrix, c.Range, info.BitDepth, c.Siting, info.OutputFormat);
        source.Start();
        var held = new List<SourceFrame>();
        for (var i = 0; held.Count < 3; i++)
        {
            Assert.True(Frames.Next(source, out var frame));
            if (i < total - 5)
            {
                source.Release(frame.FrameId);
            }
            else if (i == total - 5)
            {
                env.Kernels.EnqueueDelay(reader, 700_000_000);
                delayed.Record(reader);
                source.Release(frame.FrameId, delayed.Signal);
            }
            else
            {
                held.Add(frame);
            }
        }

        Thread.Sleep(100); // the decode thread converts into the released buffer (waiting on the GPU) and queues it
        source.Seek(500_000_000);
        Frames.WaitUntil(() => source.Statistics.Seeks == 1 && source.Statistics.StaleFramesDropped > 0, "the seek did not drop a frame from before it (the scenario needs the decoder draining)", 5);
        foreach (var frame in held)
        {
            var expected = new uint[info.Width * info.Height];
            var (y, cb, cr) = software[frame.FrameId];
            reference.Convert(y, cb, cr, info.Width, info.Height, expected);
            var rgb = Frames.Rgb(env, frame); // waits for its conversion, behind the delay
            Assert.True(expected.AsSpan().SequenceEqual(rgb), $"held frame {frame.FrameId} was overwritten: {Planes.FirstDifference(expected, rgb, info.Width)}");
        }

        foreach (var frame in held)
        {
            source.Release(frame.FrameId);
        }

        reader.Synchronize();
    }

    /// <summary>The ids of this process's threads named fmp-decode.</summary>
    private static int[] DecodeThreads() =>
        [.. Directory.GetDirectories("/proc/self/task")
            .Where(t => ReadOrEmpty(Path.Combine(t, "comm")).Trim() == "fmp-decode")
            .Select(t => int.Parse(Path.GetFileName(t), CultureInfo.InvariantCulture))];

    /// <summary>The thread's voluntary plus involuntary context switches over <paramref name="period"/>.</summary>
    private static long Switches(int tid, TimeSpan period)
    {
        long Count()
        {
            var status = System.IO.File.ReadAllLines($"/proc/self/task/{tid}/status");
            return status.Where(l => l.Contains("ctxt_switches", StringComparison.Ordinal))
                .Sum(l => long.Parse(l.Split(':')[1].Trim(), CultureInfo.InvariantCulture));
        }

        var start = Count();
        Thread.Sleep(period);
        return Count() - start;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
