namespace FrameMotionPlus.Core.Statistics;

/// <summary>
/// Fixed-bin histogram for long runs where an exact rolling window is too small (e.g. p99 latency over a
/// 10 minute simulation). Mean, min and max are exact; percentiles are exact to one bin width.
/// </summary>
public sealed class Histogram
{
    private readonly long[] _bins;
    private readonly long _binWidth;
    private long _overflow;
    private long _underflow;
    private long _count;
    private double _sum;
    private long _min = long.MaxValue;
    private long _max = long.MinValue;

    public Histogram(long binWidth, long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValue, binWidth);
        _binWidth = binWidth;
        _bins = new long[checked((int)((maxValue + binWidth - 1) / binWidth))];
    }

    public long BinWidth => _binWidth;

    public long Count => _count;

    public double Mean => _count == 0 ? 0 : _sum / _count;

    public long Min => _count == 0 ? 0 : _min;

    public long Max => _count == 0 ? 0 : _max;

    public void Add(long value)
    {
        _count++;
        _sum += value;
        _min = Math.Min(_min, value);
        _max = Math.Max(_max, value);
        if (value < 0)
        {
            _underflow++;
            return;
        }

        var bin = value / _binWidth;
        if (bin >= _bins.Length)
        {
            _overflow++;
        }
        else
        {
            _bins[bin]++;
        }
    }

    /// <summary>Upper edge of the bin holding the nearest-rank percentile, clamped to the observed range.</summary>
    public long Percentile(double fraction)
    {
        if (_count == 0)
        {
            return 0;
        }

        var rank = (long)Math.Ceiling(Math.Clamp(fraction, 0.0, 1.0) * _count);
        rank = Math.Max(rank, 1);
        var seen = _underflow;
        if (seen >= rank)
        {
            return _min;
        }

        for (var i = 0; i < _bins.Length; i++)
        {
            seen += _bins[i];
            if (seen >= rank)
            {
                return Math.Clamp(((i + 1) * _binWidth) - 1, _min, _max);
            }
        }

        return _max;
    }

    public void Clear()
    {
        Array.Clear(_bins);
        _overflow = 0;
        _underflow = 0;
        _count = 0;
        _sum = 0;
        _min = long.MaxValue;
        _max = long.MinValue;
    }
}
