namespace FrameMotionPlus.Core.Statistics;

/// <summary>
/// Exact percentiles over the most recent <see cref="Capacity"/> samples. Keeps a time-ordered ring and a
/// sorted copy; each <see cref="Add"/> is two binary searches and two memmoves of at most the window, and
/// never allocates.
/// </summary>
public sealed class RollingPercentile
{
    private readonly long[] _ring;
    private readonly long[] _sorted;
    private int _count;
    private int _next;
    private long _sum;

    public RollingPercentile(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new long[capacity];
        _sorted = new long[capacity];
    }

    public int Capacity => _ring.Length;

    public int Count => _count;

    public bool IsEmpty => _count == 0;

    public double Mean => _count == 0 ? 0 : (double)_sum / _count;

    public long Min => _count == 0 ? 0 : _sorted[0];

    public long Max => _count == 0 ? 0 : _sorted[_count - 1];

    /// <summary>A sample by age in arrival order: 0 is the newest, <see cref="Count"/> - 1 the oldest.</summary>
    public long Recent(int age)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(age);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(age, _count);
        var index = _next - 1 - age;
        return _ring[index < 0 ? index + _ring.Length : index];
    }

    public void Add(long value)
    {
        if (_count == _ring.Length)
        {
            var old = _ring[_next];
            var removeAt = new ReadOnlySpan<long>(_sorted, 0, _count).BinarySearch(old);
            new Span<long>(_sorted, removeAt + 1, _count - removeAt - 1).CopyTo(new Span<long>(_sorted, removeAt, _count - removeAt - 1));
            _count--;
            _sum -= old;
        }

        _ring[_next] = value;
        _next = _next + 1 == _ring.Length ? 0 : _next + 1;

        var insertAt = new ReadOnlySpan<long>(_sorted, 0, _count).BinarySearch(value);
        if (insertAt < 0)
        {
            insertAt = ~insertAt;
        }

        new Span<long>(_sorted, insertAt, _count - insertAt).CopyTo(new Span<long>(_sorted, insertAt + 1, _count - insertAt));
        _sorted[insertAt] = value;
        _count++;
        _sum += value;
    }

    /// <summary>
    /// Nearest-rank percentile: the smallest sample such that at least <paramref name="fraction"/> of the
    /// window is less than or equal to it. Returns 0 when empty.
    /// </summary>
    public long Percentile(double fraction)
    {
        if (_count == 0)
        {
            return 0;
        }

        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var rank = (int)Math.Ceiling(fraction * _count);
        return _sorted[Math.Clamp(rank - 1, 0, _count - 1)];
    }

    /// <summary>
    /// Nearest-rank percentile over only the samples at or below <paramref name="limit"/> (outliers excluded).
    /// Returns 0 when no sample qualifies.
    /// </summary>
    public long PercentileAtOrBelow(double fraction, long limit)
    {
        var sorted = new ReadOnlySpan<long>(_sorted, 0, _count);
        var upper = sorted.BinarySearch(limit);
        if (upper < 0)
        {
            upper = ~upper;
        }
        else
        {
            while (upper < _count && sorted[upper] == limit)
            {
                upper++;
            }
        }

        if (upper == 0)
        {
            return 0;
        }

        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var rank = (int)Math.Ceiling(fraction * upper);
        return sorted[Math.Clamp(rank - 1, 0, upper - 1)];
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
        _sum = 0;
    }
}
