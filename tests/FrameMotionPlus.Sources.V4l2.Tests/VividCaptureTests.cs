using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.V4l2.Interop;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The capture source against the kernel's vivid test driver (its webcam input): enumeration and negotiation, every
/// buffer mode with the captured frame converted on the GPU bit for bit against the CPU reference, newest-wins when the
/// dequeue thread or the consumer is slow, timestamps, repeated start/stop and open/close without leaks, and the ways a
/// capture ends (unplug, a streaming error) or pauses (no frames), using vivid's error-injection controls. Skips without
/// vivid (it is loaded by hand and goes away on reboot) or a GPU.
/// </summary>
[Trait(Category.Name, Category.Capture)]
[SupportedOSPlatform("linux")]
public class VividCaptureTests
{
    [Fact]
    public void Vivid_is_listed_with_its_formats_sizes_and_rates()
    {
        var path = CaptureHardware.RequireVivid();
        var d = CaptureDevices.Describe(path);
        Assert.True(d.Info.IsVideoCapture);
        Assert.Contains(d.Modes, m => m.Format == CapturePixelFormat.Nv12 && m.Width == 640 && m.Height == 360 && Math.Abs(m.Fps - 30) < 0.01);
        Assert.Contains(d.Modes, m => m.Format == CapturePixelFormat.Yuyv);
        Assert.DoesNotContain(d.Modes, m => m.Format == CapturePixelFormat.P010); // vivid has no P010
        Log.WriteLine($"{path}: {d.Info.Card}, {d.Modes.Count} modes, current {d.Current}, input {d.Input}");
    }

    [Fact]
    public void Negotiation_logs_what_was_set_and_refuses_what_is_not_offered()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using (var source = new V4l2FrameSource(context, Capture.Options(path, 640, 360, 30, CapturePixelFormat.Nv12)))
        {
            Assert.Equal((640, 360, CapturePixelFormat.Nv12), (source.Format.Width, source.Format.Height, source.Format.Format));
            Assert.Equal(30, source.Format.Rate.Hz, 3);
            Assert.True(source.Format.IntervalConfirmed);
            Assert.Empty(source.Format.Changes);
            Assert.Equal(PixelFormat.Rgba8, source.Info.Format);
            Assert.Equal(MatrixCoefficients.Bt601, source.Color.Matrix); // sRGB's default encoding
            Assert.Equal(ColorRange.Limited, source.Color.Range);
            Log.WriteLine(source.Describe());
        }

