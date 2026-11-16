namespace FrameMotionPlus.Core.Time;

/// <summary>A manually driven clock for simulations and tests. Time never moves backwards.</summary>
public sealed class SimulatedClock : IClock
{
    public SimulatedClock(long startNs = 0)
    {
        NowNs = startNs;
    }

    public long NowNs { get; private set; }

    public void SleepUntil(long deadlineNs) => AdvanceTo(deadlineNs);

    public void AdvanceTo(long timeNs)
    {
        if (timeNs > NowNs)
        {
            NowNs = timeNs;
        }
    }

    public void Advance(long deltaNs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deltaNs);
        NowNs += deltaNs;
    }
}
