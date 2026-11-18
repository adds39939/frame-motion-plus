using System.Collections.Concurrent;
using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// Faults and stalls on the real clock (offscreen): a generator that throws, a GPU too slow for the slots, a presenter or
/// a generator thread stalled for 0.5 s and 3 s, the end of the file, many sessions in a row, a teardown with writes in
/// flight. Playback degrades (real frames, skipped slots, counted) and recovers; nothing stalls, hangs or leaks.
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class FaultTests
{
    private static PlaybackOptions Realtime(string path, double seconds, double rateHz = 120) => new()
    {
        Path = path,
        Generator = GeneratorKind.Blend,
        Multiplier = 2,
        Display = DisplayKind.Offscreen,
        Clock = PlaybackClock.Real,
        OffscreenRate = Core.Time.FrameRate.FromDouble(rateHz),
        DurationNs = (long)(seconds * 1e9),
        WarmupNs = 500_000_000,
        Loop = true,
    };

    /// <summary>
    /// A generator that throws mid-run, after queueing its GPU-side waits (Core's failure contract: nothing signalled,
    /// output undefined): the slot gets the real frame instead, generation stays off, playback carries on with real
    /// frames, and the run reports the fault.
    /// </summary>
    [Fact]
    public void A_throwing_generator_falls_back_to_real_frames_and_playback_continues()
    {
        var cuda = Hardware.RequireGpu();
        var hooks = new PlaybackHooks { GeneratorFactory = (c, k, s) => Faulty.Wrap(new BlendGenerator(c, k, s), throwAt: 40) };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 3), hooks);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.Equal(1, report.GeneratorFaults);
        Assert.True(report.GeneratorFaulted);
        Assert.Contains("injected", report.FirstGeneratorFault, StringComparison.Ordinal);
        Assert.True(report.Fallbacks > 50, $"only {report.Fallbacks} generated slots fell back to real frames");
        Assert.Equal(39, report.GeneratedWrites);

        // It kept presenting through and after the fault: about one present per refresh over the whole run.
        Assert.True(report.Statistics.Presents >= report.Statistics.MeasuredSeconds * 120 * 0.97, $"{report.Statistics.Presents} presents in {report.Statistics.MeasuredSeconds:0.00} s");
        Assert.Equal(0, report.Statistics.PipelineMisses);
    }

    /// <summary>
    /// A GPU too slow for the slots (every generation 40 ms behind a delay kernel for a while): writes pile up on the
    /// generator stream until the bridge refuses slots; the pacing thread skips them (counted) instead of queueing, the
    /// lead never passes the bridge's limit, nothing stalls, and once the GPU is fast again playback is back to one new
    /// frame per refresh.
    /// </summary>
    [Fact]
    public void A_slow_gpu_is_held_back_at_the_bridge_and_playback_recovers()
    {
        var cuda = Hardware.RequireGpu();
        var presents = new ConcurrentQueue<long>();
        var hooks = new PlaybackHooks
        {
            GeneratorFactory = (c, k, s) => Faulty.Wrap(new BlendGenerator(c, k, s), slowFrom: 30, slowUntil: 70, slowNs: 40_000_000, kernels: k),
            BeforePresent = j => presents.Enqueue(Stopwatch.GetTimestamp()),
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 5), hooks);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.True(report.SkippedBackPressure > 10, $"only {report.SkippedBackPressure} slots skipped for back-pressure");
        Assert.True(report.MaxOutstanding <= report.Setup.MaxLead, $"lead {report.MaxOutstanding} over the limit {report.Setup.MaxLead}");
        Assert.Equal(0, report.OutOfOrderSignals);
        AssertRecovered(presents, 120);
    }

    /// <summary>The present thread stalled (a presenter that does not take frames) for 0.5 s and for 3 s.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    public void A_stalled_presenter_recovers(double stallS)
    {
        var cuda = Hardware.RequireGpu();
        var presents = new ConcurrentQueue<long>();
        var stalled = 0;
        var hooks = new PlaybackHooks
        {
            BeforePresent = j =>
            {
                if (j.SlotIndex >= 200 && Interlocked.Exchange(ref stalled, 1) == 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(stallS));
                }

                presents.Enqueue(Stopwatch.GetTimestamp());
            },
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), stallS + 4), hooks);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.Equal(1, stalled);
        Assert.True(report.SkippedPresenterBacklog + report.DroppedRefused > 0, "nothing was skipped while the presenter stalled");
        Assert.True(report.MaxOutstanding <= report.Setup.MaxLead);
        AssertRecovered(presents, 120);
    }

    /// <summary>The generator thread stalled (a producer that stops writing) for 0.5 s and for 3 s.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    public void A_stalled_generator_thread_recovers(double stallS)
    {
        var cuda = Hardware.RequireGpu();
        var presents = new ConcurrentQueue<long>();
        var stalled = 0;
        var hooks = new PlaybackHooks
        {
            BeforeWrite = j =>
            {
                if (j.SlotIndex >= 200 && Interlocked.Exchange(ref stalled, 1) == 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(stallS));
                }
            },
            BeforePresent = j => presents.Enqueue(Stopwatch.GetTimestamp()),
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), stallS + 4), hooks);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.Equal(1, stalled);
        Assert.True(report.SkippedBackPressure + report.SkippedPresenterBacklog > 0, "nothing was skipped while the generator thread stalled");
        Assert.True(report.MaxOutstanding <= report.Setup.MaxLead);
        AssertRecovered(presents, 120);
    }

    /// <summary>
    /// Ring writes whose presents are dropped (a run of them longer than the ring, as happens when presents fall behind the
    /// refreshes) are released by the present thread: the generator's write into the same slot a ring's length later waits
    /// for that release, so without it the generator, the source's frames and every later write would stall for good and
    /// playback would show the last image from then on. Both clocks.
    /// </summary>
    [Theory]
    [InlineData(PlaybackClock.Virtual)]
    [InlineData(PlaybackClock.Real)]
    public void Dropped_presents_release_their_slots_and_playback_goes_on(PlaybackClock clock)
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get("c60-long");
        var options = clock == PlaybackClock.Virtual
            ? Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 5)
            : Realtime(path, 5);
        options = options with { StallTimeoutNs = 3_000_000_000 };
        var lastWrite = 0L;
        var dropped = 0;
        var hooks = new PlaybackHooks
        {
            BeforePresent = j =>
            {
                if (j.Kind == PresentKind.Write)
                {
                    Volatile.Write(ref lastWrite, j.SlotIndex);
                }
            },
            DropPresent = j =>
            {
                var drop = j.SlotIndex is >= 150 and < 300;
                dropped += drop ? 1 : 0;
                return drop;
            },
        };
        var report = Playback.Run(cuda, options, hooks);
        Log.WriteLine($"{clock}: {dropped} write presents dropped; last write presented for slot {lastWrite}; {Playback.Describe(report)}");
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.True(dropped >= 100, $"only {dropped} write presents dropped");
        Assert.True(report.DroppedRefused >= dropped, $"{report.DroppedRefused} presents counted dropped, {dropped} dropped by the hook");
        Assert.Equal(0, report.PresenterStats.RejectedRequests);
        Assert.True(lastWrite >= 500, $"the last ring write handed to the present thread was for slot {lastWrite}: writing stopped after the drops");
    }

    /// <summary>
    /// A generator thread that stops making progress for longer than the stall timeout (writes handed out, none finished)
    /// fails the run with that reason, instead of playback showing the last image for as long as it runs; once it is free
    /// again the teardown is the ordinary one.
    /// </summary>
    [Fact]
    public void A_generator_thread_that_makes_no_progress_fails_the_run()
    {
        var cuda = Hardware.RequireGpu();
        var stalled = 0;
        var hooks = new PlaybackHooks
        {
            BeforeWrite = j =>
            {
                if (j.SlotIndex >= 200 && Interlocked.Exchange(ref stalled, 1) == 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2.5));
                }
            },
        };
        var watch = Stopwatch.StartNew();
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 20) with { StallTimeoutNs = 1_000_000_000 }, hooks);
        Log.WriteLine($"{watch.Elapsed.TotalSeconds:0.00} s; {Playback.Describe(report)}");
        Assert.Equal(StopReason.Failure, report.StopReason);
        var failure = Assert.IsType<TimeoutException>(report.Failure);
        Assert.Contains("No ring write finished for 1 s", failure.Message, StringComparison.Ordinal);
        Assert.Empty(report.TeardownErrors);
        Assert.Equal(0, report.AbandonedImports);
        Assert.False(report.SourceLeaked);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// GPU work on the ring writes' stream that outlasts every teardown bound (the generator's 60th frame keeps the GPU busy
    /// for 12 s; FMP_PLAY_FAULT's gpu-busy): the stall check fails the run, and teardown, finding the stream not idle,
    /// abandons everything GPU-side instead of calling what would wait for it (cuModuleUnload, cuMemFree, the decoder,
    /// the device's idle wait); Run returns within bounds and says what it left. It used to hang for good in
    /// cuModuleUnload. Once the GPU work ends (the writes behind it were released when the present thread stopped), the
    /// stream drains and the process plays a new session normally. A wait that never ends is checked out of process
    /// (CliTests), where the process exit reclaims the GPU.
    /// </summary>
    [Fact]
    public void Gpu_work_that_does_not_finish_is_abandoned_and_the_run_still_returns()
    {
        var cuda = Hardware.RequireGpu();
        CudaStream? stream = null;
        var fault = PlaybackFaults.Parse("gpu-busy@60:12")!;
        var hooks = new PlaybackHooks
        {
            WrapGenerator = (h, s, k) =>
            {
                stream = s;
                return fault(h, s, k);
            },
        };
        var watch = Stopwatch.StartNew();
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 30) with { StallTimeoutNs = 1_000_000_000 }, hooks, timeoutS: 60);
        var returned = watch.Elapsed;
        Log.WriteLine($"returned after {returned.TotalSeconds:0.0} s; {Playback.Describe(report)}; abandoned: {report.Abandoned}");
        Assert.True(returned < TimeSpan.FromSeconds(25), $"Run took {returned.TotalSeconds:0.0} s");
        Assert.Equal(StopReason.Failure, report.StopReason);
        Assert.Contains("No ring write finished", report.Failure?.Message, StringComparison.Ordinal);
        Assert.True(report.GpuAbandoned);
        Assert.Contains("left allocated", report.Abandoned, StringComparison.Ordinal);
        Assert.Contains(report.Failures(), f => f.Contains("Teardown abandoned", StringComparison.Ordinal));

        // The GPU work ends by itself; the process is then whole again.
        Assert.NotNull(stream);
        var giveUp = Stopwatch.StartNew();
        while (!stream!.IsIdle)
        {
            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(40), "the abandoned stream never drained");
            Thread.Sleep(100);
        }

        var second = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 2), timeoutS: 60);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(second));
        Assert.False(second.GpuAbandoned);
    }

    /// <summary>
    /// The file's first frame is the first one shown (the review saw playback start at frame 3, 9 or 4 in windowed runs and at
    /// 11 of 120 offscreen: the frames due while the decoder started were skipped). Both clocks.
    /// </summary>
    [Theory]
    [InlineData(PlaybackClock.Virtual)]
    [InlineData(PlaybackClock.Real)]
    public void Playback_starts_with_the_first_frame_of_the_file(PlaybackClock clock)
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get("c60-long");
        var options = clock == PlaybackClock.Virtual ? Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 2) : Realtime(path, 2);
        var first = -1L;
        var hooks = new PlaybackHooks
        {
            BeforePresent = j =>
            {
                if (first < 0 && j.Kind == PresentKind.Write)
                {
                    first = j.FrameA;
                }
            },
        };
        var report = Playback.Run(cuda, options, hooks);
        Log.WriteLine($"{clock}: first frame shown {report.FirstShownFrame}, first written {first}, content offset {report.ContentOffsetNs / 1e6:0.000} ms");
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(0, report.FirstShownFrame);
        Assert.Equal(0, first);
    }

    /// <summary>Without looping, playback ends at the end of the file (after its last frame); looping, it goes on.</summary>
    [Fact]
    public void The_end_of_the_file_ends_playback_unless_it_loops()
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get("c60-short");
        var once = Playback.Run(cuda, Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 30));
        Assert.Empty(once.Failures());
        Assert.Equal(StopReason.EndOfFile, once.StopReason);
        Assert.InRange(once.PlaybackNs, 400_000_000L, 700_000_000L); // 30 frames at 60 fps, shown for 0.5 s
        Assert.Equal(30, once.SourceStatistics.FramesDelivered);

        var looped = Playback.Run(cuda, Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 2) with { Loop = true });
        Assert.Empty(looped.Failures());
        Assert.Equal(StopReason.Duration, looped.StopReason);
        Assert.True(looped.SourceStatistics.Loops >= 3, $"{looped.SourceStatistics.Loops} loops");
        Assert.True(looped.Counters.Discontinuities >= 3);
    }

    /// <summary>
    /// Sessions started and stopped many times: the process's descriptors, threads and GPU memory (the context's free
    /// memory and nvidia-smi's figure for the process) are the same after as after the first two, and nothing hangs.
    /// </summary>
    [Fact]
    public void Starting_and_stopping_many_times_leaks_nothing()
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get("c30");
        void Once(GeneratorKind generator, bool realtime)
        {
            var options = realtime
                ? Realtime(path, 0.4) with { Generator = generator, WarmupNs = 100_000_000 }
                : Playback.Virtual(path, generator, 2, 120, 0.4);
            var report = Playback.Run(cuda, options);
            Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
            Assert.Equal(0, report.AbandonedImports);
        }

        for (var i = 0; i < 3; i++)
        {
            Once(GeneratorKind.Blend, i % 2 == 0);
        }

        Assert.True(CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(5)));
        var before = Resources.Take(cuda);
        for (var i = 0; i < 12; i++)
        {
            Once(i % 3 == 2 ? GeneratorKind.Repeat : GeneratorKind.Blend, i % 2 == 0);
        }

        Assert.True(CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(5)));
        var after = Resources.Take(cuda);
        Log.WriteLine($"before {before}; after {after}");
        Assert.InRange(after.Descriptors - before.Descriptors, -8, 8);
        Assert.InRange(after.Threads - before.Threads, -8, 8);
        Assert.InRange(Math.Abs(after.FreeBytes - before.FreeBytes), 0, 64L << 20);
        if (before.ProcessMiB >= 0 && after.ProcessMiB >= 0)
        {
            Assert.InRange(after.ProcessMiB - before.ProcessMiB, -64, 64);
        }
    }

    /// <summary>
    /// A stop while writes are in flight on a GPU that takes 150 ms per generation: the run stops and tears down within its
    /// bounds (every write handed out is enqueued and signalled, the ring drained, the source freed), nothing abandoned.
    /// </summary>
    [Fact]
    public void A_stop_with_writes_in_flight_tears_down_cleanly()
    {
        var cuda = Hardware.RequireGpu();
        var hooks = new PlaybackHooks
        {
            GeneratorFactory = (c, k, s) => Faulty.Wrap(new BlendGenerator(c, k, s), slowFrom: 10, slowUntil: int.MaxValue, slowNs: 150_000_000, kernels: k),
        };
        var watch = Stopwatch.StartNew();
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 30), hooks, during: s =>
        {
            Thread.Sleep(1500);
            s.RequestStop(StopReason.User);
        });
        Log.WriteLine($"stopped and torn down {watch.Elapsed.TotalSeconds:0.00} s after the start");
        Assert.Equal(StopReason.User, report.StopReason);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(0, report.AbandonedImports);
        Assert.False(report.SourceLeaked);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>In the last second of the run, presents came at about the refresh rate again.</summary>
    private static void AssertRecovered(ConcurrentQueue<long> presents, double rateHz)
    {
        var times = presents.ToArray();
        Assert.NotEmpty(times);
        var end = times[^1];
        var lastSecond = times.Count(t => end - t < Stopwatch.Frequency);
        Assert.True(lastSecond >= rateHz * 0.9, $"{lastSecond} presents in the last second at {rateHz} Hz");
    }
}

