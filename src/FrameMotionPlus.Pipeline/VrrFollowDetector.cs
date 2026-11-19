using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>A stretch of a variable-refresh run in which the display stopped following the presents.</summary>
public readonly record struct VrrLossEpisode(long StartNs, long EndNs)
{
    public long DurationNs => EndNs - StartNs;
}

/// <summary>What <see cref="VrrFollowDetector"/> found over a run.</summary>
public sealed record VrrFollowSummary
{
    /// <summary>Episodes in which the display showed on its refresh grid instead of following (an open one closed at the end).</summary>
    public int Episodes { get; init; }

    public long TotalNs { get; init; }

    public long LongestNs { get; init; }

    /// <summary>Late shows (first pixel more than half the shortest refresh period after the slot's time) inside episodes and outside.</summary>
    public long LateInside { get; init; }

    public long LateOutside { get; init; }

    /// <summary>Timed shows judged.</summary>
    public long Shows { get; init; }

    /// <summary>The first <see cref="VrrFollowDetector.LogCapacity"/> episodes.</summary>
    public IReadOnlyList<VrrLossEpisode> Log { get; init; } = [];

    /// <summary>"lost 2x for a total of 3.1 s (the longest 2.0 s)", or "never lost".</summary>
    public string Describe() => Episodes == 0
        ? "never lost"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"lost {Episodes}x for a total of {TotalNs / 1e9:0.0} s (the longest {LongestNs / 1e9:0.0} s)");
}

/// <summary>
/// Watches a variable-refresh run for stretches where the display stops following the presents and shows on its refresh
/// grid instead (KWin drops variable refresh for a fullscreen window when something covers it, focus moves, an overlay
/// appears): the app keeps handing presents over at their own times and the first pixels snap to the next refresh, so
/// many come late while the intervals look even. The same criterion as the start-up probe, made sustained: over the last
/// <see cref="Window"/> timed shows, at least half came more than <c>max(1 ms, a quarter of the refresh period)</c> later
/// after their hand-over than the delay measured while the display followed (the probe's), and at least 60% of the pairs of
/// consecutive shows were a whole number of refreshes apart. Under variable refresh the first pixel comes the display's
/// delay (about 0.9 ms) after the hand-over; on the grid it comes anywhere up to a refresh later, so a drifting phase puts
/// most shows over the threshold, and the pairs land on the grid (a display that follows with a delay varying by
/// milliseconds puts few pairs a whole number of refreshes apart, so the two together do not mistake it for the grid).
/// The episode ends, back-dated to its last show late after hand-over, once <c>2 x Window</c> shows in a row have not been
/// (about 2 s at 60 Hz): on a grid whose phase drifts against the slots (30.000 → 60 on 119.88 Hz) the shows come on time
/// for a second or so of every cycle, and that would otherwise split one loss into two.
/// (A grid whose phase happens to sit within the threshold of the delay is not seen; its shows are on time.)
/// Allocation-free; one thread (the present thread live, or <c>fmp verify</c> over a CSV).
/// </summary>
public sealed class VrrFollowDetector
{
    /// <summary>Timed shows the decision looks back over (about a second at 60 Hz).</summary>
    public const int Window = 60;

    /// <summary>Episodes kept for the summary (all are counted).</summary>
    public const int LogCapacity = 64;

    private const long GridToleranceNs = 250 * Nanoseconds.PerMicrosecond;

    private readonly byte[] _flags = new byte[Window];
    private readonly long[] _shown = new long[Window];
    private readonly VrrLossEpisode[] _log = new VrrLossEpisode[LogCapacity];
    private int _head;
    private int _fill;
    private int _off;
    private int _onGrid;
    private int _late;
    private long _lastShownNs = long.MinValue;
    private long _episodeStartNs = long.MinValue;
    private long _lastOffNs;
    private int _quiet;
    private int _episodes;
    private int _logged;
    private long _totalNs;
    private long _longestNs;
    private long _lateInside;
    private long _lateOutside;
    private long _shows;
    private long _version;

    private const byte Off = 1;
    private const byte Grid = 2;
    private const byte Late = 4;

    /// <param name="followDelayNs">Hand-over to first pixel while the display follows (the probe's median).</param>
    public VrrFollowDetector(long followDelayNs)
    {
        FollowDelayNs = followDelayNs > 0 ? followDelayNs : 750 * Nanoseconds.PerMicrosecond;
    }

    public long FollowDelayNs { get; }

    /// <summary>True while the display is not following (any thread).</summary>
    public bool IsLost => Volatile.Read(ref _episodeStartNs) != long.MinValue;

