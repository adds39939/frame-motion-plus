namespace FrameMotionPlus.Core.Time;

/// <summary>The one monotonic clock. Every timestamp in the app is nanoseconds on this clock.</summary>
public interface IClock
{
    long NowNs { get; }

    /// <summary>Blocks the calling thread until the absolute time <paramref name="deadlineNs"/>.</summary>
    void SleepUntil(long deadlineNs);
}
