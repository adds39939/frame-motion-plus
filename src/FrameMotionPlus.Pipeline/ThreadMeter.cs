using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Per-thread measurement of the measured window: managed bytes the thread allocated and CPU time it used. Only a thread
/// can read its own counters, so the session asks (<see cref="Begin"/>, <see cref="End"/>, any thread) and the thread
/// takes the snapshot at its next <see cref="Tick"/> (one volatile read per loop turn; allocation-free).
/// </summary>
public sealed class ThreadMeter
{
    private volatile int _requested;
    private int _state;
    private long _allocatedAtBegin;
    private long _cpuAtBegin;
    private long _allocated = -1;
    private long _cpu = -1;
    private long _ticks;
    private long _ticksAtBegin;

    /// <summary>Managed bytes allocated by the thread in the measured window; -1 if it was not measured.</summary>
    public long AllocatedBytes => Volatile.Read(ref _allocated);

    /// <summary>CPU time the thread used in the measured window, ns; -1 if it was not measured.</summary>
    public long CpuNs => Volatile.Read(ref _cpu);

    /// <summary>Loop turns in the measured window.</summary>
    public long Ticks { get; private set; }

    /// <summary>Starts the window at the thread's next turn. Any thread.</summary>
    public void Begin()
    {
        if (_requested == 0)
        {
            _requested = 1;
        }
    }

    /// <summary>Ends the window at the thread's next turn. Any thread.</summary>
    public void End() => _requested = 2;

    /// <summary>The owning thread, once per loop turn.</summary>
    public void Tick()
    {
        _ticks++;
        var requested = _requested;
        if (requested == _state)
        {
            return;
        }

        if (_state == 0)
        {
            _allocatedAtBegin = GC.GetAllocatedBytesForCurrentThread();
            _cpuAtBegin = ThreadCpuClock.CurrentThreadNs;
            _ticksAtBegin = _ticks;
            _state = 1;
        }

        if (requested == 2 && _state == 1)
        {
            Finish();
        }
    }

    /// <summary>The owning thread, as it exits: ends a window still open.</summary>
    public void Stop()
    {
        if (_state == 1)
        {
            Finish();
        }
    }

    private void Finish()
    {
        Ticks = _ticks - _ticksAtBegin;
        Volatile.Write(ref _cpu, _cpuAtBegin < 0 ? -1 : ThreadCpuClock.CurrentThreadNs - _cpuAtBegin);
        Volatile.Write(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - _allocatedAtBegin);
        _state = 2;
    }
}