    /// <summary>Changes when an episode starts or ends (any thread).</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Episodes so far, counting an open one (any thread).</summary>
    public int Episodes => Volatile.Read(ref _episodes);

    /// <summary>The open episode's start, or the last one's (any thread).</summary>
    public long EpisodeStartNs => Volatile.Read(ref _episodeStartNs);

    /// <summary>The last closed episode's duration (any thread).</summary>
    public long LastDurationNs { get; private set; }

    /// <summary>
    /// One timed show: when it was handed over and when its first pixel came, the output's shortest refresh period, and
    /// whether it was late (first pixel more than half that period after the slot's time).
    /// </summary>
    public void OnShown(long handoverNs, long shownNs, double periodNs, bool late)
    {
        _shows++;
        var threshold = Math.Max(Nanoseconds.PerMillisecond, (long)(periodNs / 4));
        byte flags = 0;
        if (shownNs - handoverNs - FollowDelayNs > threshold)
        {
            flags |= Off;
        }

        if (_lastShownNs != long.MinValue && periodNs > 0)
        {
            var periods = (shownNs - _lastShownNs) / periodNs;
            if (periods >= 0.5 && Math.Abs(periods - Math.Round(periods)) * periodNs < GridToleranceNs)
            {
                flags |= Grid;
            }
        }

        _lastShownNs = shownNs;
        if (late)
        {
            flags |= Late;
            if (_episodeStartNs != long.MinValue)
            {
                _lateInside++;
            }
            else
            {
                _lateOutside++;
            }
        }

        if (_fill == Window)
        {
            Forget(_flags[_head]);
        }
        else
        {
            _fill++;
        }

        _flags[_head] = flags;
        _shown[_head] = shownNs;
        _head = (_head + 1) % Window;
        _off += (flags & Off) != 0 ? 1 : 0;
        _onGrid += (flags & Grid) != 0 ? 1 : 0;
        _late += (flags & Late) != 0 ? 1 : 0;
        if ((flags & Off) != 0)
        {
            _lastOffNs = shownNs;
            _quiet = 0;
        }
        else
        {
            _quiet++;
        }

        if (_episodeStartNs == long.MinValue)
        {
            if (_fill == Window && _off * 2 >= Window && _onGrid * 10 >= Window * 6)
            {
                Begin();
            }
        }
        else if (_off * 5 <= Window && _quiet >= 2 * Window)
        {
            End(_lastOffNs);
        }
    }

    /// <summary>Closes an open episode at <paramref name="endNs"/> (the run ended) and returns the totals.</summary>
    public VrrFollowSummary Finish(long endNs)
    {
        if (_episodeStartNs != long.MinValue)
        {
            End(Math.Max(endNs, _episodeStartNs));
        }

        return new VrrFollowSummary
        {
            Episodes = _episodes,
            TotalNs = _totalNs,
            LongestNs = _longestNs,
            LateInside = _lateInside,
            LateOutside = _lateOutside,
            Shows = _shows,
            Log = _log.AsSpan(0, _logged).ToArray(),
        };
    }

    private void Forget(byte flags)
    {
        _off -= (flags & Off) != 0 ? 1 : 0;
        _onGrid -= (flags & Grid) != 0 ? 1 : 0;
        _late -= (flags & Late) != 0 ? 1 : 0;
    }

    /// <summary>The episode starts at the window's first show that came late after its hand-over; the late shows in the
    /// window from there on move inside (they were counted before the decision was made).</summary>
    private void Begin()
    {
        var start = long.MinValue;
        var moved = 0;
        for (var i = 0; i < Window; i++)
        {
            var j = (_head + i) % Window;
            if (start == long.MinValue && (_flags[j] & Off) != 0)
            {
                start = _shown[j];
            }

            if (start != long.MinValue && (_flags[j] & Late) != 0)
            {
                moved++;
            }
        }

        _lateOutside -= moved;
        _lateInside += moved;
        Volatile.Write(ref _episodes, _episodes + 1);
        Volatile.Write(ref _episodeStartNs, start == long.MinValue ? _lastShownNs : start);
        Volatile.Write(ref _version, _version + 1);
    }

    private void End(long endNs)
    {
        var start = _episodeStartNs;
        var duration = Math.Max(0, endNs - start);
        _totalNs += duration;
        _longestNs = Math.Max(_longestNs, duration);
        if (_logged < LogCapacity)
        {
            _log[_logged++] = new VrrLossEpisode(start, start + duration);
        }

        LastDurationNs = duration;
        Volatile.Write(ref _episodeStartNs, long.MinValue);
        Volatile.Write(ref _version, _version + 1);
    }
}
