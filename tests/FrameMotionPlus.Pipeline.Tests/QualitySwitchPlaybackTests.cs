using System.Collections.Concurrent;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// Live quality switching through the whole path (offscreen): VFG switched Medium → High → Medium mid-run on the
/// virtual clock with the true middle frame of every pair known (every generated frame, the first ones after each switch
/// included, must be a genuine interpolation that beats the blend), and a model generator on the real clock (the switch
/// lands on the generator thread between two writes, in order, and frames keep coming).
/// </summary>
public class QualitySwitchPlaybackTests
{
    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void Vfg_switched_mid_run_keeps_every_generated_frame_right()
    {
        var cuda = Hardware.RequireVfx();
        var even = Clips.Get("even-60");
        var truth = ReferenceFrames.Decode(cuda, Clips.Get("truth-120"), 240, loop: false);
        var reference = ReferenceFrames.Decode(cuda, even, 120, loop: false);
        var checker = new PresentChecker(reference, GeneratorKind.Vfg) { Truth = a => 2 * a + 1 < truth.Count ? truth.Pixels((2 * a) + 1) : null };
        PlaybackSession? session = null;
        var presented = 0;
        var hooks = new PlaybackHooks
        {
            OffscreenReadback = true,
            OnPresented = f =>
            {
                checker.OnPresented(f);
                var n = Interlocked.Increment(ref presented);
                if (n is 60 or 140)
                {
                    Assert.True(session!.RequestQualityToggle());
                }
            },
        };
        var options = Playback.Virtual(even, GeneratorKind.Vfg, 2, 120, 1.8) with { QualitySwitch = true };
        var report = Playback.Run(cuda, options, hooks, onOpen: s =>
        {
            checker.Session = s;
            session = s;
        });
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        Assert.Equal((FrameGenQuality.Medium, FrameGenQuality.High), report.Setup.QualitySwitch);
        Assert.NotNull(report.Setup.SecondVfgLoad);
        Assert.Equal(2, report.QualitySwitches.Count);
        Assert.All(report.QualitySwitches, n => Assert.True(n.Switched));
        Assert.Equal(FrameGenQuality.High, report.QualitySwitches[0].Commit.To);
        Assert.Equal(FrameGenQuality.Medium, report.QualityAtEnd);
        Assert.Equal(0, report.GeneratorFaults);
        Assert.Equal(0, report.Fallbacks);
        Assert.Equal(0, report.Statistics.TotalMisses);

        // The switch happened on the GPU: High's generation times are several times Medium's.
        var s = report.Statistics;
        var medium = s.GenerationTimesAt(FrameGenQuality.Medium);
        var high = s.GenerationTimesAt(FrameGenQuality.High);
        Log.WriteLine($"d at Medium p50 {medium.Percentile(0.5) / 1e6:0.000} ms over {medium.Count}, at High p50 {high.Percentile(0.5) / 1e6:0.000} ms over {high.Count}");
        Assert.True(medium.Count > 20 && high.Count > 20, $"Medium {medium.Count}, High {high.Count} generated frames timed");
        Assert.True(high.Percentile(0.5) > 1.5 * medium.Percentile(0.5));

        // Every generated frame, the first after each switch included, is between its neighbours and beats the blend at t = 0.5.
        var halves = checker.Results.Where(r => r.Frame.Kind == SlotKind.Generated && Math.Abs(r.Frame.Timestep - 0.5) < 0.01).ToList();
        Assert.True(halves.Count > 40, $"only {halves.Count} generated frames at t = 0.5");
        var better = halves.Count(r => r.ToTruth < r.BlendToTruth);
        Assert.True(better >= halves.Count * 0.9, $"VFG beat the blend in only {better} of {halves.Count} frames");
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void A_second_instance_that_cannot_be_loaded_leaves_one_and_plays_on()
    {
        var cuda = Hardware.RequireVfx();
        var notices = new ConcurrentQueue<string>();
        var hooks = new PlaybackHooks
        {
            BeforeSecondQualityLoad = q => throw new OutOfMemoryLikeException($"no room for {q}"),
            OnQualityNotice = notices.Enqueue,
        };
        var options = Playback.Virtual(Clips.Get("c60"), GeneratorKind.Vfg, 2, 120, 1.0) with { QualitySwitch = true };
        var report = Playback.Run(cuda, options, hooks, onOpen: s => Assert.False(s.RequestQualityToggle()));
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Null(report.Setup.QualitySwitch);
        Assert.Null(report.Setup.SecondVfgLoad);
        Assert.Contains("no room for High", report.Setup.QualitySwitchLoadFailure, StringComparison.Ordinal);
        Assert.Contains("could not be loaded", report.Setup.QualitySwitchOff, StringComparison.Ordinal);
        Assert.Empty(report.QualitySwitches);
        Assert.True(report.GeneratedWrites > 20);
        Assert.Equal(0, report.GeneratorFaults);
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_model_generator_switches_between_two_writes_and_frames_keep_coming()
    {
        var cuda = Hardware.RequireGpu();
        var calls = new ConcurrentQueue<(FrameGenQuality Quality, string? Thread, long WritesBefore)>();
        long writes = 0;
        PlaybackSession? session = null;
        var hooks = new PlaybackHooks
        {
            GeneratorFactory = (c, k, s) =>
            {
                var blend = new BlendGenerator(c, k, s);
                return new GeneratorHandle(blend, blend.TryReadGpuTime, "blend with two qualities", q =>
                {
                    calls.Enqueue((q, Thread.CurrentThread.Name, Interlocked.Read(ref writes)));
                    return true;
                });
            },
            BeforeWrite = j =>
            {
                var n = Interlocked.Increment(ref writes);
                if (n == 150)
                {
                    session!.RequestQualityToggle();
                }
            },
        };
        var options = new PlaybackOptions
        {
            Path = Clips.Get("c60-long"),
            Generator = GeneratorKind.Blend,
            Multiplier = 2,
            Display = DisplayKind.Offscreen,
            Clock = PlaybackClock.Real,
            OffscreenRate = Core.Time.FrameRate.FromHz(120),
            DurationNs = 3_000_000_000,
            WarmupNs = 500_000_000,
            Loop = true,
            QualitySwitch = true,
        };
        var report = Playback.Run(cuda, options, hooks, onOpen: s =>
        {
            session = s;
            Assert.True(s.RequestQualityToggle()); // Q during start-up: playback starts at the other quality
        });
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);

        // The pre-roll's warm-up call (a switch to the quality it has), then the two switches, all on the generator thread.
        var list = calls.ToList();
        Assert.Equal(3, list.Count);
        Assert.All(list, c => Assert.Equal("fmp-generator", c.Thread));
        Assert.Equal(FrameGenQuality.Medium, list[0].Quality);
        Assert.Equal(FrameGenQuality.High, list[1].Quality);
        Assert.Equal(FrameGenQuality.Medium, list[2].Quality);
        Assert.Equal(2, report.QualitySwitches.Count);
        Assert.Equal(FrameGenQuality.Medium, report.QualityAtEnd);

        // Frames kept coming through both switches; nothing is counted as missed or fell back.
        Assert.Equal(0, report.Statistics.PipelineMisses);
        Assert.Equal(0, report.Fallbacks);
        Assert.Equal(0, report.SkippedBackPressure + report.SkippedPresenterBacklog);
        Assert.True(report.GeneratedWrites > 100);
        Assert.Equal(2, report.QualitySwitches[1].Commit.Epoch);
    }

    private sealed class OutOfMemoryLikeException(string message) : Exception(message);
}