/// <summary>A generator decorator that throws, or is slow on the GPU, for some of its calls.</summary>
internal sealed class Faulty : IFrameGenerator
{
    private readonly CudaFrameGenerator _inner;
    private readonly int _throwAt;
    private readonly int _slowFrom;
    private readonly int _slowUntil;
    private readonly long _slowNs;
    private readonly ImageKernels? _kernels;
    private int _calls;

    private Faulty(CudaFrameGenerator inner, int throwAt, int slowFrom, int slowUntil, long slowNs, ImageKernels? kernels)
    {
        _inner = inner;
        _throwAt = throwAt;
        _slowFrom = slowFrom;
        _slowUntil = slowUntil;
        _slowNs = slowNs;
        _kernels = kernels;
    }

    public FrameGenCapabilities Capabilities => _inner.Capabilities;

    public static GeneratorHandle Wrap(CudaFrameGenerator inner, int throwAt = -1, int slowFrom = -1, int slowUntil = -1, long slowNs = 0, ImageKernels? kernels = null)
    {
        var faulty = new Faulty(inner, throwAt, slowFrom, slowUntil, slowNs, kernels);
        return new GeneratorHandle(faulty, inner.TryReadGpuTime, "faulty " + inner.GetType().Name);
    }

    public void Configure(in FrameGenConfig config) => _inner.Configure(config);

