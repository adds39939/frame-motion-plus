using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Simulation;

/// <summary>A stretch of content time during which the simulated source produces no new frames.</summary>
public readonly record struct SimulatedHold(long StartNs, long DurationNs);

public sealed record SimulatedSourceOptions
{
    /// <summary>Unique-frame rate (the game's rate, after cadence detection).</summary>
    public FrameRate Rate { get; init; } = FrameRate.FromHz(60);

    /// <summary>Content time of frame 0; sets the source phase against the vblank grid.</summary>
    public long StartNs { get; init; }

    /// <summary>Delay from content time to availability on the GPU (capture, upload, convert).</summary>
    public long IngestLatencyNs { get; init; }

    /// <summary>Extra uniform [0, J] delay added to each frame's availability (USB and driver jitter).</summary>
    public long IngestJitterNs { get; init; }

    /// <summary>Probability that a frame is dropped (the game misses a refresh).</summary>
    public double DropProbability { get; init; }

    /// <summary>Uniform ± jitter on content timestamps (uneven game frame times). Must stay below T/2.</summary>
    public long TimestampJitterNs { get; init; }

    /// <summary>Source clock error versus the display clock, in ppm (crystal skew between PS5 and panel).</summary>
    public double ClockSkewPpm { get; init; }

    /// <summary>From the first tick at or after this content time, the source runs at <see cref="RateAfterChange"/>.</summary>
    public long RateChangeAtNs { get; init; } = long.MaxValue;

    public FrameRate RateAfterChange { get; init; }

    /// <summary>When non-empty, after <see cref="RateChangeAtNs"/> frames advance by these numbers of ticks of the
    /// original rate in turn (e.g. 3, 4: an alternating two-valued cadence) instead of using <see cref="RateAfterChange"/>.</summary>
    public ReadOnlyMemory<int> CadenceAfterChange { get; init; }

    public ReadOnlyMemory<SimulatedHold> Holds { get; init; }

    /// <summary>The first frame at or after this content time carries a source discontinuity (cut, seek, switch).</summary>
    public long DiscontinuityAtNs { get; init; } = long.MaxValue;

    /// <summary>When non-empty, these content timestamps are used verbatim instead of the rate.</summary>
    public ReadOnlyMemory<long> ExplicitTimestamps { get; init; }

    public ulong Seed { get; init; } = 1;
}

/// <summary>Deterministic generator of unique-frame arrivals for the scheduler.</summary>
public sealed class SimulatedSource
{
    private readonly SimulatedSourceOptions _options;
    private readonly DeterministicRandom _random;
    private readonly double _skew;
    private long _tick;
    private long _nextId;
    private long _lastContentNs = long.MinValue;
    private long _lastArrivalNs = long.MinValue;
    private bool _changed;
    private long _segmentOriginNs;
    private long _lastNominalNs;
    private long _tick0;
    private bool _discontinuitySent;
    private long _cadenceTicks;

    public SimulatedSource(SimulatedSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExplicitTimestamps.IsEmpty)
        {
            if (!options.Rate.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Source rate must be set.");
            }

            if (options.RateChangeAtNs != long.MaxValue && !options.RateAfterChange.IsValid && options.CadenceAfterChange.IsEmpty)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RateAfterChange or CadenceAfterChange must be set with RateChangeAtNs.");
            }

            var fastest = options.RateChangeAtNs == long.MaxValue || !options.RateAfterChange.IsValid
                ? options.Rate.PeriodNs
                : Math.Min(options.Rate.PeriodNs, options.RateAfterChange.PeriodNs);
            if (options.TimestampJitterNs * 2 >= fastest)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Timestamp jitter must stay below half the frame interval.");
            }
        }

        if (!(options.DropProbability >= 0 && options.DropProbability < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Drop probability must be in [0, 1).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.IngestJitterNs);
        _options = options;
        _random = new DeterministicRandom(options.Seed);
        _skew = 1.0 + (options.ClockSkewPpm * 1e-6);
    }

    public long Delivered { get; private set; }

    public long Dropped { get; private set; }

    public long HeldBack { get; private set; }

    public bool TryNext(out FrameArrival frame)
    {
        var explicitTimes = _options.ExplicitTimestamps.Span;
        if (!explicitTimes.IsEmpty)
        {
            if (_tick >= explicitTimes.Length)
            {
                frame = default;
                return false;
            }

            var ts = explicitTimes[(int)_tick++];
            return Deliver(ts, out frame);
        }

        while (true)
        {
            var tick = _tick++;
            var nominal = NextNominal(tick);
            var ts = nominal + _random.NextSymmetric(_options.TimestampJitterNs);
            var dropped = _options.DropProbability > 0 && _random.NextDouble() < _options.DropProbability;
            if (InHold(nominal))
            {
                HeldBack++;
                continue;
            }

            if (dropped && tick > 0)
            {
                Dropped++;
                continue;
            }

            return Deliver(ts, out frame);
        }
    }

    private long NextNominal(long tick)
    {
        if (_changed)
        {
            var cadence = _options.CadenceAfterChange.Span;
            if (!cadence.IsEmpty)
            {
                _cadenceTicks += cadence[(int)((tick - _tick0 - 1) % cadence.Length)];
                _lastNominalNs = _segmentOriginNs + (long)Math.Round(_options.Rate.TickTimeNs(_cadenceTicks) * _skew);
                return _lastNominalNs;
            }

            _lastNominalNs = _segmentOriginNs + (long)Math.Round(_options.RateAfterChange.TickTimeNs(tick - _tick0) * _skew);
            return _lastNominalNs;
        }

        var nominal = _options.StartNs + (long)Math.Round(_options.Rate.TickTimeNs(tick) * _skew);
        if (nominal >= _options.RateChangeAtNs && tick > 0)
        {
            _changed = true;
            _segmentOriginNs = _lastNominalNs;
            _tick0 = tick - 1;
            return NextNominal(tick);
        }

        _lastNominalNs = nominal;
        return nominal;
    }

    private bool Deliver(long contentNs, out FrameArrival frame)
    {
        if (contentNs <= _lastContentNs)
        {
            throw new InvalidOperationException("Simulated content timestamps must increase strictly.");
        }

        _lastContentNs = contentNs;
        var jitter = _options.IngestJitterNs > 0 ? (long)(_random.NextDouble() * _options.IngestJitterNs) : 0;
        var arrival = Math.Max(contentNs + _options.IngestLatencyNs + jitter, _lastArrivalNs);
        _lastArrivalNs = arrival;
        var discontinuity = !_discontinuitySent && contentNs >= _options.DiscontinuityAtNs;
        _discontinuitySent |= discontinuity;
        frame = new FrameArrival(_nextId++, contentNs, arrival, discontinuity);
        Delivered++;
        return true;
    }

    private bool InHold(long contentNs)
    {
        foreach (var hold in _options.Holds.Span)
        {
            if (contentNs >= hold.StartNs && contentNs < hold.StartNs + hold.DurationNs)
            {
                return true;
            }
        }

        return false;
    }
}