        var e = Assert.Throws<CaptureFormatException>(() => new V4l2FrameSource(context, Capture.Options(path, 1000, 1000, 30, CapturePixelFormat.Nv12)));
        Assert.Contains("sizes offered", e.Message, StringComparison.Ordinal);
        Assert.Throws<CaptureFormatException>(() => new V4l2FrameSource(context, Capture.Options(path, 0, 0, 0, CapturePixelFormat.P010)));
        Assert.Throws<CaptureUnavailableException>(() => new V4l2FrameSource(context, new CaptureSourceOptions { Device = "/dev/video-none" }));
    }

    /// <summary>
    /// Each buffer mode, NV12 and YUYV: vivid's pattern with nothing moving and no on-screen text (every frame the same),
    /// captured, uploaded and converted; the frame equals the CPU reference of the bytes the driver wrote, and the
    /// cadence detector keeps only the first (the rest repeat it exactly).
    /// </summary>
    [Theory]
    [InlineData(CaptureBufferMode.UserPtrPinned, CapturePixelFormat.Nv12)]
    [InlineData(CaptureBufferMode.UserPtrRegistered, CapturePixelFormat.Nv12)]
    [InlineData(CaptureBufferMode.MmapRegistered, CapturePixelFormat.Nv12)]
    [InlineData(CaptureBufferMode.UserPtrPinned, CapturePixelFormat.Yuyv)]
    [InlineData(CaptureBufferMode.MmapRegistered, CapturePixelFormat.Yuyv)]
    public void Every_buffer_mode_converts_the_pattern_bit_exactly(CaptureBufferMode mode, CapturePixelFormat format)
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.OsdTextMode, VividControls.OsdNone).Set(VividControls.HorizontalMovement, VividControls.NoMovement);
        var options = Capture.Options(path, 640, 360, 30, format) with { BufferMode = mode };
        using var source = new V4l2FrameSource(context, options);
        Assert.Equal(mode, source.BufferMode);
        var raw = new Dictionary<long, byte[]>();
        source.RawFrameHook = (timestamp, host, length) =>
        {
            var bytes = new byte[length];
            Marshal.Copy(host, bytes, 0, (int)length);
            lock (raw)
            {
                raw[timestamp] = bytes;
            }
        };
        source.Start();
        var frame = Capture.Next(source);
        Capture.WaitUntil(() => source.Statistics.Captured >= 8, 10, "eight captures");
        source.Stop();

        byte[] bytes;
        lock (raw)
        {
            bytes = raw[frame.ContentTimeNs];
            Assert.All(raw.Values, b => Assert.True(b.AsSpan().SequenceEqual(bytes), "vivid's frames differ although nothing moves"));
        }

        var actual = Capture.Read(context, frame);
        source.Release(frame.FrameId, GpuSignal.None);
        var expected = Reference(source.Format, source.Color, bytes);
        Assert.True(expected.AsSpan().SequenceEqual(actual), FirstDifference(expected, actual, source.Format.Width));
        Assert.True(actual.Distinct().Count() > 4, "the pattern converted to a flat image");
        var s = source.Statistics;
        Assert.Equal(1, s.Unique); // every later capture repeats the first exactly
        Assert.Equal(s.Captured - 1, s.Repeats);
        Log.WriteLine($"{CaptureBuffers.Name(mode)} {format}: {s.Captured} captured, {s.Unique} unique; {source.Describe()}");
    }

    /// <summary>
    /// Newest wins when the dequeue thread falls behind: with the thread held up for three frame times before each poll,
    /// several buffers are ready at once; the newest is processed and the rest go back unseen, counted as stale, and the
    /// processed sequence numbers jump over them.
    /// </summary>
    [Fact]
    public void A_slow_dequeue_thread_takes_the_newest_buffer_and_requeues_the_rest()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        source.BeforePollHook = () => Thread.Sleep(50);
        source.Start();
        var read = 0;
        var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        while (read < 20 && Stopwatch.GetTimestamp() < deadline)
        {
            if (source.TryRead(out var f))
            {
                source.Release(f.FrameId, GpuSignal.None);
                read++;
            }

            Thread.Sleep(1);
        }

        source.Stop();
        var s = source.Statistics;
        Assert.True(s.SkippedStale > 10, $"only {s.SkippedStale} stale buffers skipped");
        Assert.Equal(s.Dequeued, s.Captured + s.SkippedStale + s.ErrorFrames + s.ShortFrames);
        var log = source.FrameLog.ToArray();
        var processed = log.Where(r => (r.Flags & CaptureFrameEvents.Stale) == 0).ToList();
        Assert.Contains(processed.Zip(processed.Skip(1)), p => p.Second.Sequence > p.First.Sequence + 1);

        // Within each batch the processed buffer is the newest: no stale buffer is newer than the one processed after it.
        for (var i = 1; i < log.Length; i++)
        {
            if ((log[i - 1].Flags & CaptureFrameEvents.Stale) != 0)
            {
                Assert.True(log[i].Sequence > log[i - 1].Sequence, $"buffer {log[i].Sequence} processed after the newer {log[i - 1].Sequence}");
            }
        }

        Log.WriteLine($"{s.Dequeued} dequeued, {s.SkippedStale} stale, {s.Captured} captured, {read} read");
    }

    /// <summary>A consumer that reads nothing: the source never waits for it; new frames are dropped (counted) while its queue
    /// and history are full, capture goes on, and reading again gets frames again.</summary>
    [Fact]
    public void A_consumer_that_stops_reading_never_slows_the_capture()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12) with { QueueDepth = 2, HistoryDepth = 4 });
        source.Start();
        var held = new List<SourceFrame>();
        for (var i = 0; i < 4; i++)
        {
            held.Add(Capture.Next(source)); // the consumer holds as many as its history allows
        }

        var before = source.Statistics;
        Thread.Sleep(1000);
        var during = source.Statistics;
        Assert.True(during.Captured - before.Captured > 40, $"capture slowed down: {during.Captured - before.Captured} frames in 1 s at 60 fps");
        Assert.True(during.DroppedBehind - before.DroppedBehind > 30, $"only {during.DroppedBehind - before.DroppedBehind} frames dropped while the consumer held everything");
        Assert.Equal(0, during.SkippedStale - before.SkippedStale);

        var releasedAt = Platform.Time.Monotonic.NowNs;
        foreach (var f in held)
        {
            source.Release(f.FrameId, GpuSignal.None);
        }

        // The two frames queued while it held everything are old by now; then fresh ones come.
        var a = Capture.Next(source);
        source.Release(a.FrameId, GpuSignal.None);
        var b = Capture.Next(source);
        source.Release(b.FrameId, GpuSignal.None);
        var c = Capture.Next(source);
        source.Release(c.FrameId, GpuSignal.None);
        Assert.True(c.ContentTimeNs > releasedAt - 50_000_000, "no fresh frame after the consumer read again");
        source.Stop();
        Log.WriteLine($"while held: {during.Captured - before.Captured} captured, {during.DroppedBehind - before.DroppedBehind} dropped behind");
    }

    /// <summary>Timestamps are CLOCK_MONOTONIC (by the buffer flags), strictly increasing, on the signal's period, and near
    /// the dequeue time on the same clock; content times are the capture timestamps.</summary>
    [Fact]
    public void Timestamps_are_monotonic_on_clock_monotonic_and_are_the_content_times()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        source.Start();
        var frames = new List<SourceFrame>();
        while (frames.Count < 60)
        {
            var f = Capture.Next(source);
            frames.Add(f);
            source.Release(f.FrameId, GpuSignal.None);
        }

        source.Stop();
        Assert.Equal(0, source.Statistics.NonMonotonicTimestamps);
        var log = source.FrameLog.ToArray().Where(r => (r.Flags & (CaptureFrameEvents.Error | CaptureFrameEvents.Stale)) == 0).ToList();
        Assert.All(log.Zip(log.Skip(1)), p => Assert.True(p.Second.TimestampNs > p.First.TimestampNs));
        Assert.All(log, r => Assert.InRange(r.DequeuedNs - r.TimestampNs, -40_000_000, 40_000_000)); // vivid stamps the end of the frame period
        Assert.InRange(source.Intervals.Mean, 16_000_000, 17_400_000);
        Assert.All(frames.Zip(frames.Skip(1)), p => Assert.True(p.Second.ContentTimeNs > p.First.ContentTimeNs));
        var published = log.Where(r => (r.Flags & CaptureFrameEvents.Published) != 0).Select(r => r.TimestampNs).ToHashSet();
        Assert.All(frames, f => Assert.Contains(f.ContentTimeNs, published));
        Assert.All(frames, f => Assert.True(f.ArrivalTimeNs >= f.ContentTimeNs - 40_000_000));
    }

    /// <summary>
    /// Opening, streaming and closing ten times, and starting and stopping one source five times: descriptors, GPU memory and
    /// threads come back to where they were, and the device can be opened by someone else after every close.
    /// </summary>
    [Fact]
    public void Repeated_start_stop_and_open_close_leak_nothing_and_release_the_device()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        void Cycle(int frames)
        {
            using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
            for (var start = 0; start < 2; start++)
            {
                source.Start();
                for (var i = 0; i < frames; i++)
                {
                    var f = Capture.Next(source);
                    source.Release(f.FrameId, GpuSignal.None);
                }

                source.Stop();
            }
        }

        Cycle(3); // first use: kernels' JIT cache, the thread pool, lazy statics
        var fds = Descriptors();
        var threads = Process.GetCurrentProcess().Threads.Count;
        var gpu = context.MemoryInfo.FreeBytes;
        for (var i = 0; i < 10; i++)
        {
            Cycle(3);

            // Released: another opener can stream at once.
            using var other = V4l2Device.Open(path);
            Assert.True(other.RequestBuffers(2, V4l2Defs.MemoryMmap, out _) == 2, "the device was not released");
            _ = other.RequestBuffers(0, V4l2Defs.MemoryMmap, out _);
        }

        using (var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12)))
        {
            for (var i = 0; i < 5; i++)
            {
                source.Start();
                var f = Capture.Next(source);
                source.Release(f.FrameId, GpuSignal.None);
                source.Stop();
            }
        }

        Assert.Equal(fds, Descriptors());
        Assert.InRange(Process.GetCurrentProcess().Threads.Count, threads - 2, threads + 2);
        Assert.InRange(context.MemoryInfo.FreeBytes, gpu - (16L << 20), long.MaxValue); // within 16 MiB (other processes share the GPU)
    }

    /// <summary>
    /// vivid's "disconnect" (a simulated USB unplug) while streaming: the capture ends, saying the device went away, within
    /// a second, without hanging; the source can still be disposed; vivid reconnects once the last descriptor is closed.
    /// </summary>
    [Fact]
    public void An_unplugged_device_ends_the_capture_without_hanging()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        try
        {
            source.Start();
            var f = Capture.Next(source);
            source.Release(f.FrameId, GpuSignal.None);
            using (var other = V4l2Device.Open(path))
            {
                Assert.Equal(0, VividControls.Press(other, VividControls.Disconnect));
            }

            Capture.WaitUntil(() => source.Failure is not null, 3, "the capture's end after the unplug");
            var failure = Assert.IsType<CaptureEndedException>(source.Failure);
            Assert.Contains(failure.Reason, new[] { CaptureEndReason.DeviceLost, CaptureEndReason.StreamError });
            Assert.Equal(CaptureSignalState.Ended, source.State);
            Log.WriteLine($"{failure.Reason}: {failure.Message}");
            var stop = Stopwatch.StartNew();
            source.Dispose();
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(3), $"Dispose took {stop.Elapsed}");
        }
        finally
        {
            source.Dispose();
        }

        // vivid comes back when the last user is gone.
        Capture.WaitUntil(() => CaptureDevices.List().Any(d => d.Info.Path == path), 5, "vivid's reconnect");
    }

    /// <summary>
    /// No frames (vivid dropping every buffer): after a second the source reports no signal and the pipeline would hold the
    /// last frame; frames resume by themselves when the signal does, the first one after the gap marked as a
    /// discontinuity; nothing ends.
    /// </summary>
    [Fact]
    public void A_signal_that_stops_is_reported_and_resumes_with_a_discontinuity()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        source.Start();
        for (var i = 0; i < 5; i++)
        {
            var f = Capture.Next(source);
            source.Release(f.FrameId, GpuSignal.None);
        }

        controls.Set(VividControls.PercentageDroppedBuffers, 100);
        Capture.WaitUntil(() => source.State == CaptureSignalState.NoSignal, 5, "the no-signal state");
        while (source.TryRead(out var stale))
        {
            source.Release(stale.FrameId, GpuSignal.None);
        }

        Assert.Null(source.Failure);
        Assert.Equal(1, source.Statistics.NoSignalEpisodes);
        controls.Set(VividControls.PercentageDroppedBuffers, 0);
        var resumed = Capture.Next(source, 5);
        source.Release(resumed.FrameId, GpuSignal.None);
        Assert.True((resumed.Marks & SourceFrameMarks.Discontinuity) != 0, "the first frame after the gap is not a discontinuity");
        Capture.WaitUntil(() => source.State == CaptureSignalState.Streaming, 2, "streaming again");
        var next = Capture.Next(source);
        source.Release(next.FrameId, GpuSignal.None);
        Assert.Equal(SourceFrameMarks.None, next.Marks);
        source.Stop();
    }

    /// <summary>vivid's injected fatal streaming error: the capture ends as a stream error, at once, and disposes cleanly;
    /// an error-flagged buffer is skipped and counted without ending anything.</summary>
    [Fact]
    public void Streaming_errors_end_the_capture_and_error_flagged_buffers_are_skipped()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        source.Start();
        var f = Capture.Next(source);
        source.Release(f.FrameId, GpuSignal.None);
        using (var other = V4l2Device.Open(path))
        {
            Assert.Equal(0, VividControls.Press(other, VividControls.InjectBufFlagError));
        }

        Capture.WaitUntil(() => source.Statistics.ErrorFrames > 0, 3, "an error-flagged buffer");
        Assert.Null(source.Failure);
        using (var other = V4l2Device.Open(path))
        {
            Assert.Equal(0, VividControls.Press(other, VividControls.InjectFatalStreamingError));
        }

        Capture.WaitUntil(() => source.Failure is not null, 3, "the capture's end after a fatal streaming error");
        var failure = Assert.IsType<CaptureEndedException>(source.Failure);
        Assert.Equal(CaptureEndReason.StreamError, failure.Reason);
        Log.WriteLine(failure.Message);
    }

    private static int Descriptors() => Directory.GetFiles("/proc/self/fd").Length;

    /// <summary>The CPU reference of a captured frame's conversion (NV12 through 4:2:0, YUYV through 4:2:2).</summary>
    internal static uint[] Reference(NegotiatedFormat f, CaptureColor color, byte[] bytes)
    {
        int w = f.Width, h = f.Height, bpl = f.BytesPerLine;
        var output = f.OutputFormat;
        if (f.Format == CapturePixelFormat.Yuyv)
        {
            // 4:2:2 as a 4:2:0 image twice as tall whose chroma rows are the 4:2:2 rows, sited on the even rows (top-left
            // vertically): the even output rows are then exactly the 4:2:2 conversion.
            var cw = w / 2;
            var y2 = new ushort[w * h * 2];
            var cb = new ushort[cw * h];
            var cr = new ushort[cw * h];
            for (var r = 0; r < h; r++)
            {
                for (var x = 0; x < w; x++)
                {
                    y2[(2 * r * w) + x] = bytes[(r * bpl) + (2 * x)];
                    y2[((2 * r) + 1) * w + x] = bytes[(r * bpl) + (2 * x)];
                }

                for (var i = 0; i < cw; i++)
                {
                    cb[(r * cw) + i] = bytes[(r * bpl) + (4 * i) + 1];
                    cr[(r * cw) + i] = bytes[(r * bpl) + (4 * i) + 3];
                }
            }

            var siting = color.Siting switch
            {
                ChromaSiting.Center or ChromaSiting.Top or ChromaSiting.Bottom => ChromaSiting.Top,
                _ => ChromaSiting.TopLeft,
            };
            var tall = new uint[w * h * 2];
            YuvReference.Create(color.Matrix, color.Range, 8, siting, output).Convert(y2, cb, cr, w, h * 2, tall);
            var result = new uint[w * h];
            for (var r = 0; r < h; r++)
            {
                Array.Copy(tall, 2 * r * w, result, r * w, w);
            }

            return result;
        }

        var y = new ushort[w * h];
        var cbs = new ushort[(w / 2) * (h / 2)];
        var crs = new ushort[(w / 2) * (h / 2)];
        for (var r = 0; r < h; r++)
        {
            for (var x = 0; x < w; x++)
            {
                y[(r * w) + x] = bytes[(r * bpl) + x];
            }
        }

        var chroma = bpl * h;
        for (var r = 0; r < h / 2; r++)
        {
            for (var i = 0; i < w / 2; i++)
            {
                cbs[(r * (w / 2)) + i] = bytes[chroma + (r * bpl) + (2 * i)];
                crs[(r * (w / 2)) + i] = bytes[chroma + (r * bpl) + (2 * i) + 1];
            }
        }

        var expected = new uint[w * h];
        YuvReference.Create(color.Matrix, color.Range, 8, color.Siting, output).Convert(y, cbs, crs, w, h, expected);
        return expected;
    }

    private static string FirstDifference(uint[] expected, uint[] actual, int width)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"first difference at ({i % width},{i / width}): expected 0x{expected[i]:x8}, got 0x{actual[i]:x8}";
            }
        }

        return "equal";
    }
}
