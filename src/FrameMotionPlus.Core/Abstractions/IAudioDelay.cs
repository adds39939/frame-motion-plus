namespace FrameMotionPlus.Core.Abstractions;

/// <summary>
/// Delays the source's audio by the measured video pipeline latency. Changes to the target are smoothed
/// internally so the delay never warbles.
/// </summary>
public interface IAudioDelay : IDisposable
{
    long CurrentDelayNs { get; }

    void SetTargetDelay(long delayNs);

    void Start();

    void Stop();
}
