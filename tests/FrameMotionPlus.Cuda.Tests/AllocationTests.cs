using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// Allocation measurements run on their own, after every parallel test, so nothing else in the process allocates
/// (and triggers collections) while they read the thread's allocation counter. With non-concurrent GC in this test
/// project (see <see cref="GcConfigurationTests"/>) <see cref="GC.GetAllocatedBytesForCurrentThread"/> is exact.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementDefinition
{
    public const string Name = "Allocation measurement (serial)";
}

/// <summary>Hot-path rule: per-frame calls allocate nothing. Each test warms the path up, then measures several windows.</summary>
[Collection(AllocationMeasurementDefinition.Name)]
[Trait("Category", "Gpu")]
public class AllocationTests
{
    private const int Windows = 3;
    private const int CallsPerWindow = 500;
    private const int Width = 640;
    private const int Height = 360;

    private static long Measure(Action window)
    {
        window();
        long total = 0;
        for (var k = 0; k < Windows; k++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            window();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }

    public static TheoryData<string> Generators => ["blend", "repeat"];

    [Theory]
    [MemberData(nameof(Generators))]
    public void Generator_enqueue_with_dependencies_allocates_nothing(string kind)
    {
        var env = Gpu.Require();
        using var producer = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var b = env.Image(Width, Height, PixelFormat.Rgba8);
        using var o = env.Image(Width, Height, PixelFormat.Rgba8);
        using var prevWritten = CudaEvent.Create(env.Context);
        using var currWritten = CudaEvent.Create(env.Context);
        using var released = CudaEvent.Create(env.Context);
        using var done = CudaEvent.Create(env.Context);
        using CudaFrameGenerator generator = kind == "blend" ? new BlendGenerator(env.Context, env.Kernels) : new RepeatGenerator(env.Context);
        IFrameGenerator asInterface = generator;
        asInterface.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });

        // Distinct events for prev and curr, so the separate curr.Ready wait runs too.
        var prev = a.AsFrame(ready: prevWritten.Signal);
        var curr = b.AsFrame(ready: currWritten.Signal);
        var output = o.AsFrame(ready: released.Signal);
        Assert.NotEqual(prev.Ready, curr.Ready);
        var signal = done.Signal;
        long times = 0;
        var stream = generator.Stream;
        var (waitsBefore, signalsBefore) = (stream.EventWaits, stream.EventSignals);
        long clock = 0;

        var allocated = Measure(() =>
        {
            for (var i = 0; i < CallsPerWindow; i++)
            {
                prevWritten.Record(producer);
                currWritten.Record(producer);
                released.Record(producer);
                // Alternate the buffers and move the pair on in time, so a discontinuity (which covers every call whose
                // pair spans it) ends at the next call and the generator's own work keeps running in the measured windows.
                var swap = (i & 1) == 1;
                var time = clock++;
                asInterface.Enqueue((swap ? curr : prev) with { ContentTimeNs = time }, (swap ? prev : curr) with { ContentTimeNs = time + 1 }, (i % 7) / 7f, output, signal);
                if (i % 50 == 0)
                {
                    asInterface.NotifyDiscontinuity(time + 2);
                }

                while (generator.TryReadGpuTime(out _))
                {
                    times++;
                }
            }

            generator.Stream.Synchronize();
            while (generator.TryReadGpuTime(out _))
            {
                times++;
            }
        });

        Assert.True(times > 0);
        Assert.Equal(0, allocated);

        // Every per-frame wait and signal path ran in every call: prev, curr and output waits, the done signal.
        // (Timeline-semaphore waits and signals need a Vulkan exporter; FrameMotionPlus.Gpu.Tests covers those.)
        var calls = (Windows + 1) * CallsPerWindow;
        Assert.Equal(3L * calls, stream.EventWaits - waitsBefore);
        Assert.Equal(calls, stream.EventSignals - signalsBefore);
    }

    [Fact]
    public void Kernels_copies_signals_and_timers_allocate_nothing()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var other = CudaStream.Create(env.Context, CudaStreamPriority.High);
        using var a = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var b = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var c = env.Image(Width, Height, PixelFormat.Rgba8);
        using var e = CudaEvent.Create(env.Context);
        using var ring = CudaSpanTimerRing.Create(env.Context, 8);
        using var timer = CudaSpanTimer.Create(env.Context);
        var fa = a.AsFrame();
        var fb = b.AsFrame();
        var fc = c.AsFrame();
        var signal = e.Signal;

        var allocated = Measure(() =>
        {
            for (var i = 0; i < CallsPerWindow; i++)
            {
                timer.Begin(stream);
                ring.Begin(stream);
                env.Kernels.EnqueueTestPattern(stream, fa, (uint)i);
                env.Kernels.EnqueueBlend(stream, fa, fb, 0.5f, fb);
                env.Kernels.EnqueueConvert(stream, fb, fc);
                stream.CopyFrameAsync(fa, fb);
                stream.FillFrameAsync(fc, 0);
                ring.End(stream);
                timer.End(stream);
                stream.Signal(signal);
                other.Wait(signal);
                other.Wait(e);
                e.Record(other);
                _ = e.IsComplete;
                _ = timer.TryGetElapsedNanoseconds(out _);
                _ = ring.TryRead(out _);
                env.Context.EnsureCurrent();
            }

            other.Synchronize();
            stream.Synchronize();
        });

        Assert.Equal(0, allocated);
    }
}
