using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File;

/// <summary>
/// Turns presentation timestamps (stream time base) into content times in nanoseconds that are distinct and strictly
/// increasing per delivered frame, across a missing PTS, a non-zero start time, loops, seeks and timestamp
/// discontinuities in the stream. Frames arrive here in presentation order (the decoder reorders B-frames), each with
/// FFmpeg's best-effort timestamp.
/// <list type="bullet">
/// <item>Media time is the timestamp minus the stream's start (its <c>start_time</c>, else the first frame's), scaled
/// exactly (128-bit) from the time base.</item>
/// <item>A frame without any timestamp is placed one frame duration after the previous one (its own duration, else the
/// previous one's, else the nominal interval); after a restart from the beginning of the file, at the start.</item>
/// <item>After a loop or a seek (<see cref="Rebase"/>) the next frame continues one interval after the last delivered
/// one, whatever its media time, so time never steps back.</item>
/// <item>Within a lap, a timestamp that does not advance is one of two things. An <b>isolated collision</b> (a broken
/// file: the frame is less than one frame interval behind the previous one, and the previous one was not itself
/// repaired) is moved to 1 ns after the previous frame and counted (<see cref="Repairs"/>). Anything else (a step back of
/// a frame interval or more, or a second frame in a row that does not advance) is a <b>discontinuity</b> in the stream's
/// timestamps (concatenated files, a reset or wrapped clock): the frame continues one interval after the previous one,
/// as after a seek, and is reported (<see cref="Discontinuities"/>; the source marks it). So repairs never form runs that
/// would squeeze many frames into nanoseconds.</item>
/// <item>A step forward of more than <see cref="MaxForwardStepNs"/> is a discontinuity too (reported and marked: nothing
/// may interpolate across it). Its time is kept where the container's timeline is continuous (MP4, Matroska: a 12 s
/// still is content, held for 12 s), and collapsed to one interval only where the container's clock may legitimately
/// jump (<c>collapseForwardJumps</c>: libavformat's <c>AVFMT_TS_DISCONT</c>, MPEG-TS and MPEG-PS).</item>
/// </list>
/// Allocation-free; one thread.
/// </summary>
internal sealed class ContentClock
{
    /// <summary>
    /// A step forward between consecutive frames larger than this is a timestamp discontinuity, not a long frame:
    /// 10 s, the default of FFmpeg's own <c>dts_delta_threshold</c> for formats with timestamp discontinuities.
    /// </summary>
    public const long MaxForwardStepNs = 10_000_000_000;

    private readonly long _timeBaseNum;
    private readonly long _timeBaseDen;
    private readonly long _nominalIntervalNs;
    private readonly long _origin;
    private long _startTicks;
    private bool _haveStart;
    private long _offsetNs;
    private long _lastContentNs = long.MinValue;
    private long _lastTicks = Av.NoPtsValue;
    private long _lastDurationNs;
    private readonly bool _collapseForwardJumps;
    private bool _repairedLast;
    private bool _rebase;

