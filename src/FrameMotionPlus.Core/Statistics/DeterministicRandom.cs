namespace FrameMotionPlus.Core.Statistics;

/// <summary>SplitMix64: a tiny, fast generator that produces identical sequences on every platform.</summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [-<paramref name="amplitude"/>, +<paramref name="amplitude"/>].</summary>
    public long NextSymmetric(long amplitude) =>
        amplitude <= 0 ? 0 : (long)Math.Round(((NextDouble() * 2.0) - 1.0) * amplitude);
}
