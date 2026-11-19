using System.Globalization;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>Which way a periodic beat between the capture rate and the source's goes.</summary>
public enum BeatKind
{
    /// <summary>No periodic beat seen.</summary>
    None = 0,

    /// <summary>The card repeats a picture periodically: it captures faster than the source sends (30 of a 29.97 Hz source).</summary>
    Repeats = 1,

    /// <summary>The card leaves a source frame out periodically: it captures slower than the source sends (59.94 of 60).</summary>
    Skips = 2,
}

/// <summary>What <see cref="RepeatBeatDetector"/> has seen (a copy; read from any thread).</summary>
public readonly record struct RepeatBeat
{
    /// <summary>The rate the card was set to.</summary>
    public FrameRate CaptureRate { get; init; }

    /// <summary>Runs of repeats one longer than the steady cadence (an extra repeat: the card had no new frame).</summary>
    public long ExtraRepeats { get; init; }

    /// <summary>Runs one shorter than a steady cadence of at least one repeat (a source frame the card left out).</summary>
    public long Skips { get; init; }

    public BeatKind Kind { get; init; }

    /// <summary>The beat's period, ns (0: not periodic).</summary>
    public long PeriodNs { get; init; }

    /// <summary>Consecutive events that agree with the period (at least 3 when periodic).</summary>
    public int AgreeingEvents { get; init; }

    /// <summary>
    /// The source's rate the beat implies (the capture rate less one frame per period for repeats, plus one for skips), and
    /// the NTSC or whole-number sibling of the capture rate it matches (within 10% of the expected beat period); invalid
    /// when there is no periodic beat or it matches no sibling (then it is something in the content, not the clocks).
    /// </summary>
    public FrameRate Suggested { get; init; }

    /// <summary>Of <see cref="ExtraRepeats"/> and <see cref="Skips"/>, the events that were part of a periodic beat (three or
    /// more agreeing); the rest are the content's own (a game's dropped frame, the first frames of a still).</summary>
    public long PeriodicEvents { get; init; }

    public bool IsPeriodic => Kind != BeatKind.None && PeriodNs > 0;

    /// <summary>
    /// The beat is the card's own clock against the source's: the implied source rate is within
    /// <see cref="CardClockPpm"/> of the capture rate (a period of minutes: at 29.97 about 35 min for the 16 ppm measured).
    /// The card free-runs on its crystal and does not lock to its input, so this stays whatever rate is asked: nothing to
    /// change.
    /// </summary>
    public bool IsCardClock => IsPeriodic && CaptureRate.IsValid
        && Nanoseconds.PerSecond / (double)PeriodNs / CaptureRate.Hz <= CardClockPpm * 1e-6;

    /// <summary>How far apart two crystals' rates may be and still be the same nominal rate (a card's and a console's).</summary>
    public const double CardClockPpm = 200;

    /// <summary>The implied source rate, Hz (0 when not periodic).</summary>
    public double ImpliedSourceHz => !IsPeriodic || !CaptureRate.IsValid ? 0
        : CaptureRate.Hz + ((Kind == BeatKind.Repeats ? -1 : 1) * Nanoseconds.PerSecond / (double)PeriodNs);

    /// <summary>A rate as <c>--capture-fps</c> takes it: "29.97", "60".</summary>
    public static string RateText(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : rate.Hz.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Watches the capture's repeats for a beat between the card's clock and the source's: a card set to 30.000 fps with a
/// console sending 59.94 Hz (a 30 fps game: 29.97 new pictures a second) has no new frame once every 33.4 s and sends a
/// repeat; at 60.000 of 59.94, once every 16.7 s. Allocation-free, one call per judged frame on the capture thread.
/// <para>
/// The content's own cadence comes first: the run of repeats between two new pictures is steady (0 for a 30 fps game at
/// 30, 1 for a 30 fps game at 60) when one length makes at least 90% of the last 64 runs. A run one longer than that is an
/// extra repeat (the card captured faster than the source); one shorter, when the steady run is at least 1, a frame the card
/// left out (slower). Three or more such events of one kind at intervals that agree (within 3 capture intervals or 2%,
/// between 1 s and an hour apart) are a periodic beat. A card slower than a source whose every frame is new (a 60 fps game)
/// leaves frames out without a trace in the repeats: that beat is not seen here.
/// </para>
/// </summary>
public sealed class RepeatBeatDetector
{
    /// <summary>Runs of repeats remembered for the steady cadence.</summary>
    public const int RunWindow = 64;

    private const int MaxRun = 4;
    private const int Events = 8;
    private const long MinPeriodNs = Nanoseconds.PerSecond;
    private const long MaxPeriodNs = 3600 * Nanoseconds.PerSecond;

    private readonly byte[] _runs = new byte[RunWindow];
    private readonly int[] _runCounts = new int[MaxRun + 2];
    private readonly long[] _extraTimes = new long[Events];
    private readonly long[] _skipTimes = new long[Events];
    private readonly long _frameNs;
    private int _runHead;
    private int _runFill;
    private int _currentRun;
    private bool _haveNew;
    private int _extraCount;
    private int _skipCount;
    private long _extras;
    private long _skips;
    private int _kind;
    private long _periodNs;
    private int _agreeing;
    private long _periodic;
    private long _version;

    public RepeatBeatDetector(FrameRate captureRate)
    {
        CaptureRate = captureRate;
        _frameNs = captureRate.IsValid ? (long)captureRate.PeriodNs : Nanoseconds.PerSecond / 30;
    }

    public FrameRate CaptureRate { get; }

    /// <summary>Changes each time an event is recorded or the beat changes (cheap to poll from another thread).</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>The state now (any thread; fields may be a frame apart).</summary>
    public RepeatBeat Snapshot
    {
        get
        {
            var kind = (BeatKind)Volatile.Read(ref _kind);
            var period = Volatile.Read(ref _periodNs);
            return new RepeatBeat
            {
                CaptureRate = CaptureRate,
                ExtraRepeats = Volatile.Read(ref _extras),
                Skips = Volatile.Read(ref _skips),
                Kind = kind,
                PeriodNs = period,
                AgreeingEvents = Volatile.Read(ref _agreeing),
                PeriodicEvents = Volatile.Read(ref _periodic),
                Suggested = Suggest(CaptureRate, kind, period),
            };
        }
    }

    /// <summary>One judged frame: its capture timestamp and whether it was a new picture.</summary>
    public void Add(long timestampNs, bool isNew)
    {
        if (!isNew)
        {
            _currentRun = Math.Min(_currentRun + 1, 255);
            return;
        }

        if (!_haveNew)
        {
            // The first new picture: the repeats before it (a still start) are no run of the cadence.
            _haveNew = true;
            _currentRun = 0;
            return;
        }

        var run = _currentRun;
        _currentRun = 0;
        if (_runFill >= RunWindow / 2 && Steady(out var steady))
        {
            if (run == steady + 1)
            {
                Volatile.Write(ref _extras, _extras + 1);
                Record(_extraTimes, ref _extraCount, timestampNs, BeatKind.Repeats);
            }
            else if (steady >= 1 && run == steady - 1)
            {
                Volatile.Write(ref _skips, _skips + 1);
                Record(_skipTimes, ref _skipCount, timestampNs, BeatKind.Skips);
            }
        }

        Remember(run);
    }

    /// <summary>
    /// The sibling of <paramref name="capture"/> (30 ↔ 30000/1001, 60 ↔ 60000/1001, 120 ↔ 120000/1001) the beat says the
    /// source runs at, when the beat's period is within 10% of the one the two rates make; invalid otherwise.
    /// </summary>
    public static FrameRate Suggest(FrameRate capture, BeatKind kind, long periodNs)
    {
        if (kind == BeatKind.None || periodNs <= 0 || !capture.IsValid)
        {
            return default;
        }

        FrameRate sibling;
        if (capture.Denominator == 1)
        {
            sibling = new FrameRate(capture.Numerator * 1000, 1001); // a whole rate: the source the NTSC one below it
        }
        else if (capture.Denominator == 1001 && capture.Numerator % 1000 == 0)
        {
            sibling = new FrameRate(capture.Numerator / 1000, 1); // an NTSC rate: the source the whole one above it
        }
        else
        {
            return default;
        }

        // Repeats: the source is slower than the capture; skips: faster.
        if ((kind == BeatKind.Repeats) != (sibling.Hz < capture.Hz))
        {
            return default;
        }

        var expected = Nanoseconds.PerSecond / Math.Abs(capture.Hz - sibling.Hz);
        return Math.Abs(periodNs - expected) <= expected * 0.1 ? sibling : default;
    }

    private void Record(long[] times, ref int count, long timestampNs, BeatKind kind)
    {
        if (count == times.Length)
        {
            Array.Copy(times, 1, times, 0, times.Length - 1);
            count--;
        }

        times[count++] = timestampNs;
        Classify(times, count, kind);

        // Last, so a reader that sees the new version also sees the kind and period written for it.
        Volatile.Write(ref _version, _version + 1);
    }

    private void Classify(long[] times, int count, BeatKind kind)
    {
        // The newest intervals that agree with the newest one: the beat's period is their mean.
        if (count < 3)
        {
            return;
        }

        var newest = times[count - 1] - times[count - 2];
        if (newest < MinPeriodNs || newest > MaxPeriodNs)
        {
            Lose(kind);
            return;
        }

        var tolerance = Math.Max(3 * _frameNs, newest / 50);
        var agreeing = 1;
        var sum = newest;
        for (var i = count - 2; i >= 1; i--)
        {
            var interval = times[i] - times[i - 1];
            if (Math.Abs(interval - newest) > tolerance)
            {
                break;
            }

            agreeing++;
            sum += interval;
        }

        if (agreeing >= 2)
        {
            // This event belongs to the beat; when the beat is new, so do the earlier ones that agree with it.
            var already = (BeatKind)_kind == kind && _periodNs > 0;
            Volatile.Write(ref _periodic, _periodic + (already ? 1 : agreeing + 1));
            Volatile.Write(ref _periodNs, sum / agreeing);
            Volatile.Write(ref _agreeing, agreeing + 1);
            Volatile.Write(ref _kind, (int)kind);
        }
        else
        {
            Lose(kind);
        }
    }

    private void Lose(BeatKind kind)
    {
        if ((BeatKind)_kind == kind)
        {
            Volatile.Write(ref _kind, (int)BeatKind.None);
            Volatile.Write(ref _periodNs, 0);
            Volatile.Write(ref _agreeing, 0);
        }
    }

    private void Remember(int run)
    {
        var bucket = (byte)Math.Min(run, MaxRun + 1);
        if (_runFill == RunWindow)
        {
            _runCounts[_runs[_runHead]]--;
        }
        else
        {
            _runFill++;
        }

        _runs[_runHead] = bucket;
        _runCounts[bucket]++;
        _runHead = (_runHead + 1) % RunWindow;
    }

    /// <summary>The run length that makes at least 90% of the remembered runs, if one does.</summary>
    private bool Steady(out int run)
    {
        for (run = 0; run <= MaxRun; run++)
        {
            if (_runCounts[run] * 10 >= _runFill * 9)
            {
                return true;
            }
        }

        return false;
    }
}
