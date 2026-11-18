using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The Phase 5 review's regressions (adopted from its experiments): a new picture the source dropped for lack of room
/// became the cadence reference, so a picture that then stayed still was never delivered and the display kept an older
/// one. Both failed before the fix (the reference now advances only with a published frame).
/// </summary>
[Trait(Category.Name, Category.Capture)]
[SupportedOSPlatform("linux")]
public class ReviewRegressionTests
{
    /// <summary>
    /// The consumer falls behind (holds everything) while the picture moves, the picture then stops, the consumer catches
    /// up: the last frame it gets must be the picture now on the input, not one from before the stall.
    /// </summary>
    [Fact]
    public void A_picture_that_goes_still_while_the_consumer_is_behind_is_still_delivered()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.OsdTextMode, VividControls.OsdNone).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        using var source = new V4l2FrameSource(context, Capture.Options(path, 640, 360, 60, CapturePixelFormat.Nv12) with { QueueDepth = 2, HistoryDepth = 4 });
        byte[]? lastRaw = null;
        source.RawFrameHook = (timestamp, host, length) =>
        {
            var bytes = new byte[length];
            Marshal.Copy(host, bytes, 0, (int)length);
            Volatile.Write(ref lastRaw, bytes);
        };
        source.Start();
        for (var i = 0; i < 20; i++)
        {
            var f = Capture.Next(source); // past the start-up priming
            source.Release(f.FrameId, GpuSignal.None);
        }

        var held = new List<SourceFrame>();
        for (var i = 0; i < 4; i++)
        {
            held.Add(Capture.Next(source));
        }

        Capture.WaitUntil(() => source.Statistics.DroppedBehind > 10, 5, "frames dropped behind");
        controls.Set(VividControls.HorizontalMovement, VividControls.NoMovement); // the picture stops while the consumer is behind
        Thread.Sleep(500);
        var during = source.Statistics;
        foreach (var f in held)
        {
            source.Release(f.FrameId, GpuSignal.None);
        }

        uint[]? last = null;
        var got = 0;
        var until = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            if (source.TryRead(out var f))
            {
                last = Capture.Read(context, f);
                source.Release(f.FrameId, GpuSignal.None);
                got++;
            }

            Thread.Sleep(1);
        }

        source.Stop();
        var s = source.Statistics;
        var expected = Reference(source.Format, source.Color, Volatile.Read(ref lastRaw)!);
        var differing = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            differing += expected[i] != last![i] ? 1 : 0;
        }

        Log.WriteLine($"dropped behind {s.DroppedBehind} (at the stop of movement + 0.5 s: {during.DroppedBehind}), unique {s.Unique}, repeats {s.Repeats}, published {s.Published}; frames read after catching up {got}; " +
            $"the last frame delivered differs from the picture on the input in {differing} of {expected.Length} pixels");
        Assert.Equal(0, differing);
    }



    /// <summary>End to end, default generator (VFG): the GPU is busy for 2.5 s mid-run, the picture stops moving during
    /// that time, the pipeline recovers. Was the picture now on the input ever published?</summary>
    [Fact]
    public void Pipeline_stall_then_still_picture_end_to_end()
    {
        var path = CaptureHardware.RequireVivid();
        var cuda = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.OsdTextMode, VividControls.OsdNone).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        var fault = FrameMotionPlus.Pipeline.PlaybackFaults.Parse("gpu-busy@300:2.5")!;
        var hooks = new FrameMotionPlus.Pipeline.PlaybackHooks { WrapGenerator = (h, s, k) => fault(h, s, k) };
        var options = new FrameMotionPlus.Pipeline.PlaybackOptions
        {
            Path = path,
            Capture = new CaptureSourceOptions { Device = path, Request = new CaptureRequest { Width = 640, Height = 360, Fps = 60, Format = CapturePixelFormat.Nv12 }, FrameLogCapacity = 4096 },
            Generator = FrameMotionPlus.Pipeline.GeneratorKind.Vfg,
            Display = FrameMotionPlus.Pipeline.DisplayKind.Offscreen,
            Clock = FrameMotionPlus.Pipeline.PlaybackClock.Real,
            OffscreenRate = FrameMotionPlus.Core.Time.FrameRate.FromHz(120),
            DurationNs = 12_000_000_000L,
            WarmupNs = 1_000_000_000L,
        };
        using var session = new FrameMotionPlus.Pipeline.PlaybackSession(options, cuda, hooks);
        session.Open();
        var source = ((FrameMotionPlus.Pipeline.CapturePlaybackSource)session.PlaybackSource!).Source;
        var stoppedAt = 0L;
        var watcher = new Thread(() =>
        {
            var until = Stopwatch.GetTimestamp() + (11 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
            {
                if (source.Statistics.DroppedBehind >= 5)
                {
                    controls.Set(VividControls.HorizontalMovement, VividControls.NoMovement);
                    Volatile.Write(ref stoppedAt, Platform.Time.Monotonic.NowNs);
                    return;
                }

                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        watcher.Start();
        var report = session.Run();
        watcher.Join();
        var log = source.FrameLog.ToArray();
        var c = report.Capture!.Counters;
        var lastUnique = log.Last(r => (r.Flags & CaptureFrameEvents.Unique) != 0);
        var lastPublished = log.Last(r => (r.Flags & CaptureFrameEvents.Published) != 0);
        var end = log[^1];
        var repeatsAfter = log.Count(r => r.TimestampNs > lastUnique.TimestampNs && (r.Flags & CaptureFrameEvents.Unique) == 0);
        Log.WriteLine($"stop {report.StopReason}; failure {report.Failure?.Message}; captured {c.Captured}, unique {c.Unique}, repeats {c.Repeats}, dropped behind {c.DroppedBehind}, published {c.Published}; movement stopped at {Volatile.Read(ref stoppedAt)}");
        Log.WriteLine($"last new picture: timestamp {lastUnique.TimestampNs}, flags {lastUnique.Flags}; last published: timestamp {lastPublished.TimestampNs} ({(lastUnique.TimestampNs - lastPublished.TimestampNs) / 1e6:0.0} ms older), flags {lastPublished.Flags}; " +
            $"{repeatsAfter} repeats over {(end.TimestampNs - lastUnique.TimestampNs) / 1e9:0.00} s followed, none published");
        Assert.True(Volatile.Read(ref stoppedAt) != 0, "the stall never made the source drop frames");
        Assert.True((lastUnique.Flags & CaptureFrameEvents.Published) != 0, "the picture on the input was never published: the display holds an older one");
    }

    private static uint[] Reference(NegotiatedFormat f, CaptureColor color, byte[] bytes) => VividCaptureTests.Reference(f, color, bytes);
}
