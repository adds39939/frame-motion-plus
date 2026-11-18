namespace FrameMotionPlus.Audio.Dsp;

/// <summary>
/// The delay line's memory: interleaved stereo float frames addressed by absolute frame number (frames ever written), for
/// one producer (the capture side) and one consumer (the playback side). The producer copies and then publishes the new
/// count with a release write; the consumer reads the count with an acquire read and reads only frames below it. The
/// producer never waits: it overwrites the oldest frames, so the consumer reads only frames at least <see cref="Guard"/>
/// newer than the oldest one kept (<see cref="OldestReadable"/>), which leaves the producer that many frames of room to
/// write during a read. Frames never written (before frame 0) read as silence. Allocation-free after construction.
/// </summary>
public sealed class SampleRing
{
    public const int Channels = 2;

    private readonly float[] _samples;
    private readonly int _mask;
    private long _written;

    /// <param name="capacityFrames">A power of two.</param>
    /// <param name="guardFrames">Frames kept back from the oldest end for the producer's writes during a read.</param>
    public SampleRing(int capacityFrames, int guardFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 64);
        if ((capacityFrames & (capacityFrames - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames), capacityFrames, "Must be a power of two.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(guardFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(guardFrames, capacityFrames);
        _samples = new float[capacityFrames * Channels];
        _mask = capacityFrames - 1;
        Capacity = capacityFrames;
        Guard = guardFrames;
    }

    public int Capacity { get; }

    public int Guard { get; }

    /// <summary>Frames ever written (acquire read: every frame below it is in memory).</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>The oldest frame the consumer may read when <paramref name="written"/> frames have been written.</summary>
    public long OldestReadable(long written) => written - Capacity + Guard;

    /// <summary>Producer: appends interleaved frames (an odd trailing sample is ignored).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / Channels;
        if (frames == 0)
        {
            return;
        }

        var w = _written;
        var skip = Math.Max(0, frames - Capacity); // more than the ring holds: only the newest frames matter
        var source = interleaved.Slice(skip * Channels, (frames - skip) * Channels);
        var start = (int)((w + skip) & _mask);
        var first = Math.Min(frames - skip, Capacity - start);
        source[..(first * Channels)].CopyTo(_samples.AsSpan(start * Channels));
        if (first < frames - skip)
        {
            source[(first * Channels)..].CopyTo(_samples);
        }

        Volatile.Write(ref _written, w + frames);
    }

    /// <summary>Producer: appends <paramref name="frames"/> frames of silence.</summary>
    public void WriteSilence(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var w = _written;
        var n = Math.Min(frames, Capacity);
        var start = (int)((w + frames - n) & _mask);
        var first = Math.Min(n, Capacity - start);
        _samples.AsSpan(start * Channels, first * Channels).Clear();
        if (first < n)
        {
            _samples.AsSpan(0, (n - first) * Channels).Clear();
        }

        Volatile.Write(ref _written, w + frames);
    }

    /// <summary>Consumer: one sample of absolute frame <paramref name="frame"/> (the caller keeps it readable).</summary>
    public float Read(long frame, int channel) => _samples[((int)(frame & _mask) * Channels) + channel];
}
