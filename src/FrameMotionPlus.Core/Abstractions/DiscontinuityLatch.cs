using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

/// <summary>
/// The rule of <see cref="IFrameGenerator.NotifyDiscontinuity"/>, shared by the generators so they all keep it: a
/// notification names the content time T of the first frame after a cut, and a pair (prev, curr) is discontinuous when it
/// spans T (<c>prev.ContentTimeNs &lt; T ≤ curr.ContentTimeNs</c>). A pair entirely before T is not affected, so a
/// notification may arrive from any thread at any time, also while earlier pairs are still being generated. The first pair
/// that starts at or after T (<c>prev.ContentTimeNs ≥ T</c>) releases the notification, whether a pair across the cut was
/// ever enqueued or not, so a cut the pipeline showed with real frames leaves nothing behind.
/// <para>
/// Pairs are identified by content time alone, never by memory: every frame carries a distinct content time and a pair's
/// times increase (<see cref="ValidatePair"/>, which generators call before they enqueue anything).
/// </para>
/// <para>
/// Up to <see cref="Capacity"/> notifications can be pending at once (cuts not yet passed by the pairs enqueued). When a
/// further one arrives, the earliest of all of them, the new one included, is dropped: the pairs enqueued are furthest
/// behind on that one. Allocation-free. <see cref="Notify"/> is for any thread, any number of them; <see cref="Observe"/>
/// is for the one thread that enqueues.
/// </para>
/// <para>
/// Two notifications of the same time that race (two threads, while <see cref="Observe"/> frees a slot) can each take a
/// slot. That is harmless: both name the same cut, the same pair spans both and the same pair releases both; the only
/// effect is one slot fewer until then.
/// </para>
/// </summary>
public sealed class DiscontinuityLatch
{
    /// <summary>Notifications that can be pending at once.</summary>
    public const int Capacity = 4;

    private const long Empty = long.MinValue;

    private readonly long[] _cuts = [Empty, Empty, Empty, Empty];

    /// <summary>True while a notification has not yet been passed by the pairs observed (diagnostics).</summary>
    public bool IsActive
    {
        get
        {
            for (var i = 0; i < Capacity; i++)
            {
                if (Volatile.Read(ref _cuts[i]) != Empty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The frame with content time <paramref name="contentTimeNs"/> starts a new shot. Safe from any thread; never blocks
    /// and never allocates.
    /// </summary>
    public void Notify(long contentTimeNs)
    {
        if (contentTimeNs == Empty)
        {
            ThrowTime(contentTimeNs);
        }

        while (true)
        {
            var earliest = -1;
            var earliestTime = long.MaxValue;
            for (var i = 0; i < Capacity; i++)
            {
                var value = Volatile.Read(ref _cuts[i]);
                if (value == contentTimeNs)
                {
                    return; // already pending
                }

                if (value == Empty)
                {
                    var seen = Interlocked.CompareExchange(ref _cuts[i], contentTimeNs, Empty);
                    if (seen == Empty || seen == contentTimeNs)
                    {
                        return; // stored, or another thread stored the same cut here first
                    }

                    continue;
                }

                if (value < earliestTime)
                {
                    earliestTime = value;
                    earliest = i;
                }
            }

            // Full: the earliest cut gives way (the pairs enqueued are the most behind on it), which is the new one when it
            // comes before every pending one.
            if (earliest < 0)
            {
                continue; // every slot changed under the scan: look again
            }

            if (contentTimeNs < earliestTime)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _cuts[earliest], contentTimeNs, earliestTime) == earliestTime)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Called once per enqueue, before the work, by the enqueuing thread: true when (<paramref name="prev"/>,
    /// <paramref name="curr"/>) spans a pending cut. Releases every pending cut the pair has passed. The pair must be valid
    /// (<see cref="ValidatePair"/>).
    /// </summary>
    public bool Observe(in GpuFrame prev, in GpuFrame curr)
    {
        var start = prev.ContentTimeNs;
        var end = curr.ContentTimeNs;
        var spans = false;
        for (var i = 0; i < Capacity; i++)
        {
            var value = Volatile.Read(ref _cuts[i]);
            if (value == Empty)
            {
                continue;
            }

            if (value <= start)
            {
                // Passed: the pair starts at or after the cut, so no later pair can span it (times only increase). A
                // compare-exchange, so a notification that took the slot meanwhile is kept.
                Interlocked.CompareExchange(ref _cuts[i], Empty, value);
            }
            else if (value <= end)
            {
                spans = true;
            }
        }

        return spans;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless the pair's content times increase
    /// (<c>prev.ContentTimeNs &lt; curr.ContentTimeNs</c>): every frame carries a distinct content time, and prev is the
    /// earlier one. Allocation-free when they do.
    /// </summary>
    public static void ValidatePair(in GpuFrame prev, in GpuFrame curr)
    {
        if (prev.ContentTimeNs >= curr.ContentTimeNs)
        {
            ThrowPair(prev.ContentTimeNs, curr.ContentTimeNs);
        }
    }

    [DoesNotReturn]
    private static void ThrowPair(long prev, long curr) =>
        throw new ArgumentException(
            $"The pair's content times must increase (prev {prev} ns, curr {curr} ns): every frame carries a distinct content time " +
            "(GpuFrame.ContentTimeNs), and prev is the earlier frame.",
            nameof(curr));

    [DoesNotReturn]
    private static void ThrowTime(long time) =>
        throw new ArgumentOutOfRangeException(nameof(time), time, "Not a content time.");
}
