using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Sources.File;

/// <summary>
/// A source's unique RGB frames in preallocated CUDA buffers: the ones queued ahead of the consumer and the last two or
/// three the consumer holds as generator inputs (design doc, "Frame history"). Each buffer has a CUDA event of its own
/// as its <see cref="GpuFrame.Ready"/> (recorded once the frame is written, never re-recorded while a published frame
/// names it), and the frame carries its distinct content time and colour metadata.
/// <para>
/// <b>Overwriting.</b> A buffer is written again only after the consumer has released its frame
/// (<see cref="Release"/>) with the GPU-side point after which no work reads it any more: the generator's
/// <c>done</c>, the ring write that copied it, a CUDA event the consumer recorded. The producer's stream waits on that
/// point (<c>cuStreamWaitEvent</c> or an external-semaphore wait) before the next write, so the dependency is GPU-side:
/// no CPU waits for the GPU here. The object behind that signal must outlive the release (<see cref="GpuSignal"/>,
/// "Lifetime"): the producer enqueues the wait only when it next takes the buffer, which may be any time later, so the
/// event or semaphore must stay alive until the source is stopped or disposed (destroying it earlier stops the producer
/// with an <see cref="InvalidOperationException"/> naming the frame). An event is waited on as it was last recorded when
/// the producer enqueues the wait, so a consumer that re-records it afterwards for later work only makes the wait later.
/// </para>
/// <para>
/// <b>Threads.</b> One producer thread (<see cref="TryBeginWrite"/>, <see cref="CompleteWrite"/>, <see cref="Abandon"/>)
/// and one consumer thread (<see cref="MarkRead"/>, <see cref="Release"/>, <see cref="TryGetPlanes"/>), joined by a
/// single-producer ring of releases. Nothing allocates after construction.
/// </para>
/// </summary>
public sealed class FrameHistory : IDisposable
{
    private readonly CudaDeviceBuffer[] _buffers;
    private readonly CudaDeviceBuffer?[] _planes;
    private readonly CudaEvent[] _ready;
    private readonly SlotState[] _state;     // producer's view
    private readonly GpuSignal[] _lastReader; // producer's: what to wait for before writing the slot again
    private readonly long[] _frameOfSlot;    // producer writes (volatile) before publishing; consumer reads after
    private readonly bool[] _held;           // consumer's: read and not released yet
    private readonly SpscRing<ReleaseEntry> _releases;
    private readonly int _planePitch;
    private int _next;
    private bool _disposed;

