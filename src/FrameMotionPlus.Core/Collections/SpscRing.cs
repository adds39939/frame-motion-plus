using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Core.Collections;

/// <summary>
/// Bounded lock-free ring for exactly one producer thread and one consumer thread. Never allocates after
/// construction. Indices are monotonically increasing 64-bit counters, so wrap-around needs no special case.
/// </summary>
public sealed class SpscRing<T>
{
    private readonly T[] _buffer;
    private readonly long _mask;

    // Each counter sits alone on its own cache lines so producer and consumer never false-share.
    private PaddedInt64 _head;       // next index to read; written by the consumer only
    private PaddedInt64 _tail;       // next index to write; written by the producer only
    private PaddedInt64 _cachedHead; // producer's last observed head
    private PaddedInt64 _cachedTail; // consumer's last observed tail

    public SpscRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        if ((capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two.");
        }

        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Approximate number of queued items; exact when called from either endpoint while the other is idle.</summary>
    public int Count
    {
        get
        {
            var tail = Volatile.Read(ref _tail.Value);
            var head = Volatile.Read(ref _head.Value);
            return (int)Math.Clamp(tail - head, 0, _buffer.Length);
        }
    }

    public bool IsEmpty => Count == 0;

    /// <summary>Producer only. Returns false when the ring is full.</summary>
    public bool TryEnqueue(in T item)
    {
        var tail = _tail.Value;
        if (tail - _cachedHead.Value >= _buffer.Length)
        {
            _cachedHead.Value = Volatile.Read(ref _head.Value);
            if (tail - _cachedHead.Value >= _buffer.Length)
            {
                return false;
            }
        }

        _buffer[tail & _mask] = item;
        Volatile.Write(ref _tail.Value, tail + 1);
        return true;
    }

    /// <summary>Consumer only. Returns false when the ring is empty.</summary>
    public bool TryDequeue(out T item)
    {
        var head = _head.Value;
        if (head >= _cachedTail.Value)
        {
            _cachedTail.Value = Volatile.Read(ref _tail.Value);
            if (head >= _cachedTail.Value)
            {
                item = default!;
                return false;
            }
        }

        var index = head & _mask;
        item = _buffer[index];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _buffer[index] = default!;
        }

        Volatile.Write(ref _head.Value, head + 1);
        return true;
    }

    /// <summary>Consumer only. Reads the oldest item without removing it.</summary>
    public bool TryPeek(out T item)
    {
        var head = _head.Value;
        if (head >= _cachedTail.Value)
        {
            _cachedTail.Value = Volatile.Read(ref _tail.Value);
            if (head >= _cachedTail.Value)
            {
                item = default!;
                return false;
            }
        }

        item = _buffer[head & _mask];
        return true;
    }

    /// <summary>Consumer only. Drains everything queued and returns the newest item ("newest wins").</summary>
    public bool TryDequeueLatest(out T item, out int discarded)
    {
        discarded = 0;
        if (!TryDequeue(out item))
        {
            return false;
        }

        while (TryDequeue(out var next))
        {
            item = next;
            discarded++;
        }

        return true;
    }
}

/// <summary>A 64-bit counter alone on its own pair of cache lines (outside the generic type: explicit layout cannot be generic).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedInt64
{
    [FieldOffset(64)]
    public long Value;
}
