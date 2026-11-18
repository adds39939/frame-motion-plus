using System.Diagnostics;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The Elgato 4K X with whatever the console sends (a PS5 here): each target mode negotiates as asked, frames arrive at the
/// signal's rate on CLOCK_MONOTONIC with the driver dropping nothing, they are not black and new pictures are detected, and
/// the capture path's costs are within bounds. Skips without the card, and without a picture on it (a console asleep
/// sends black, which is reported, not failed).
/// </summary>
[Trait(Category.Name, Category.Elgato)]
[SupportedOSPlatform("linux")]
public class ElgatoTests
{
    [Theory]
    [InlineData(3840, 2160, 30, CapturePixelFormat.P010)] // 4k30-hdr: the owner's target mode
    [InlineData(2560, 1440, 60, CapturePixelFormat.P010)] // 1440p60-hdr
    [InlineData(3840, 2160, 60, CapturePixelFormat.Nv12)] // 4k60-sdr
    public void Each_target_mode_captures_the_console(int width, int height, double fps, CapturePixelFormat format)
    {
        var path = CaptureHardware.RequireElgato();
        var context = CaptureHardware.RequireCuda();
        using var source = new V4l2FrameSource(context, Capture.Options(path, width, height, fps, format));
        Assert.Equal((width, height, format), (source.Format.Width, source.Format.Height, source.Format.Format));
        Assert.Equal(fps, source.Format.Rate.Hz, 3);
        Assert.Empty(source.Format.Changes);
        Assert.Equal(CaptureBufferMode.UserPtrPinned, source.BufferMode); // the card takes CUDA pinned memory as USERPTR
        source.Start();
        var watch = Stopwatch.StartNew();
        var frames = new List<SourceFrame>();
        while (watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (source.TryRead(out var f))
            {
                frames.Add(f);
                source.Release(f.FrameId, GpuSignal.None);
            }

            Assert.True(source.Failure is null, source.Failure?.Message);
            Thread.Sleep(1);
        }

        source.Stop();
        var s = source.Statistics;
        var log = source.FrameLog.ToArray();
        var judged = log.Where(r => r.Metric >= 0).Skip(10).ToList(); // after the card's warm-up (black, then error-flagged frames)
        Log.WriteLine($"{source.Describe()}: {s.Captured} captured, {s.Unique} unique, {s.Repeats} repeats, {s.BlackFrames} black, {s.ErrorFrames} error-flagged, " +
            $"{s.DriverDropped} dropped by the driver, {s.SkippedStale} stale; detected {s.DetectedFps:0.00} fps; intervals mean {source.Intervals.Mean / 1e6:0.000} ms " +
            $"sd {source.Intervals.StdDev / 1e3:0.0} µs; USB delivery p50 {source.DequeueLag.Percentile(0.5) / 1e6:0.00} ms; upload p50 {source.UploadGpu.Percentile(0.5) / 1e6:0.000} ms, " +
            $"cadence {source.CadenceGpu.Percentile(0.5) / 1e6:0.000} ms, convert {source.ConvertGpu.Percentile(0.5) / 1e6:0.000} ms; capture thread {source.ThreadReport}");
        if (judged.Count == 0 || judged.Count(r => (r.Flags & CaptureFrameEvents.Black) != 0) * 2 > judged.Count)
        {
            Assert.Skip($"the card delivers black frames ({s.BlackFrames} of {s.Captured}): no picture on its input (the console asleep or off?)");
        }

        Assert.True(s.Captured >= fps * 3, $"only {s.Captured} frames in 4 s at {fps} fps");
        Assert.Equal(0, s.DriverDropped);
        Assert.Equal(0, s.NonMonotonicTimestamps);
        Assert.InRange(source.Intervals.Percentile(0.5), 0.99e9 / fps, 1.01e9 / fps);
        Assert.InRange(source.DequeueLag.Percentile(0.99), 0, 50_000_000); // USB delivery of a frame: 4 to 26 ms measured
        Assert.True(s.Unique >= 2 && frames.Count >= 2, $"{s.Unique} unique frames: the picture never changed");
        Assert.True(judged.Count(r => (r.Flags & CaptureFrameEvents.Black) == 0) > judged.Count / 2, "most frames are black");
        Assert.All(frames.Zip(frames.Skip(1)), p => Assert.True(p.Second.ContentTimeNs > p.First.ContentTimeNs));
        Assert.Equal(0, s.DequeueThreadAllocatedBytes);
        Assert.InRange(source.UploadGpu.Percentile(0.5), 1, 5_000_000);
    }
}
