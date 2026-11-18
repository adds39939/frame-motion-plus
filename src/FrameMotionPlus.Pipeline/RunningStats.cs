using FrameMotionPlus.Core.Statistics;

namespace FrameMotionPlus.Pipeline;

/// <summary>Count, mean, standard deviation, extremes and percentiles of a series, allocation-free after construction.</summary>
public sealed class RunningStats
{
    private readonly Histogram _histogram;
    private double _sum;
    private double _sumSquares;

    /// <param name="binWidthNs">Percentile resolution.</param>
    /// <param name="maxNs">Values above are counted in the last bin (the maximum is still exact).</param>
    public RunningStats(long binWidthNs, long maxNs)
    {
        _histogram = new Histogram(binWidthNs, maxNs);
    }

    public long Count => _histogram.Count;

    public double Mean => Count == 0 ? 0 : _sum / Count;

    public double StdDev
    {
        get
        {
            if (Count < 2)
            {
                return 0;
            }

            var mean = Mean;
            return Math.Sqrt(Math.Max(0, (_sumSquares / Count) - (mean * mean)));
        }
    }

    public long Min => _histogram.Min;

    public long Max => _histogram.Max;

    public long BinWidth => _histogram.BinWidth;

    public void Add(long value)
    {
        _histogram.Add(value);
        _sum += value;
        _sumSquares += (double)value * value;
    }

    /// <summary>Nearest-rank percentile to the histogram's bin width (the bin's upper edge, clamped to the maximum).</summary>
    public long Percentile(double fraction) => Count == 0 ? 0 : Math.Min(_histogram.Percentile(fraction), Max);

    public void Clear()
    {
        _histogram.Clear();
        _sum = 0;
        _sumSquares = 0;
    }
}
