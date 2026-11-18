namespace FrameMotionPlus.Core.Abstractions;

/// <summary>
/// Delays the source's audio by the measured video pipeline latency. Changes to the target are smoothed
/// internally so the delay never warbles.
/// </summary>
public interface IAudioDelay : IDisposable
{
    /// <summary>The total delay of the sound now, from a sample's capture time to the time it is heard, ns (0 before any).</summary>
    long CurrentDelayNs { get; }

    /// <summary>
    /// The delay to hold: the video latency the sound must match (from a frame's capture timestamp to its first pixel), ns.
    /// The implementation accounts for its own path's latency and adds its own manual trim. Called from the pipeline's
    /// control thread whenever the measurement moves: it must not block or allocate.
    /// </summary>
    void SetTargetDelay(long delayNs);

    void Start();

    /// <summary>Silences the sound and begins releasing it; returns at once (the pipeline calls it as playback ends).
    /// <see cref="IDisposable.Dispose"/> waits, bounded, for the release.</summary>
    void Stop();
}