    public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
    {
        var call = ++_calls;
        if (call == _throwAt)
        {
            // What a failing call may leave: its GPU-side waits queued, nothing signalled.
            _inner.Stream.Wait(prev.Ready);
            _inner.Stream.Wait(output.Ready);
            throw new InvalidOperationException($"injected generator failure at call {call}");
        }

        if (call >= _slowFrom && call < _slowUntil && _kernels is not null)
        {
            _kernels.EnqueueDelay(_inner.Stream, _slowNs);
        }

        _inner.Enqueue(prev, curr, t, output, done);
    }

    public void NotifyDiscontinuity(long contentTimeNs) => _inner.NotifyDiscontinuity(contentTimeNs);

    public void Dispose() => _inner.Dispose();
}

/// <summary>The process's descriptors, threads and GPU memory.</summary>
internal readonly record struct Resources(int Descriptors, int Threads, long FreeBytes, long ProcessMiB)
{
    public static Resources Take(CudaContext cuda)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var descriptors = Directory.GetFiles("/proc/self/fd").Length;
        var threads = Directory.GetDirectories("/proc/self/task").Length;
        var free = cuda.MemoryInfo.FreeBytes;
        return new Resources(descriptors, threads, free, ProcessGpuMiB());
    }

    private static long ProcessGpuMiB()
    {
        try
        {
            var smi = Cuda.Tests.Support.Tools.Which("nvidia-smi");
            if (smi is null)
            {
                return -1;
            }

            var (exit, output) = Cuda.Tests.Support.Tools.Run(smi, ["--query-compute-apps=pid,used_memory", "--format=csv,noheader,nounits"], timeoutMs: 20_000);
            if (exit != 0)
            {
                return -1;
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && pid == Environment.ProcessId && long.TryParse(parts[1], out var mib))
                {
                    return mib;
                }
            }

            return -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
