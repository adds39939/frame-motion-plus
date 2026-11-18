using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The whole path, file to presented image, on the virtual clock and an offscreen target the source's size (so a present
/// is a 1:1 copy): every presented image is read back and checked against what the scheduler decided for its slot.
/// Real frames are bit-exact with the decoded-and-converted reference; blended frames bit-exact with the CPU blend of the
/// right two frames at the right t; repeats equal to the image before; content times never go back; every decided slot
/// is presented once, in order; no generated frame spans a discontinuity (a loop, a seek, a jump in the timestamps).
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class OffscreenVerificationTests
{
    /// <summary>The modes: 2x at both ends of the usual range, 4x, a non-integer ratio, a variable frame rate, 10-bit.</summary>
    [Theory]
    [InlineData("c30", 60.0, 2, 2.0)]      // 30 -> 60
    [InlineData("c60", 120.0, 2, 1.5)]     // 60 -> 120
    [InlineData("c30", 120.0, 4, 2.0)]     // 30 -> 120 (4x)
    [InlineData("c24", 60.0, 3, 2.0)]      // 24 -> 60 (2.5x: non-integer)
    [InlineData("vfr", 120.0, 4, 1.5)]     // 23 / 43 ms frames
    [InlineData("hevc10", 120.0, 2, 1.2)]  // 10-bit: RGB10A2 through the ring
    public void Every_presented_frame_is_what_the_scheduler_decided(string clip, double rateHz, int multiplier, double seconds)
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get(clip);
        var reference = ReferenceFrames.Decode(cuda, path, 200, loop: true);
        var checker = new PresentChecker(reference, GeneratorKind.Blend);
        var hooks = new PlaybackHooks { OffscreenReadback = true, OnPresented = checker.OnPresented };
        var options = Playback.Virtual(path, GeneratorKind.Blend, multiplier, rateHz, seconds) with { Loop = true };
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);

        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        CheckSequence(checker, reference, report, rateHz);
        var generated = checker.Results.Count(r => r.Frame.Kind == SlotKind.Generated);
        var real = checker.Results.Count(r => r.Frame.Kind == SlotKind.Real);
        Log.WriteLine($"{clip} at {rateHz} Hz, cap {multiplier}: {checker.Results.Count} presents checked: {real} real, {generated} generated, " +
                      $"{checker.Results.Count(r => r.Frame.Kind == SlotKind.Repeat)} repeats; timesteps {string.Join(", ", checker.Results.Where(r => r.Frame.Kind == SlotKind.Generated).Select(r => r.Frame.Timestep.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Distinct().Take(8))}");
        Assert.True(real > 10, $"only {real} real frames presented");
        Assert.True(generated > 10, $"only {generated} generated frames presented");
        Assert.Equal(0, report.Statistics.TotalMisses);
    }

    /// <summary>Large frames: 1080p, 1440p and 2160p (every eighth row compared, the same bit-exact rules).</summary>
    [Theory]
    [InlineData("c60-1080p")]
    [InlineData("c60-1440p")]
    [InlineData("c60-2160p")]
    [InlineData("hevc10-2160p")]
    public void Large_frames_are_presented_exactly(string clip)
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get(clip);
        var reference = ReferenceFrames.Decode(cuda, path, 40, loop: false);
        var checker = new PresentChecker(reference, GeneratorKind.Blend);
        var hooks = new PlaybackHooks { OffscreenReadback = true, OnPresented = checker.OnPresented };
        var options = Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 0.45);
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        CheckSequence(checker, reference, report, 120);
        Assert.Contains(checker.Results, r => r.Frame.Kind == SlotKind.Generated);
        Assert.Contains(checker.Results, r => r.Frame.Kind == SlotKind.Real);
    }

    /// <summary>
    /// A loop and a jump back in the stream's timestamps (two MPEG-TS recordings one after the other) are discontinuities:
    /// they reach the scheduler (and the generator), which shows real frames across them and never a frame generated
    /// between a frame before and one after.
    /// </summary>
    [Theory]
    [InlineData("c60-short", true)]
    [InlineData("ts-concat", false)]
    public void Nothing_is_interpolated_across_a_loop_or_a_timestamp_jump(string clip, bool loop)
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get(clip);
        var reference = ReferenceFrames.Decode(cuda, path, 200, loop);
        var checker = new PresentChecker(reference, GeneratorKind.Blend);
        var hooks = new PlaybackHooks { OffscreenReadback = true, OnPresented = checker.OnPresented };
        var options = Playback.Virtual(path, GeneratorKind.Blend, 2, 120, loop ? 1.5 : 10) with { Loop = loop };
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(loop ? StopReason.Duration : StopReason.EndOfFile, report.StopReason);
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        CheckSequence(checker, reference, report, 120);
        var cuts = Enumerable.Range(0, reference.Count).Count(i => reference.Discontinuity(i));
        Assert.True(cuts >= 1, "the clip has no discontinuity");
        Assert.True(report.Discontinuities >= 1, "no discontinuity reached the pipeline");
        Assert.True(report.Counters.Discontinuities >= 1, "no discontinuity reached the scheduler");
        Log.WriteLine($"{clip}: {cuts} discontinuities in the reference, {report.Discontinuities} through the pipeline");
    }

    /// <summary>A seek in the middle of playback: the first frame after it is a discontinuity, never interpolated across.</summary>
    [Fact]
    public void Nothing_is_interpolated_across_a_seek()
    {
        var cuda = Hardware.RequireGpu();
        var path = Clips.Get("c60-long");
        var reference = ReferenceFrames.Decode(cuda, path, 600, loop: false);
        var checker = new PresentChecker(reference, GeneratorKind.Blend);
        PlaybackSession? session = null;
        var seeked = false;
        var hooks = new PlaybackHooks
        {
            OffscreenReadback = true,
            OnPresented = f =>
            {
                checker.OnPresented(f);
                if (!seeked && f.Kind == SlotKind.Real && f.FrameA >= 60)
                {
                    // To 5 s (frame 300): the frames in between are never shown. Frame ids carry on, so frames after this
                    // one are no longer the reference's (frames already read before the seek still are, but which those
                    // are depends on timing): from here, generated frames are checked against the real frames presented.
                    seeked = true;
                    checker.ReferenceValidUpTo = f.FrameA;
                    session!.Seek(5_000_000_000);
                }
            },
        };
        var options = Playback.Virtual(path, GeneratorKind.Blend, 2, 120, 2.5);
        var report = Playback.Run(cuda, options, hooks, onOpen: s => { session = s; checker.Session = s; });
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.True(seeked);
        Assert.Equal(1, report.SourceStatistics.Seeks);
        Assert.True(report.Discontinuities >= 1, "the seek did not reach the pipeline as a discontinuity");

        var deferred = checker.CheckDeferred();
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        Assert.True(deferred > 10, $"only {deferred} generated frames after the seek could be checked against their real frames");
        Assert.Contains(checker.Results, r => r.Frame.BreakBeforeB && r.Frame.Kind == SlotKind.Real);
        CheckSequence(checker, reference, report, 120, referenceUpTo: checker.ReferenceValidUpTo);
    }

    /// <summary>The order, timing and uniqueness rules over a checked run.</summary>
    private static void CheckSequence(PresentChecker checker, ReferenceFrames reference, PlaybackReport report, double rateHz, long referenceUpTo = long.MaxValue)
    {
        var results = checker.Results;
        Assert.NotEmpty(results);
        var period = 1e9 / rateHz;
        long lastSlot = long.MinValue, lastContent = long.MinValue, lastTarget = long.MinValue;
        var shownReal = new HashSet<long>();
        foreach (var r in results)
        {
            var f = r.Frame;

            // Every decided slot is presented once, in order, each at the next refresh.
            Assert.True(f.SlotIndex > lastSlot, $"slot {f.SlotIndex} presented after slot {lastSlot}");
            if (lastTarget != long.MinValue)
            {
                var refreshes = (f.TargetNs - lastTarget) / period;
                Assert.True(Math.Abs(refreshes - Math.Round(refreshes)) < 0.01 && refreshes >= 0.99, $"slot {f.SlotIndex}: {refreshes:0.###} refreshes after the slot before");
            }

            // Content time never goes back; a repeat shows what was shown before.
            Assert.True(f.ContentNs >= lastContent, $"slot {f.SlotIndex}: content time {f.ContentNs} before {lastContent}");
            if (f.Kind == SlotKind.Real)
            {
                Assert.True(shownReal.Add(f.FrameA), $"real frame {f.FrameA} shown twice as a new image");
                Assert.Equal(f.ContentA, f.ContentNs);
            }

            if (f.Kind == SlotKind.Generated)
            {
                // A pair of neighbouring frames, never across a discontinuity, at a t strictly inside (0, 1) that matches
                // the content time shown.
                Assert.Equal(f.FrameA + 1, f.FrameB);
                Assert.False(f.BreakBeforeB, $"slot {f.SlotIndex} generated across the discontinuity before frame {f.FrameB}");
                if (f.FrameB < reference.Count && f.FrameB <= referenceUpTo)
                {
                    Assert.False(reference.Discontinuity(f.FrameB), $"slot {f.SlotIndex} generated across the discontinuity at frame {f.FrameB}");
                }

                Assert.InRange(f.Timestep, 1e-6, 1 - 1e-6);
                var expected = f.ContentA + (f.Timestep * (f.ContentB - f.ContentA));
                Assert.True(Math.Abs(expected - f.ContentNs) <= 1_000, $"slot {f.SlotIndex}: content {f.ContentNs} for t {f.Timestep} between {f.ContentA} and {f.ContentB}");
            }

            lastSlot = f.SlotIndex;
            lastContent = f.ContentNs;
            lastTarget = f.TargetNs;
        }

        // The run made one present per non-empty slot.
        Assert.Equal(report.Shown, results.Count);
    }
}
