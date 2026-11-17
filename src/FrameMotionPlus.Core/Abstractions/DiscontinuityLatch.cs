using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

/// <summary>
/// The "next pair" of <see cref="IFrameGenerator.NotifyDiscontinuity"/>, shared by the generators so they all keep one
/// rule. <see cref="Notify"/> (any thread) arms it; the next <see cref="Observe"/> binds it to that call's pair, and every
/// later call with the same pair is discontinuous too, until a call with a different pair releases it. A pair is
/// identified by its frames: each frame's first pixel address and its content timestamp, so a history buffer reused for
/// a later frame is a different pair. Allocation-free; <see cref="Observe"/> is for the one thread that enqueues.
/// </summary>
public sealed class DiscontinuityLatch
{
    private int _armed;
    private bool _bound;
    private PairKey _pair;

    /// <summary>A pair's identity: where each frame's pixels start and when each frame first appeared.</summary>
    public readonly record struct PairKey(ulong Prev, long PrevTimeNs, ulong Curr, long CurrTimeNs)
    {
        public static PairKey Of(in GpuFrame prev, in GpuFrame curr) =>
            new(prev.Address + prev.Offset, prev.ContentTimeNs, curr.Address + curr.Offset, curr.ContentTimeNs);
    }

    /// <summary>True while a notification waits for its pair, or a pair is bound (diagnostics).</summary>
    public bool IsActive => Volatile.Read(ref _armed) != 0 || _bound;

    /// <summary>Arms the latch for the next pair. Safe from any thread.</summary>
    public void Notify() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>
    /// Called once per enqueue, before the work: true when this pair must not be treated as continuous motion (the pair
    /// the latch is bound to, binding it first if a notification is pending).
    /// </summary>
    public bool Observe(in GpuFrame prev, in GpuFrame curr)
    {
        var key = PairKey.Of(prev, curr);
        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            _pair = key;
            _bound = true;
            return true;
        }

        if (_bound && key != _pair)
        {
            _bound = false;
        }

        return _bound;
    }
}
