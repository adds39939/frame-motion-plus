namespace FrameMotionPlus.Cuda;

/// <summary>
/// Measures the GPU time of one span of work on a stream: <see cref="Begin"/> and <see cref="End"/> record timing
/// events around it, and the result is read later without blocking. The span covers the stream's work between the
/// two records (including any wait for other work the GPU interleaves), the same as a VFG run's d.
/// <para>
/// The span starts when the GPU reaches <see cref="Begin"/>. On an idle stream that is immediately, so host time
/// spent enqueueing the work after <see cref="Begin"/> counts too (a few µs per launch; milliseconds for a first,
/// JIT-compiled call). For pure GPU time, record <see cref="Begin"/> behind work that keeps the stream busy, or
/// enqueue everything in the span promptly.
/// </para>
/// <para>Not thread-safe: use one span timer from one thread at a time.</para>
/// </summary>
public sealed class CudaSpanTimer : IDisposable
{
    private readonly CudaEvent _start;
    private readonly CudaEvent _end;
    private bool _begun;
    private bool _ended;

    private CudaSpanTimer(CudaEvent start, CudaEvent end)
    {
        _start = start;
        _end = end;
    }

    public static CudaSpanTimer Create(CudaContext context)
    {
        var start = CudaEvent.CreateForTiming(context);
        try
        {
            return new CudaSpanTimer(start, CudaEvent.CreateForTiming(context));
        }
        catch
        {
            start.Dispose();
            throw;
        }
    }

    public void Begin(CudaStream stream)
    {
        _start.Record(stream);
        _begun = true;
        _ended = false;
    }

    public void End(CudaStream stream)
    {
        if (!_begun)
        {
            throw new InvalidOperationException("End without Begin.");
        }

        _end.Record(stream);
        _ended = true;
    }

    /// <summary>Non-blocking: the span's GPU time once it has finished.</summary>
    public bool TryGetElapsedNanoseconds(out long nanoseconds)
    {
        if (!_ended)
        {
            nanoseconds = 0;
            return false;
        }

        return CudaEvent.TryGetElapsedNanoseconds(_start, _end, out nanoseconds);
    }

    /// <summary>Blocks until the span has finished and returns its GPU time. Not for the hot path.</summary>
    public long WaitElapsedNanoseconds()
    {
        if (!_ended)
        {
            throw new InvalidOperationException("The span has not been ended.");
        }

        _end.Synchronize();
        return CudaEvent.ElapsedNanoseconds(_start, _end);
    }

    public void Dispose()
    {
        _start.Dispose();
        _end.Dispose();
    }
}

/// <summary>
/// Span timers for pipelined work: each <see cref="Begin"/>/<see cref="End"/> pair uses the next of a fixed set of
/// event pairs, and <see cref="TryRead"/> returns finished spans oldest first, never blocking. Allocation-free after
/// creation.
/// <para>
/// Single producer, single consumer: one thread calls <see cref="Begin"/>, <see cref="End"/> and <see cref="Abort"/>,
/// one thread (it may be the same) calls <see cref="TryRead"/>. The writer never touches the reader's position: when
/// the reader is <see cref="Capacity"/> spans behind, a new span is not timed and is counted in <see cref="Dropped"/>
/// (the unread ones are kept).
/// </para>
/// </summary>
public sealed class CudaSpanTimerRing : IDisposable
{
    private readonly CudaEvent[] _starts;
    private readonly CudaEvent[] _ends;

    // Writer-owned: _written (published with release semantics), _dropped, _open, _skipping. Reader-owned: _read.
    private long _written;
    private long _read;
    private long _dropped;
    private bool _open;
    private bool _skipping;

    private CudaSpanTimerRing(CudaEvent[] starts, CudaEvent[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    public int Capacity => _starts.Length;

    /// <summary>Spans not timed because the reader was <see cref="Capacity"/> spans behind.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    public static CudaSpanTimerRing Create(CudaContext context, int capacity = 16)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        var starts = new CudaEvent[capacity];
        var ends = new CudaEvent[capacity];
        try
        {
            for (var i = 0; i < capacity; i++)
            {
                starts[i] = CudaEvent.CreateForTiming(context);
                ends[i] = CudaEvent.CreateForTiming(context);
            }
        }
        catch
        {
            foreach (var e in starts)
            {
                e?.Dispose();
            }

            foreach (var e in ends)
            {
                e?.Dispose();
            }

            throw;
        }

        return new CudaSpanTimerRing(starts, ends);
    }

    /// <summary>Opens a span on <paramref name="stream"/>. Writer thread.</summary>
    public void Begin(CudaStream stream)
    {
        if (_open)
        {
            throw new InvalidOperationException("Begin while a span is open.");
        }

        var written = _written;
        if (written - Volatile.Read(ref _read) >= _starts.Length)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.ThrowIfDisposed();
            _skipping = true;
            _open = true;
            return;
        }

        _starts[(int)(written % _starts.Length)].Record(stream);
        _skipping = false;
        _open = true;
    }

    /// <summary>Closes the open span and publishes it to the reader. Writer thread.</summary>
    public void End(CudaStream stream)
    {
        if (!_open)
        {
            throw new InvalidOperationException("End without Begin.");
        }

        _open = false;
        if (_skipping)
        {
            Volatile.Write(ref _dropped, _dropped + 1);
            return;
        }

        var written = _written;
        _ends[(int)(written % _ends.Length)].Record(stream);
        Volatile.Write(ref _written, written + 1);
    }

    /// <summary>Abandons the open span, if any, without publishing it (the timed work failed). Writer thread.</summary>
    public void Abort() => _open = false;

    /// <summary>Non-blocking: the oldest unread span's GPU time, if it has finished. Reader thread.</summary>
    public bool TryRead(out long nanoseconds)
    {
        var read = _read;
        if (read == Volatile.Read(ref _written))
        {
            nanoseconds = 0;
            return false;
        }

        var index = (int)(read % _starts.Length);
        if (!CudaEvent.TryGetElapsedNanoseconds(_starts[index], _ends[index], out nanoseconds))
        {
            return false;
        }

        Volatile.Write(ref _read, read + 1);
        return true;
    }

    public void Dispose()
    {
        foreach (var e in _starts)
        {
            e.Dispose();
        }

        foreach (var e in _ends)
        {
            e.Dispose();
        }
    }
}
