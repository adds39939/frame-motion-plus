using FrameMotionPlus.Core.Collections;

namespace FrameMotionPlus.Core.Telemetry;

/// <summary>
/// Lock-free hand-off of timeline records from the pipeline thread to a background writer. The pipeline
/// never waits: when the writer falls behind, records are dropped and counted.
/// </summary>
public sealed class TelemetryRing : ITelemetrySink
{
    private readonly SpscRing<FrameTimelineRecord> _ring;
    private long _dropped;

    public TelemetryRing(int capacity)
    {
        _ring = new SpscRing<FrameTimelineRecord>(capacity);
    }

    public int Capacity => _ring.Capacity;

    public int Count => _ring.Count;

    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Producer only.</summary>
    public bool TryWrite(in FrameTimelineRecord record)
    {
        if (_ring.TryEnqueue(record))
        {
            return true;
        }

        Volatile.Write(ref _dropped, _dropped + 1);
        return false;
    }

    /// <summary>Producer only.</summary>
    public void Write(in FrameTimelineRecord record) => TryWrite(record);

    /// <summary>Consumer only.</summary>
    public bool TryRead(out FrameTimelineRecord record) => _ring.TryDequeue(out record);

    /// <summary>Consumer only: writes everything queued to <paramref name="writer"/>.</summary>
    public int DrainTo(TelemetryCsvWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var n = 0;
        while (_ring.TryDequeue(out var record))
        {
            writer.Write(record);
            n++;
        }

        return n;
    }
}
