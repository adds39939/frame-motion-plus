namespace FrameMotionPlus.Core.Scheduling;

/// <summary>
/// Exact percentiles over at most <c>capacity</c> recent samples that can also be expired by time. Samples must be
/// added in non-decreasing time order. Allocation-free after construction.
/// </summary>
internal sealed class AgedPercentile
{
    private readonly long[] _times;
    private readonly long[] _values;
    private readonly long[] _sorted;
    private int _head;
    private int _count;
    private long _sum;

    public AgedPercentile(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _times = new long[capacity];
        _values = new long[capacity];
        _sorted = new long[capacity];
    }

    public int Count => _count;

    public double Mean => _count == 0 ? 0 : (double)_sum / _count;

    public void Add(long timeNs, long value)
    {
        if (_count == _values.Length)
        {
            RemoveOldest();
        }

        var tail = (_head + _count) % _values.Length;
        _times[tail] = timeNs;
        _values[tail] = value;
        var at = new ReadOnlySpan<long>(_sorted, 0, _count).BinarySearch(value);
        if (at < 0)
        {
            at = ~at;
        }

        new Span<long>(_sorted, at, _count - at).CopyTo(new Span<long>(_sorted, at + 1, _count - at));
        _sorted[at] = value;
        _count++;
        _sum += value;
    }

    public void RemoveOlderThan(long timeNs)
    {
        while (_count > 0 && _times[_head] < timeNs)
        {
            RemoveOldest();
        }
    }

    /// <summary>Nearest-rank percentile; 0 when empty.</summary>
    public long Percentile(double fraction)
    {
        if (_count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(Math.Clamp(fraction, 0.0, 1.0) * _count);
        return _sorted[Math.Clamp(rank - 1, 0, _count - 1)];
    }

    /// <summary>Median of the three most recent samples (fewer if fewer exist); 0 when empty.</summary>
    public long RecentMedianOfThree()
    {
        if (_count == 0)
        {
            return 0;
        }

        var cap = _values.Length;
        var a = _values[(_head + _count - 1) % cap];
        if (_count < 3)
        {
            return _count == 1 ? a : Math.Min(a, _values[(_head + _count - 2) % cap]);
        }

        var b = _values[(_head + _count - 2) % cap];
        var c = _values[(_head + _count - 3) % cap];
        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _sum = 0;
    }

    private void RemoveOldest()
    {
        var old = _values[_head];
        var at = new ReadOnlySpan<long>(_sorted, 0, _count).BinarySearch(old);
        new Span<long>(_sorted, at + 1, _count - at - 1).CopyTo(new Span<long>(_sorted, at, _count - at - 1));
        _head = (_head + 1) % _values.Length;
        _count--;
        _sum -= old;
    }
}