    public ContentClock(int timeBaseNum, int timeBaseDen, long streamStartTicks, long nominalIntervalNs, long originNs, bool collapseForwardJumps)
    {
        if (timeBaseNum <= 0 || timeBaseDen <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBaseDen), $"Invalid stream time base {timeBaseNum}/{timeBaseDen}.");
        }

        _timeBaseNum = timeBaseNum;
        _timeBaseDen = timeBaseDen;
        _nominalIntervalNs = Math.Max(1, nominalIntervalNs);
        _origin = originNs;
        _collapseForwardJumps = collapseForwardJumps;
        if (streamStartTicks != Av.NoPtsValue)
        {
            _startTicks = streamStartTicks;
            _haveStart = true;
        }
    }

    /// <summary>Isolated collisions: frames whose timestamp did not advance and were moved 1 ns after the previous one.</summary>
    public long Repairs { get; private set; }

    /// <summary>Frames that had no timestamp at all.</summary>
    public long Missing { get; private set; }

    /// <summary>Timestamp discontinuities in the stream (a jump back, a run of non-advancing timestamps, a jump forward).</summary>
    public long Discontinuities { get; private set; }

    public long LastContentNs => _lastContentNs;

    /// <summary>Whether the last frame's timestamp was an isolated collision moved 1 ns on.</summary>
    public bool LastWasRepaired => _repairedLast;

    /// <summary>Stream ticks of <paramref name="mediaNs"/> after the start (for seeking).</summary>
    public long TicksAt(long mediaNs) =>
        (long)((((Int128)mediaNs * _timeBaseDen) / ((Int128)1_000_000_000 * _timeBaseNum)) + _startTicks);

    /// <summary>Media time of stream ticks: nanoseconds after the start.</summary>
    public long MediaNs(long ticks) => (long)(((Int128)(ticks - _startTicks) * _timeBaseNum * 1_000_000_000) / _timeBaseDen);

    /// <summary>Scales a duration in stream ticks to nanoseconds (0 for none).</summary>
    public long DurationNs(long ticks) => ticks <= 0 ? 0 : (long)(((Int128)ticks * _timeBaseNum * 1_000_000_000) / _timeBaseDen);

    /// <summary>
    /// The next frame continues one interval after the last delivered one (a loop or a seek).
    /// <paramref name="fromStart"/>: the decoder restarted at the beginning of the file, so frames without timestamps
    /// count again from the start.
    /// </summary>
    public void Rebase(bool fromStart)
    {
        _rebase = true;
        _repairedLast = false;
        if (fromStart)
        {
            _lastTicks = Av.NoPtsValue;
        }
    }

    /// <summary>The media time the next frame would have (<see cref="Next"/>'s), without delivering it.</summary>
    public long Peek(long ticks, long durationTicks)
    {
        var resolved = Resolve(ticks, Interval(DurationNs(durationTicks)));
        return _haveStart ? MediaNs(resolved) : 0;
    }

    /// <summary>A frame decoded but not delivered (before a seek target): only the place of the next untimed frame moves.</summary>
    public void Skip(long ticks, long durationTicks)
    {
        var durationNs = DurationNs(durationTicks);
        var interval = Interval(durationNs);
        if (ticks == Av.NoPtsValue)
        {
            Missing++;
        }

        ticks = Resolve(ticks, interval);
        if (!_haveStart)
        {
            _startTicks = ticks;
            _haveStart = true;
        }

        _lastTicks = ticks;
        _lastDurationNs = durationNs > 0 ? durationNs : interval;
    }

    /// <summary>
    /// The content time of the next frame in presentation order, from its best-effort timestamp and duration (stream
    /// ticks; <see cref="Av.NoPtsValue"/> / 0 when unknown). Also returns its media time (ns after the stream's start) and
    /// whether its timestamp is a discontinuity in the stream (see the class remarks).
    /// </summary>
    public long Next(long ticks, long durationTicks, out long mediaNs, out bool discontinuity)
    {
        var durationNs = DurationNs(durationTicks);
        var interval = Interval(durationNs);
        if (ticks == Av.NoPtsValue)
        {
            Missing++;
        }

        ticks = Resolve(ticks, interval);
        if (!_haveStart)
        {
            _startTicks = ticks;
            _haveStart = true;
        }

        mediaNs = MediaNs(ticks);
        discontinuity = false;
        var repaired = false;
        long content;
        if (_lastContentNs == long.MinValue)
        {
            content = _origin + _offsetNs + mediaNs;
        }
        else
        {
            // The previous frame's interval: where this frame belongs after a rebase or a discontinuity, and how far
            // behind the previous frame an isolated collision may be.
            var step = _lastDurationNs > 0 ? _lastDurationNs : _nominalIntervalNs;
            var jump = _rebase;
            content = _origin + _offsetNs + mediaNs;
            if (!jump)
            {
                var ahead = content - _lastContentNs;
                if (ahead > MaxForwardStepNs)
                {
                    // Marked either way; the gap is kept unless the container's clock may jump.
                    jump = _collapseForwardJumps;
                    discontinuity = true;
                    Discontinuities++;
                }
                else if (ahead <= 0 && (-ahead >= step || _repairedLast))
                {
                    jump = true;
                    discontinuity = true;
                    Discontinuities++;
                }
                else if (ahead <= 0)
                {
                    Repairs++;
                    repaired = true;
                    content = _lastContentNs + 1;
                }
            }

            if (jump)
            {
                _offsetNs = _lastContentNs + step - mediaNs - _origin;
                content = _lastContentNs + step;
            }
        }

        _rebase = false;
        _repairedLast = repaired;
        _lastContentNs = content;
        _lastTicks = ticks;
        _lastDurationNs = durationNs > 0 ? durationNs : interval;
        return content;
    }

    private long Interval(long durationNs) => durationNs > 0 ? durationNs : _lastDurationNs > 0 ? _lastDurationNs : _nominalIntervalNs;

    /// <summary>The frame's ticks, or for a frame without a timestamp, one interval after the previous frame (else the start).</summary>
    private long Resolve(long ticks, long intervalNs)
    {
        if (ticks != Av.NoPtsValue)
        {
            return ticks;
        }

        // The interval in ticks, rounded to the nearest (a nominal 1/30 s is 33,333,333 ns: 2999.99997 ticks of 1/90000).
        var nsPerTickScaled = (Int128)1_000_000_000 * _timeBaseNum;
        return _lastTicks == Av.NoPtsValue
            ? (_haveStart ? _startTicks : 0)
            : _lastTicks + Math.Max(1, (long)((((Int128)intervalNs * _timeBaseDen) + (nsPerTickScaled / 2)) / nsPerTickScaled));
    }
}
