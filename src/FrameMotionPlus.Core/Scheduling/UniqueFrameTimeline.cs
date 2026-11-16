namespace FrameMotionPlus.Core.Scheduling;

[Flags]
public enum TimelineMarks
{
    None = 0,

    /// <summary>The gap before this frame exceeded the hold threshold.</summary>
    HoldBefore = 1 << 0,

    /// <summary>The source reported a discontinuity before this frame.</summary>
    DiscontinuityBefore = 1 << 1,
}

public readonly record struct TimelineFrame(long FrameId, long ContentTimeNs, long ArrivalTimeNs, TimelineMarks Marks)
{
    /// <summary>True when interpolation between the previous frame and this one is not allowed.</summary>
    public bool BreaksContinuity => Marks != TimelineMarks.None;
}

/// <summary>
/// Recent unique real frames in strictly increasing content-time order, addressed by a monotonically
/// increasing sequence number. Fixed capacity; the oldest entries are overwritten.
/// </summary>
public sealed class UniqueFrameTimeline
{
    private readonly TimelineFrame[] _frames;
    private readonly long _mask;
    private long _next;
    private long _first;

    public UniqueFrameTimeline(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        if ((capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two.");
        }

        _frames = new TimelineFrame[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _frames.Length;

    /// <summary>Sequence number of the oldest retained frame.</summary>
    public long FirstSequence => Math.Max(_first, _next - _frames.Length);

    /// <summary>Sequence number of the newest frame, or <see cref="FirstSequence"/> − 1 when empty.</summary>
    public long LastSequence => _next - 1;

    public int Count => (int)(_next - FirstSequence);

    public bool IsEmpty => Count == 0;

    public ref readonly TimelineFrame this[long sequence]
    {
        get
        {
            if (sequence < FirstSequence || sequence > LastSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence is not retained.");
            }

            return ref _frames[sequence & _mask];
        }
    }

    public ref readonly TimelineFrame Newest => ref this[LastSequence];

    public bool Contains(long sequence) => sequence >= FirstSequence && sequence <= LastSequence;

    /// <summary>Appends a frame. The caller guarantees content time increases strictly.</summary>
    public long Add(in TimelineFrame frame)
    {
        _frames[_next & _mask] = frame;
        return _next++;
    }

    /// <summary>
    /// Sequence of the newest frame whose content time is at or before <paramref name="contentTimeNs"/>,
    /// or −1 if every retained frame is later. Starts from <paramref name="hint"/>; amortised O(1) when the
    /// query time only moves forward, as display-time scheduling does.
    /// </summary>
    public long FindAtOrBefore(long contentTimeNs, long hint)
    {
        var first = FirstSequence;
        var last = LastSequence;
        if (last < first)
        {
            return -1;
        }

        var s = Math.Clamp(hint, first, last);
        if (_frames[s & _mask].ContentTimeNs > contentTimeNs)
        {
            while (s >= first && _frames[s & _mask].ContentTimeNs > contentTimeNs)
            {
                s--;
            }

            return s < first ? -1 : s;
        }

        while (s < last && _frames[(s + 1) & _mask].ContentTimeNs <= contentTimeNs)
        {
            s++;
        }

        return s;
    }

    /// <summary>Forgets all frames. Sequence numbers keep increasing.</summary>
    public void Clear() => _first = _next;
}
