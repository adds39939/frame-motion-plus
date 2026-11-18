using System.Diagnostics;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// Per captured frame, nothing is allocated: on the capture thread (dequeue, upload, cadence, conversion, publishing: the
/// source measures it itself, frame to frame, after its first frames) and on the consumer path (TryRead, Release). Each
/// measurement is shown to catch an injected 64-byte allocation. Unique frames (vivid's pattern moving) and repeats
/// (nothing moving) both run.
/// </summary>
[Trait(Category.Name, Category.Capture)]
[SupportedOSPlatform("linux")]
public class AllocationTests
{
    [Theory]
    [InlineData(VividControls.FastRight, false)]
    [InlineData(VividControls.NoMovement, false)]
    [InlineData(VividControls.FastRight, true)]
    public void Nothing_is_allocated_per_frame_on_the_capture_thread_or_the_consumer_path(int movement, bool inject)
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, movement).Set(VividControls.OsdTextMode, VividControls.OsdNone);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 320, 180, 60, CapturePixelFormat.Nv12));
        if (inject)
        {
            source.PerFrameHook = () => GC.KeepAlive(new byte[40]);
        }

        source.Start();
        var consumer = Consume(source, TimeSpan.FromSeconds(2.5), inject);
        source.Stop();
        var s = source.Statistics;
        Log.WriteLine($"movement {movement}, inject {inject}: {s.Captured} captured, {s.Unique} unique, {s.Repeats} repeats; capture thread {s.DequeueThreadAllocatedBytes} B, consumer {consumer} B");
        Assert.True(s.Captured > 100, $"only {s.Captured} frames captured");
        if (movement == VividControls.NoMovement)
        {
            Assert.True(s.Repeats > 100, "the repeat path did not run");
        }
        else
        {
            Assert.True(s.Published > 100, "the publish path did not run");
        }

        if (inject)
        {
            Assert.True(s.DequeueThreadAllocatedBytes >= 64 * (s.Captured - 32), $"the injected allocation was not seen on the capture thread ({s.DequeueThreadAllocatedBytes} B)");
            Assert.True(consumer > 0, "the injected allocation was not seen on the consumer path");
        }
        else
        {
            Assert.Equal(0, s.DequeueThreadAllocatedBytes);
            Assert.Equal(0, consumer);
        }
    }

    /// <summary>Reads and releases frames for <paramref name="duration"/>; the bytes this thread allocated after its first
    /// 30 frames (with <paramref name="inject"/>, 64 more per frame).</summary>
    private static long Consume(V4l2FrameSource source, TimeSpan duration, bool inject)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var frames = 0;
        var start = -1L;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (source.TryRead(out var f))
            {
                source.Release(f.FrameId, GpuSignal.None);
                frames++;
                if (inject && start >= 0)
                {
                    GC.KeepAlive(new byte[40]);
                }

                if (frames == 30)
                {
                    start = GC.GetAllocatedBytesForCurrentThread();
                }
            }

            Thread.SpinWait(100);
        }

        Assert.True(source.Failure is null, source.Failure?.Message);
        return start < 0 ? 0 : GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
