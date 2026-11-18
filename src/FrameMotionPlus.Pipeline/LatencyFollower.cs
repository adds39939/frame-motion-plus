using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// The video latency the sound follows (design doc, "Audio sync": measured, not configured): each real frame's first
/// pixel less its capture timestamp, as <see cref="PlaybackStatistics.RealFrameLatency"/> measures it, from the first real
/// frame shown (warm-up included, so the sound has a figure early), low-passed with a time constant of 5 s: a window
/// on fixed refresh wanders by several milliseconds over tens of seconds (its lead adapts; an unlocked source beats
/// against the refresh), which is not worth a pitch change to follow. For its first 5 s it is the running mean (no
/// single early frame drags it). One frame can move the estimate by at most <see cref="ClampNs"/> of its deviation (a
/// hitch does not drag it), and a real shift (another refresh mode, a different lead) is followed within seconds. Pure;
/// allocation-free; the control thread's.
/// </summary>
public sealed class LatencyFollower
{
    /// <summary>The most one frame's deviation counts for, ns.</summary>
    public const long ClampNs = 20 * Nanoseconds.PerMillisecond;

    private readonly double _timeConstantNs;
    private double _value;
    private long _lastAtNs;
    private long _firstAtNs;

    public LatencyFollower(long timeConstantNs = 5 * Nanoseconds.PerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeConstantNs, 0);
        _timeConstantNs = timeConstantNs;
    }

    /// <summary>The smoothed latency, ns (0 before the first frame).</summary>
    public long ValueNs => (long)Math.Round(_value);

    public long Samples { get; private set; }

    /// <summary>Bumped on every sample (so a consumer can tell a new value from an old one).</summary>
    public long Version => Samples;

    /// <summary>A real frame shown at <paramref name="shownNs"/> with latency <paramref name="latencyNs"/>.</summary>
    public void Add(long latencyNs, long shownNs)
    {
        if (latencyNs <= 0 || latencyNs > 2 * Nanoseconds.PerSecond)
        {
            return; // not a latency: a clock mix-up or a stall
        }

        if (Samples == 0)
        {
            _value = latencyNs;
            _firstAtNs = shownNs;
        }
        else
        {
            // A running mean until the time constant has passed (weight 1/(n+1)), then the exponential average.
            var dt = Math.Max(0, shownNs - _lastAtNs);
            var alpha = shownNs - _firstAtNs < _timeConstantNs ? 1.0 / (Samples + 1) : 1 - Math.Exp(-dt / _timeConstantNs);
            var deviation = Math.Clamp(latencyNs - _value, -ClampNs, ClampNs);
            _value += deviation * alpha;
        }

        _lastAtNs = shownNs;
        Samples++;
    }
}
