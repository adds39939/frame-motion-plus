using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>Reproduces the design doc's "Estimated added latency" table from ΔL = d + (M − 1)/M · T.</summary>
public class LatencyModelTests
{
    // Mode, source fps, M, T (ms), Hold (ms), Per-frame budget T/M (ms) — exactly as printed in the design doc.
    public static TheoryData<string, int, int, double, double, double> Table => new()
    {
        { "60 to 120", 60, 2, 16.7, 8.3, 8.3 },
        { "30 to 60", 30, 2, 33.3, 16.7, 16.7 },
        { "40 to 120", 40, 3, 25.0, 16.7, 8.3 },
        { "30 to 120", 30, 4, 33.3, 25.0, 8.3 },
        { "60 to 240", 60, 4, 16.7, 12.5, 4.2 },
    };

    private static long T(int fps) => (long)Math.Round(FrameRate.FromHz(fps).PeriodNs);

    private static double Ms(long ns) => Math.Round(Nanoseconds.ToMilliseconds(ns), 1, MidpointRounding.AwayFromZero);

    [Theory]
    [MemberData(nameof(Table))]
    public void Hold_and_per_frame_budget_columns_match_the_design_doc(string mode, int fps, int m, double tMs, double holdMs, double budgetMs)
    {
        Assert.False(string.IsNullOrEmpty(mode));
        var t = T(fps);
        Assert.Equal(tMs, Ms(t));
        Assert.Equal(holdMs, Ms(LatencyModel.HoldNs(t, m)));
        Assert.Equal(budgetMs, Ms(LatencyModel.PerFrameBudgetNs(t, m)));
    }

    // Mode, fps, M, d range (ms) from the doc's per-resolution estimates, and the printed added-latency range.
    public static TheoryData<string, int, int, double, double, int, int> AddedLatency => new()
    {
        { "60 to 120 @1080p", 60, 2, 2.6, 4.3, 11, 13 },
        { "60 to 120 @1440p", 60, 2, 4.6, 7.6, 13, 16 },
        { "30 to 60 @1440p", 30, 2, 4.6, 7.6, 21, 24 },
        { "30 to 60 @4K", 30, 2, 10.4, 17.1, 27, 34 },
        { "40 to 120 @1440p", 40, 3, 4.6, 7.6, 21, 24 },
        { "30 to 120 @1440p", 30, 4, 4.6, 7.6, 30, 33 },
        { "60 to 240 @1080p", 60, 4, 2.6, 4.3, 15, 17 },
    };

    [Theory]
    [MemberData(nameof(AddedLatency))]
    public void Added_latency_column_matches_the_design_doc(string mode, int fps, int m, double dLowMs, double dHighMs, int lowMs, int highMs)
    {
        Assert.False(string.IsNullOrEmpty(mode));
        var t = T(fps);
        var low = Nanoseconds.ToMilliseconds(LatencyModel.AddedLatencyNs(t, m, Nanoseconds.FromMilliseconds(dLowMs)));
        var high = Nanoseconds.ToMilliseconds(LatencyModel.AddedLatencyNs(t, m, Nanoseconds.FromMilliseconds(dHighMs)));
        Assert.Equal(lowMs, (int)Math.Round(low, MidpointRounding.AwayFromZero));
        Assert.Equal(highMs, (int)Math.Round(high, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData(60, 2, 4.3, true)]   // 60 to 120 fits at 1080p
    [InlineData(60, 2, 7.6, true)]   // ... and 1440p
    [InlineData(60, 2, 10.4, false)] // ... but not 4K
    [InlineData(30, 2, 16.6, true)]  // 30 to 60: 4K marginal
    [InlineData(30, 2, 17.1, false)]
    [InlineData(30, 4, 7.6, true)]   // 30 to 120 at 1440p
    [InlineData(60, 4, 4.3, false)]  // 60 to 240 at 1080p: borderline
    [InlineData(60, 4, 2.6, true)]
    public void Fits_column_follows_d_le_T_over_M(int fps, int m, double dMs, bool fits)
    {
        Assert.Equal(fits, LatencyModel.Fits(T(fps), m, Nanoseconds.FromMilliseconds(dMs)));
    }

    [Fact]
    public void Max_multiplier_that_fits_is_the_largest_M_with_d_within_budget()
    {
        Assert.Equal(4, LatencyModel.MaxMultiplierThatFits(T(30), Nanoseconds.FromMilliseconds(7.6), cap: 4));
        Assert.Equal(3, LatencyModel.MaxMultiplierThatFits(T(30), Nanoseconds.FromMilliseconds(10.5), cap: 8));
        Assert.Equal(1, LatencyModel.MaxMultiplierThatFits(T(60), Nanoseconds.FromMilliseconds(9), cap: 8));
        Assert.Equal(8, LatencyModel.MaxMultiplierThatFits(T(30), Nanoseconds.FromMilliseconds(1), cap: 8));
    }

    [Fact]
    public void Evaluate_returns_a_consistent_row()
    {
        var row = LatencyModel.Evaluate(T(60), 2, 5_000_000);
        Assert.Equal(row.GenerationTimeNs + row.HoldNs, row.AddedLatencyNs);
        Assert.InRange(row.HoldNs, row.SourceIntervalNs - row.PerFrameBudgetNs - 1, row.SourceIntervalNs - row.PerFrameBudgetNs + 1);
        Assert.True(row.Fits);
    }

    [Fact]
    public void Non_integer_fixed_refresh_hold_approaches_a_whole_interval()
    {
        var t = T(30);
        Assert.Equal((long)Math.Round(t * 0.98), LatencyModel.NonIntegerFixedRefreshHoldNs(t, 0.02));
        Assert.True(LatencyModel.NonIntegerFixedRefreshHoldNs(t, 0.02) > LatencyModel.HoldNs(t, 4));
    }
}
