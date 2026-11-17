using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// Hot-path rule: <see cref="VfgGenerator.Enqueue"/> allocates nothing. The path is warmed up, then measured in several
/// windows with the thread's exact allocation counter (concurrent GC is off in this project); every branch of the run
/// path is counted inside the measured windows (VFG runs with the output rebound each call, the inputs swapped, varied
/// t, shot-change runs, t = 0/1 copies, the three waits and the signal, GPU-time reads), so the test cannot pass by
/// doing nothing. Both settings of automatic shot-change detection (the blocking and the enqueue-only run).
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class AllocationTests
{
    private const int Windows = 3;
    private const int CallsPerWindow = 120;
    private const int Width = 640;
    private const int Height = 360;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enqueue_allocates_nothing(bool shotChangeDetection)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var producer = CudaStream.Create(env.Context);
        using var a = env.Pattern(Width, Height, format, 10);
        using var b = env.Pattern(Width, Height, format, 12);
        var outputs = Enumerable.Range(0, 4).Select(_ => env.Image(Width, Height, format)).ToArray();
        try
        {
            using var prevWritten = CudaEvent.Create(env.Context);
            using var currWritten = CudaEvent.Create(env.Context);
            using var released = CudaEvent.Create(env.Context);
            using var done = CudaEvent.Create(env.Context);
            using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = shotChangeDetection });
            IFrameGenerator asInterface = generator;
            var frameA = a.AsFrame(ready: prevWritten.Signal);
            var frameB = b.AsFrame(ready: currWritten.Signal);
            var targets = outputs.Select(o => o.AsFrame(ready: released.Signal)).ToArray();
            var signal = done.Signal;
            var stream = generator.Stream;
            long times = 0;
            long clock = 0;

            void Window()
            {
                for (var i = 0; i < CallsPerWindow; i++)
                {
                    prevWritten.Record(producer);
                    currWritten.Record(producer);
                    released.Record(producer);
                    var swap = (i & 1) == 1;
                    var t = (i % 10) switch
                    {
                        0 => 0f,
                        9 => 1f,
                        _ => (i % 10) / 10f,
                    };
                    // The pair moves on in time with every call; a cut at its curr makes this call's pair discontinuous
                    // and the next one, which starts at the cut, ends it.
                    var time = clock++;
                    if (i % 17 == 0)
                    {
                        asInterface.NotifyDiscontinuity(time + 1);
                    }

                    asInterface.Enqueue((swap ? frameB : frameA) with { ContentTimeNs = time }, (swap ? frameA : frameB) with { ContentTimeNs = time + 1 }, t, targets[i % targets.Length], signal);
                    while (generator.TryReadGpuTime(out _))
                    {
                        times++;
                    }

                    if (i % 8 == 7)
                    {
                        // Keep the timer ring from overflowing with detection off (Enqueue does not wait then).
                        done.Synchronize();
                    }
                }

                done.Synchronize();
                while (generator.TryReadGpuTime(out _))
                {
                    times++;
                }
            }

            Window(); // warm-up: first use of every path
            var (runs, shots, copies) = (generator.Runs, generator.ShotChangeRuns, generator.RealFrameCopies);
            var (waits, signals) = (stream.EventWaits, stream.EventSignals);
            var timesBefore = times;
            long allocated = 0;
            for (var w = 0; w < Windows; w++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                Window();
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            var calls = Windows * CallsPerWindow;
            Log.WriteLine($"detection {(shotChangeDetection ? "on" : "off")}: {calls} calls, {generator.Runs - runs} VFG runs " +
                          $"({generator.ShotChangeRuns - shots} shot changes), {generator.RealFrameCopies - copies} copies, " +
                          $"{times - timesBefore} GPU times, {allocated} B allocated");
            Assert.Equal(0, allocated);

            // Every branch ran in the measured windows: VFG runs (with and without the shot-change flag), t = 0/1 copies,
            // three waits and one signal per call, and the GPU times came back.
            Assert.Equal(Windows * CallsPerWindow * 8 / 10, generator.Runs - runs);
            Assert.True(generator.ShotChangeRuns - shots >= Windows * 4, "shot-change runs were not measured");
            Assert.Equal(Windows * CallsPerWindow * 2 / 10, generator.RealFrameCopies - copies);
            Assert.Equal(3L * calls, stream.EventWaits - waits);
            Assert.Equal(calls, stream.EventSignals - signals);
            Assert.Equal(calls, times - timesBefore);
            Assert.Equal(0, generator.DroppedGpuTimes);
        }
        finally
        {
            foreach (var o in outputs)
            {
                o.Dispose();
            }
        }
    }
}
