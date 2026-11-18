using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// A 60.000 fps file on a 119.88 Hz output is not phase-locked: almost every slot was generated and the generator ran at
/// twice the rate it needs (Phase 4b: 96% generated). A file has no live constraint, so its clock is stretched by 0.1% to
/// lock: real frames on every other refresh, a generated one between. On the virtual clock (deterministic).
/// </summary>
public class ResampleTests
{
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_60_fps_file_on_119_88_hz_is_stretched_to_lock_and_half_its_slots_are_real()
    {
        var cuda = Hardware.RequireGpu();
        var report = Playback.Run(cuda, Playback.Virtual(Clips.Get("c60-mp4"), GeneratorKind.Blend, 2, 119.88, 4));
        var s = report.Statistics;
        Log.WriteLine($"resample x{report.Resample:0.000000}; {s.Real} real, {s.Generated} generated, {s.Repeated} repeated; phase-locked {report.Diagnostics.PhaseCoherent}; T {report.Diagnostics.SourceIntervalNs / 1e6:0.000} ms");
        Assert.InRange(report.Resample, 1.00099, 1.00101);
        Assert.True(report.Diagnostics.PhaseCoherent, "not phase-locked");
        Assert.InRange(s.Real, (s.Slots * 45) / 100, (s.Slots * 55) / 100);
        Assert.InRange(s.Generated, (s.Slots * 45) / 100, (s.Slots * 55) / 100);
        Assert.Equal(0, s.TotalMisses);
    }

    /// <summary>
    /// Pure: what <c>--clock auto</c> asks of an output before choosing variable refresh, and which file rates are
    /// stretched to lock (within 0.5% of a whole number of refreshes per frame).
    /// </summary>
    [Theory]
    [InlineData(120.0, 120_000, 1001, false)] // 60 fps at 2x on HDMI-A-1 (119.88 Hz)
    [InlineData(60.0, 120_000, 1001, true)] // 30 fps at 2x there
    [InlineData(120.0, 240_016, 1000, true)] // 60 fps at 2x on DP-3
    [InlineData(240.0, 240_016, 1000, true)] // 60 fps at 4x on DP-3
    public void Variable_refresh_is_chosen_only_where_the_output_can_show_the_rate(double neededHz, long num, long den, bool fits) =>
        Assert.Equal(fits, PlaybackSession.VrrFits(neededHz, new FrameRate(num, den)));

    [Theory]
    [InlineData(60, 1, 120_000, 1001, 1.001)] // 60 fps on 119.88 Hz: 0.1% slower
    [InlineData(30, 1, 120_000, 1001, 1.001)] // 30 fps there: 4 refreshes a frame
    [InlineData(24, 1, 120_000, 1001, 1.001)] // 24 fps there: 5
    [InlineData(60, 1, 120, 1, 1.0)] // exact: nothing to do
    [InlineData(60_000, 1001, 120_000, 1001, 1.0)] // 59.94 on 119.88: exact
    [InlineData(25, 1, 120_000, 1001, 1.0)] // 4.8 refreshes a frame: too far off, played as it is
    [InlineData(60, 1, 240_016, 1000, 0.9999333)] // 60 fps on 240.016 Hz: 67 ppm faster
    public void A_file_is_stretched_only_within_half_a_percent_of_a_whole_number_of_refreshes(long sNum, long sDen, long dNum, long dDen, double expected) =>
        Assert.Equal(expected, PlaybackSession.ResampleFactor(new FrameRate(sNum, sDen), new FrameRate(dNum, dDen)), 6);

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Without_resampling_the_same_file_is_mostly_generated()
    {
        var cuda = Hardware.RequireGpu();
        var options = Playback.Virtual(Clips.Get("c60-mp4"), GeneratorKind.Blend, 2, 119.88, 4) with { ResampleToDisplay = false };
        var report = Playback.Run(cuda, options);
        var s = report.Statistics;
        Log.WriteLine($"resample x{report.Resample:0.000000}; {s.Real} real, {s.Generated} generated; phase-locked {report.Diagnostics.PhaseCoherent}");
        Assert.Equal(1.0, report.Resample);
        Assert.True(s.Generated > s.Slots * 8 / 10, $"{s.Generated} of {s.Slots} generated");
    }
}
