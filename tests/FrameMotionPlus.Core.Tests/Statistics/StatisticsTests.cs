using FrameMotionPlus.Core.Statistics;

namespace FrameMotionPlus.Core.Tests.Statistics;

public class StatisticsTests
{
    private static long NaivePercentile(IReadOnlyList<long> window, double fraction)
    {
        var sorted = window.Order().ToArray();
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    [Fact]
    public void Rolling_percentile_matches_a_naive_sort_of_the_window()
    {
        var random = new DeterministicRandom(11);
        var p = new RollingPercentile(37);
        var history = new List<long>();
        for (var i = 0; i < 5000; i++)
        {
            // Many duplicates exercise removal of equal values.
            var v = (long)(random.NextDouble() * 50) * 1000;
            p.Add(v);
            history.Add(v);
            var window = history.Skip(Math.Max(0, history.Count - 37)).ToList();
            foreach (var f in new[] { 0.0, 0.01, 0.5, 0.9, 0.99, 1.0 })
            {
                Assert.Equal(NaivePercentile(window, f), p.Percentile(f));
            }

            Assert.Equal(window.Count, p.Count);
            Assert.Equal(window.Min(), p.Min);
            Assert.Equal(window.Max(), p.Max);
            Assert.Equal(window.Average(), p.Mean, 6);
        }
    }

    [Fact]
    public void P99_of_one_to_one_hundred_is_ninety_nine()
    {
        var p = new RollingPercentile(100);
        for (var i = 1; i <= 100; i++)
        {
            p.Add(i);
        }

        Assert.Equal(99, p.Percentile(0.99));
        Assert.Equal(50, p.Percentile(0.5));
        Assert.Equal(100, p.Percentile(1.0));
        Assert.Equal(1, p.Percentile(0));
    }

    [Fact]
    public void Old_samples_age_out_of_the_window()
    {
        var p = new RollingPercentile(10);
        for (var i = 0; i < 10; i++)
        {
            p.Add(1_000);
        }

        Assert.Equal(1_000, p.Percentile(0.99));
        for (var i = 0; i < 10; i++)
        {
            p.Add(5);
        }

        Assert.Equal(5, p.Percentile(0.99));
        Assert.Equal(5, p.Max);
    }

    [Fact]
    public void Percentile_at_or_below_excludes_outliers()
    {
        var p = new RollingPercentile(16);
        foreach (var v in new long[] { 10, 10, 10, 11, 12, 10, 50, 10, 60, 10 })
        {
            p.Add(v);
        }

        Assert.Equal(50, p.Percentile(0.9));
        Assert.Equal(12, p.PercentileAtOrBelow(0.9, 20));
        Assert.Equal(12, p.PercentileAtOrBelow(1.0, 12));
        Assert.Equal(0, p.PercentileAtOrBelow(0.9, 5));
    }

    [Fact]
    public void Empty_and_cleared_windows_report_zero()
    {
        var p = new RollingPercentile(4);
        Assert.Equal(0, p.Percentile(0.99));
        Assert.Equal(0, p.Mean);
        p.Add(7);
        p.Clear();
        Assert.True(p.IsEmpty);
        Assert.Equal(0, p.Percentile(0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingPercentile(0));
    }

    [Fact]
    public void Histogram_percentiles_are_exact_to_one_bin()
    {
        var h = new Histogram(10, 10_000);
        for (var i = 1; i <= 1000; i++)
        {
            h.Add(i);
        }

        Assert.Equal(500.5, h.Mean, 9);
        Assert.Equal(1, h.Min);
        Assert.Equal(1000, h.Max);
        Assert.InRange(h.Percentile(0.99), 990, 999);
        Assert.InRange(h.Percentile(0.5), 500, 509);

        h.Add(1_000_000);
        Assert.Equal(1_000_000, h.Percentile(1.0));
        h.Clear();
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Deterministic_random_is_reproducible_and_in_range()
    {
        var a = new DeterministicRandom(5);
        var b = new DeterministicRandom(5);
        for (var i = 0; i < 1000; i++)
        {
            var x = a.NextDouble();
            Assert.Equal(x, b.NextDouble());
            Assert.InRange(x, 0.0, 1.0 - double.Epsilon);
            var s = a.NextSymmetric(100);
            Assert.Equal(s, b.NextSymmetric(100));
            Assert.InRange(s, -100, 100);
        }
    }
}
