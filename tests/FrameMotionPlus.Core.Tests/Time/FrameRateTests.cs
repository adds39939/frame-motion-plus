using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Time;

public class FrameRateTests
{
    [Theory]
    [InlineData("60", 60, 1)]
    [InlineData("120", 120, 1)]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    [InlineData("119.88", 120000, 1001)]
    [InlineData("60000/1001", 60000, 1001)]
    [InlineData("144/2", 72, 1)]
    [InlineData("59.95", 1199, 20)]
    [InlineData("47.5", 95, 2)]
    public void Parses_decimal_ntsc_and_rational_rates(string text, long num, long den)
    {
        Assert.True(FrameRate.TryParse(text, out var rate));
        Assert.Equal(new FrameRate(num, den), rate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-60")]
    [InlineData("0")]
    [InlineData("60/0")]
    [InlineData("1e3")]
    public void Rejects_invalid_rates(string? text) => Assert.False(FrameRate.TryParse(text, out _));

    [Fact]
    public void Tick_times_are_exact_with_no_accumulated_drift()
    {
        var ntsc = new FrameRate(60000, 1001);

        // After exactly 60000 frames, a 60000/1001 Hz clock has advanced exactly 1001 seconds.
        Assert.Equal(1001L * Nanoseconds.PerSecond, ntsc.TickTimeNs(60000));
        Assert.Equal(16_683_333, ntsc.TickTimeNs(1));
        Assert.Equal(10L * Nanoseconds.PerSecond, FrameRate.FromHz(120).TickTimeNs(1200));
        Assert.Equal(8_333_333, FrameRate.FromHz(120).TickTimeNs(1));
        Assert.Equal(-8_333_334, FrameRate.FromHz(120).TickTimeNs(-1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8_333_332, 0)]
    [InlineData(8_333_333, 1)]
    [InlineData(16_666_666, 2)]
    [InlineData(-1, -1)]
    public void Tick_index_at_or_before_inverts_tick_time(long time, long index)
    {
        Assert.Equal(index, FrameRate.FromHz(120).TickIndexAtOrBefore(time));
    }

    [Fact]
    public void Period_and_hz_are_consistent()
    {
        var r = FrameRate.FromDouble(59.94);
        Assert.Equal(60000.0 / 1001, r.Hz, 12);
        Assert.Equal(1e9 * 1001 / 60000, r.PeriodNs, 6);
        Assert.Equal(new FrameRate(1_000_000_000, 8_333_312), FrameRate.FromPeriodNs(8_333_312));
        Assert.Equal("59.94", r.ToString());
        Assert.Equal("120", FrameRate.FromHz(120).ToString());
        Assert.False(default(FrameRate).IsValid);
    }
}