    /// <param name="context">The CUDA context the buffers live in.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="format">RGBA8 or RGB10A2.</param>
    /// <param name="capacity">Buffers: frames queued ahead of the consumer plus frames it may hold at once.</param>
    /// <param name="planes">
    /// When set, each buffer also keeps a copy of the decoded YCbCr planes it was converted from (verification).
    /// </param>
    public FrameHistory(CudaContext context, int width, int height, PixelFormat format, int capacity, YuvSampleFormat planes = YuvSampleFormat.Unknown)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 64);
        if (format is not (PixelFormat.Rgba8 or PixelFormat.Rgb10A2))
        {
            throw new ArgumentException($"History frames are RGBA8 or RGB10A2, not {format}.", nameof(format));
        }

        Context = context;
        Width = width;
        Height = height;
        Format = format;
        Capacity = capacity;
        PlaneFormat = planes;
        _buffers = new CudaDeviceBuffer[capacity];
        _planes = new CudaDeviceBuffer?[capacity];
        _ready = new CudaEvent[capacity];
        _state = new SlotState[capacity];
        _lastReader = new GpuSignal[capacity];
        _frameOfSlot = new long[capacity];
        _held = new bool[capacity];
        var ringSize = 2;
        while (ringSize < capacity)
        {
            ringSize <<= 1;
        }

        _releases = new SpscRing<ReleaseEntry>(ringSize);
        var bytes = planes == YuvSampleFormat.P010 ? 2 : 1;
        _planePitch = (int)(((long)width * bytes + 255) & ~255L);
        try
        {
            for (var i = 0; i < capacity; i++)
            {
                _buffers[i] = CudaDeviceBuffer.AllocateImage(context, width, height, format);
                _ready[i] = CudaEvent.Create(context);
                _frameOfSlot[i] = -1;
                if (planes != YuvSampleFormat.Unknown)
                {
                    _planes[i] = CudaDeviceBuffer.Allocate(context, (long)_planePitch * (height + ((height + 1) / 2)));
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CudaContext Context { get; }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int Capacity { get; }

    /// <summary>The decoded-plane copies kept with each frame (<see cref="YuvSampleFormat.Unknown"/>: none).</summary>
    public YuvSampleFormat PlaneFormat { get; }

    /// <summary>Slots not held by the consumer nor queued for it (producer's view; diagnostics).</summary>
    public int FreeSlots
    {
        get
        {
            var free = 0;
            foreach (var s in _state)
            {
                free += s == SlotState.Free ? 1 : 0;
            }

            return free;
        }
    }

    /// <summary>
    /// Producer: takes a free buffer for the next frame and makes <paramref name="stream"/> wait, GPU-side, until its
    /// previous frame's readers are done. -1 when every buffer is queued or held (back-pressure: try again after the
    /// consumer releases one). Applies the consumer's releases first. Allocation-free.
    /// </summary>
    public int TryBeginWrite(CudaStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);
        DrainReleases();
        for (var k = 0; k < Capacity; k++)
        {
            var slot = (_next + k) % Capacity;
            if (_state[slot] != SlotState.Free)
            {
                continue;
            }

            try
            {
                stream.Wait(_lastReader[slot]);
            }
            catch (ObjectDisposedException e)
            {
                ThrowReleasePointGone(_frameOfSlot[slot], e);
            }

            _lastReader[slot] = GpuSignal.None;
            _state[slot] = SlotState.Writing;
            _next = (slot + 1) % Capacity;
            return slot;
        }

        return -1;
    }

    /// <summary>The buffer of <paramref name="slot"/> as a frame to write into (no Ready signal).</summary>
    public GpuFrame Target(int slot) => _buffers[slot].AsFrame();

    /// <summary>Where the decoded planes of the frame in <paramref name="slot"/> are kept (a history made with planes only).</summary>
    public YuvPlanes PlaneTarget(int slot)
    {
        var buffer = _planes[slot] ?? throw new InvalidOperationException("This history keeps no decoded planes.");
        return new YuvPlanes
        {
            Luma = buffer.Address,
            LumaPitch = _planePitch,
            Chroma = buffer.Address + ((ulong)_planePitch * (ulong)Height),
            ChromaPitch = _planePitch,
            Width = Width,
            Height = Height,
            Format = PlaneFormat,
        };
    }

    /// <summary>
    /// Producer: the frame in <paramref name="slot"/> is enqueued on <paramref name="stream"/>; records its Ready event
    /// there and returns it as a frame naming that event, its id, content time and colour, to publish. Allocation-free.
    /// </summary>
    public GpuFrame CompleteWrite(int slot, CudaStream stream, long frameId, long contentTimeNs, in ColorMetadata color)
    {
        if (_state[slot] != SlotState.Writing)
        {
            ThrowState(slot);
        }

        var ready = _ready[slot];
        ready.Record(stream);
        _state[slot] = SlotState.Out;
        Volatile.Write(ref _frameOfSlot[slot], frameId);
        return _buffers[slot].AsFrame(color, contentTimeNs, ready.Signal);
    }

    /// <summary>
    /// Producer: gives back a slot from <see cref="TryBeginWrite"/> that was not completed (the end of the file, a frame
    /// skipped on the way to a seek target). The wait for its last readers stays on the stream, ahead of any later write.
    /// </summary>
    public void Abandon(int slot)
    {
        if (_state[slot] == SlotState.Writing)
        {
            _state[slot] = SlotState.Free;
        }
    }

    /// <summary>Consumer: <paramref name="frameId"/> was read and is now held (until <see cref="Release"/>).</summary>
    public void MarkRead(long frameId) => _held[SlotOf(frameId)] = true;

    /// <summary>
    /// Consumer: no work enqueued after this call reads <paramref name="frameId"/>, and the work enqueued before reads it
    /// no longer once <paramref name="lastReader"/> is reached (<see cref="GpuSignal.None"/>: none is pending). The buffer
    /// may be written again after that point. Allocation-free; throws for a frame not held.
    /// </summary>
    public void Release(long frameId, in GpuSignal lastReader)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!lastReader.IsNone && lastReader.Domain != MemoryDomain.CudaDevice)
        {
            ThrowSignal(lastReader);
        }

        var slot = SlotOf(frameId);
        if (!_held[slot])
        {
            ThrowNotHeld(frameId);
        }

        _held[slot] = false;
        if (!_releases.TryEnqueue(new ReleaseEntry(slot, frameId, lastReader)))
        {
            // Cannot happen: at most Capacity frames are out, and the ring holds twice that.
            ThrowRingFull();
        }
    }

    /// <summary>Consumer: the slot of a held frame (false for a frame not held).</summary>
    public bool TryGetHeldSlot(long frameId, out int slot) => TryFindSlot(frameId, out slot) && _held[slot];

    /// <summary>Consumer: the decoded planes of a held frame (only when the history keeps them).</summary>
    public bool TryGetPlanes(long frameId, out YuvPlanes planes)
    {
        planes = default;
        if (PlaneFormat == YuvSampleFormat.Unknown || !TryFindSlot(frameId, out var slot) || !_held[slot])
        {
            return false;
        }

        planes = PlaneTarget(slot);
        return true;
    }

    /// <summary>Producer: applies the consumer's releases. Returns how many.</summary>
    public int DrainReleases()
    {
        var count = 0;
        while (_releases.TryDequeue(out var entry))
        {
            if (_state[entry.Slot] == SlotState.Out && Volatile.Read(ref _frameOfSlot[entry.Slot]) == entry.FrameId)
            {
                _state[entry.Slot] = SlotState.Free;
                _lastReader[entry.Slot] = entry.LastReader;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Frees the buffers and events. Every GPU wait or read that names them must have finished (a drained point:
    /// <c>cuMemFree</c> waits for all work queued in the context).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < Capacity; i++)
        {
            _buffers[i]?.Dispose();
            _planes[i]?.Dispose();
            _ready[i]?.Dispose();
        }
    }

    private int SlotOf(long frameId)
    {
        if (!TryFindSlot(frameId, out var slot))
        {
            ThrowUnknown(frameId);
        }

        return slot;
    }

    /// <summary>
    /// The slot holding <paramref name="frameId"/>. A frame the consumer holds cannot be rewritten, so the search is
    /// race-free for those; other slots may change meanwhile but never match.
    /// </summary>
    private bool TryFindSlot(long frameId, out int slot)
    {
        for (slot = 0; slot < Capacity; slot++)
        {
            if (frameId >= 0 && Volatile.Read(ref _frameOfSlot[slot]) == frameId)
            {
                return true;
            }
        }

        slot = -1;
        return false;
    }

    [DoesNotReturn]
    private static void ThrowState(int slot) => throw new InvalidOperationException($"History slot {slot} is not being written.");

    [DoesNotReturn]
    private static void ThrowUnknown(long frameId) => throw new ArgumentException($"Frame {frameId} is not a frame of this source that is out.", nameof(frameId));

    [DoesNotReturn]
    private static void ThrowNotHeld(long frameId) =>
        throw new InvalidOperationException($"Frame {frameId} is not held: it was never read, or was released already.");

    [DoesNotReturn]
    private static void ThrowSignal(in GpuSignal signal) =>
        throw new ArgumentException($"The release point must be a CUDA-domain signal (a CUDA event or CUDA timeline semaphore), not {signal.Kind} in {signal.Domain}.", nameof(signal));

    [DoesNotReturn]
    private static void ThrowReleasePointGone(long frameId, ObjectDisposedException inner) =>
        throw new InvalidOperationException(
            $"The release point given for frame {frameId} (the GPU signal passed to Release) was destroyed before its buffer was " +
            "written again: the event or semaphore must stay alive until the source is stopped or disposed.",
            inner);

    [DoesNotReturn]
    private static void ThrowRingFull() => throw new InvalidOperationException("The release ring is full.");

    private enum SlotState : byte
    {
        Free,
        Writing,
        Out,
    }

    private readonly record struct ReleaseEntry(int Slot, long FrameId, GpuSignal LastReader);
}
