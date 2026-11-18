using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Puts CUDA work on <c>CLOCK_MONOTONIC</c>: an anchor event is recorded on a stream of the clock's own that has nothing
/// else on it, and its completion is observed by spinning, which gives it a host time to within a few microseconds; a mark's
/// time is its anchor's host time plus <c>cuEventElapsedTime</c> from the anchor (events of one context compare across
/// streams). The device's event clock runs 10 to 12 µs/s off <c>CLOCK_MONOTONIC</c> here (which NTP slews), and the
/// elapsed time is a float of milliseconds whose resolution falls with distance, so the anchor is taken again from host time
/// every <see cref="RenewEveryNs"/>: the error stays within about 25 µs however long playback runs (chaining anchors by
/// elapsed time instead kept the device clock's drift: 6 to 7 ms after ten minutes). Each mark remembers which anchor it was
/// recorded under. These times are for the statistics and the CSV only; no miss is judged from them (host-clock facts
/// only). Generator thread only; allocation-free after construction.
/// </summary>
internal sealed class GpuClock : IDisposable
{
    public const int MarkCount = 256;
    public const long DefaultRenewEveryNs = 2 * Nanoseconds.PerSecond;
    private const long SpinLimitNs = 2 * Nanoseconds.PerMillisecond;

    private readonly CudaEvent[] _marks = new CudaEvent[MarkCount];
    private readonly int[] _markAnchor = new int[MarkCount];
    private readonly CudaEvent[] _anchors = new CudaEvent[2];
    private readonly long[] _anchorNs = new long[2];
    private readonly CudaStream? _anchorStream;
    private readonly long _renewEveryNs;
    private int _current;
    private long _renewedAtNs;
    private bool _started;

    public GpuClock(CudaContext context, long renewEveryNs = DefaultRenewEveryNs)
    {
        _renewEveryNs = renewEveryNs;
        try
        {
            for (var i = 0; i < MarkCount; i++)
            {
                _marks[i] = CudaEvent.CreateForTiming(context);
            }

            _anchors[0] = CudaEvent.CreateForTiming(context);
            _anchors[1] = CudaEvent.CreateForTiming(context);
            _anchorStream = CudaStream.Create(context);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsStarted => _started;

    public long RenewEveryNs => _renewEveryNs;

    /// <summary>Anchors taken again from host time so far.</summary>
    public long Renewals { get; private set; }

    /// <summary>Takes the first anchor (a drained point, before playback). False if it did not complete within 2 ms.</summary>
    public bool Start(CudaStream stream)
    {
        _ = stream;
        if (!Anchor(0))
        {
            return false;
        }

        _current = 0;
        _started = true;
        return true;
    }

    /// <summary>Records mark <paramref name="index"/> (mod <see cref="MarkCount"/>) on the stream, and now and then a new anchor.</summary>
    public void Mark(CudaStream stream, long index, long nowNs)
    {
        if (_started && nowNs - _renewedAtNs > _renewEveryNs)
        {
            // Taken before the mark, so the mark is recorded under it. A spin of a few microseconds on an idle stream.
            var next = 1 - _current;
            if (Anchor(next))
            {
                _current = next;
                Renewals++;
            }
        }

        var i = (int)(index % MarkCount);
        _marks[i].Record(stream);
        _markAnchor[i] = _current;
    }

    /// <summary>The time mark <paramref name="index"/> completed, once it has; false while it is pending.</summary>
    public bool TryRead(long index, out long timeNs)
    {
        timeNs = 0;
        if (!_started)
        {
            return false;
        }

        var i = (int)(index % MarkCount);
        var a = _markAnchor[i];
        if (!CudaEvent.TryGetElapsedNanoseconds(_anchors[a], _marks[i], out var elapsed))
        {
            return false;
        }

        timeNs = _anchorNs[a] + elapsed;
        return true;
    }

    public void Dispose()
    {
        foreach (var e in _marks)
        {
            e?.Dispose();
        }

        foreach (var e in _anchors)
        {
            e?.Dispose();
        }

        _anchorStream?.Dispose();
    }

    /// <summary>Records anchor <paramref name="slot"/> on the clock's own idle stream and spins until it completes (bounded).</summary>
    private bool Anchor(int slot)
    {
        _anchors[slot].Record(_anchorStream!);
        var start = Monotonic.NowNs;
        while (!_anchors[slot].IsComplete)
        {
            if (Monotonic.NowNs - start > SpinLimitNs)
            {
                _renewedAtNs = Monotonic.NowNs; // try again at the next renewal, not on every mark
                return false;
            }
        }

        var now = Monotonic.NowNs;
        _anchorNs[slot] = now;
        _renewedAtNs = now;
        return true;
    }
}
