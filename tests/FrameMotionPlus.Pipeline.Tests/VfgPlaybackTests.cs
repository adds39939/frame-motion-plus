using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// VFG through the whole path on the virtual clock: a lossless 60 fps clip made of every second frame of a lossless
/// 120 fps one, played at 2x on a 120 Hz grid, so the true middle frame of every pair is known. Each generated frame
/// must be a genuine interpolation (not a copy of a neighbour) and, at t = 0.5, closer to the true middle frame than the
/// blend of its neighbours; real frames stay bit-exact.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class VfgPlaybackTests
{
    [Fact]
    public void Vfg_frames_lie_between_their_frames_and_beat_the_blend()
    {
        var cuda = Hardware.RequireVfx();
        var even = Clips.Get("even-60");
        var truth = ReferenceFrames.Decode(cuda, Clips.Get("truth-120"), 240, loop: false);
        var reference = ReferenceFrames.Decode(cuda, even, 120, loop: false);
        Assert.Equal(120, reference.Count);
        var checker = new PresentChecker(reference, GeneratorKind.Vfg) { Truth = a => 2 * a + 1 < truth.Count ? truth.Pixels((2 * a) + 1) : null };
        var hooks = new PlaybackHooks { OffscreenReadback = true, OnPresented = checker.OnPresented };
        var options = Playback.Virtual(even, GeneratorKind.Vfg, 2, 120, 1.8);
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        Assert.Equal(0, report.GeneratorFaults);

        var generated = checker.Results.Where(r => r.Frame.Kind == SlotKind.Generated).ToList();
        var halves = generated.Where(r => Math.Abs(r.Frame.Timestep - 0.5) < 0.01).ToList();
        Assert.True(generated.Count > 80, $"only {generated.Count} generated frames");
        Assert.True(halves.Count > 40, $"only {halves.Count} generated frames at t = 0.5 (the phase did not lock?)");
        var better = halves.Count(r => r.ToTruth < r.BlendToTruth);
        Log.WriteLine($"{generated.Count} VFG frames, {halves.Count} at t = 0.5: error to the true middle frame {halves.Average(r => r.ToTruth):0.###} codes on average " +
                      $"vs the blend's {halves.Average(r => r.BlendToTruth):0.###}; closer than the blend in {better} of them; nearest neighbour at least " +
                      $"{generated.Min(r => r.ToNeighbour):0.###} codes away");
        // The test source's frame counter changes digits, which no interpolation can produce, so the margin is modest
        // (measured: 13.9 against 16.7 codes, better in 104 of 108 frames).
        Assert.True(better >= halves.Count * 0.9, $"VFG beat the blend in only {better} of {halves.Count} frames");
        Assert.True(halves.Average(r => r.ToTruth) < halves.Average(r => r.BlendToTruth), "VFG is not closer to the true middle frames than the blend on average");
    }
}
