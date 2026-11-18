using FrameMotionPlus.Audio.Dsp;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Audio.PipeWire;

/// <summary>Converts a pw_time delay (in its rate's units) to nanoseconds.</summary>
internal static class PwTiming
{
    /// <summary>
    /// A playback stream's buffer queued in a process callback reaches the graph one cycle later: pw_stream first hands the
    /// graph the buffer queued in the previous callback (already in its io area when the callback runs, so pw_time.queued
    /// reads 0), then asks for the next. So a frame written now is heard one cycle (the block's own frames) after what
    /// pw_time's delay, queued and buffered add up to. Measured, not assumed: the null-sink loopback
    /// (PipeWireLoopbackTests) with the capture graph at 256 frames and the playback graph at 128 comes out 0.26 ms from
    /// its target with this cycle on the playback side, where it would be 2.67 ms off if it belonged to the capture side,
    /// and 10.67 ms (two 256-frame cycles, the generator's and the line's) off without it.
    /// </summary>
    public const int PlaybackExtraCycles = 1;

    public static long DelayNs(in PwTime t)
    {
        var num = t.Rate.Num == 0 ? 1u : t.Rate.Num;
        var denom = t.Rate.Denom == 0 ? (uint)PwStream.Rate : t.Rate.Denom;
        return Math.Max(0, t.Delay) * 1_000_000_000L * num / denom;
    }

    public static long FramesNs(ulong frames) => (long)(frames * 1_000_000_000UL / PwStream.Rate);
}

/// <summary>Allocation counter for a real-time callback: what the thread allocated inside the callbacks after a warm-up.</summary>
internal struct CallbackMeter
{
    private const int WarmupCalls = 200;
    private long _calls;
    private long _allocated;

    public long Calls => _calls;

    /// <summary>Bytes allocated inside the callbacks after the first 200 (the thread's runtime attach and first-use work).</summary>
    public long AllocatedBytes => Volatile.Read(ref _allocated);

    public static long Begin() => GC.GetAllocatedBytesForCurrentThread();

    public void End(long before)
    {
        if (++_calls > WarmupCalls)
        {
            Volatile.Write(ref _allocated, _allocated + (GC.GetAllocatedBytesForCurrentThread() - before));
        }
    }
}

/// <summary>
/// The capture half: every buffer of the source's quantum goes into the delay line with the capture time of its first
/// frame: the cycle time the buffer was queued at, less the stream's delay from the device (PipeWire's pw_time: the age
/// of the first frame of the quantum when the graph cycle starts) and the frames held in its converter.
/// </summary>
internal sealed unsafe class CaptureStream(PwConnection connection, string name, IReadOnlyList<KeyValuePair<string, string>> properties, DelayLine line)
    : PwStream(connection, name, properties)
{
    private CallbackMeter _meter;
    private long _delaySumNs;
    private long _delayCount;
    private long _lastDelayNs;
    private long _buffersWithoutData;

    public long AllocatedBytes => _meter.AllocatedBytes;

    public long Cycles => _meter.Calls;

    /// <summary>The capture path's own latency (age of a quantum's first frame at the cycle), mean, ns.</summary>
    public double MeanDelayNs => _delayCount == 0 ? 0 : _delaySumNs / (double)_delayCount;

    public long LastDelayNs => Volatile.Read(ref _lastDelayNs);

    public long BuffersWithoutData => _buffersWithoutData;

    protected override void OnProcess()
    {
        var before = CallbackMeter.Begin();
        if (!TryGetTime(out var t))
        {
            while (Dequeue() is var b && b != null)
            {
                Queue(b);
            }

            _meter.End(before);
            return;
        }

        var delay = PwTiming.DelayNs(t) + PwTiming.FramesNs(t.Buffered);
        Volatile.Write(ref _lastDelayNs, delay);
        _delaySumNs += delay;
        _delayCount++;
        PwBuffer* buffer;
        while ((buffer = Dequeue()) != null)
        {
            var d = buffer->Buffer->NDatas > 0 ? buffer->Buffer->Datas : null;
            if (d == null || d->Data == null || d->Chunk == null)
            {
                _buffersWithoutData++;
                Queue(buffer);
                continue;
            }

            var max = d->MaxSize;
            var offset = Math.Min(d->Chunk->Offset % Math.Max(1u, max), max);
            var size = Math.Min(d->Chunk->Size, max - offset);
            var frames = (int)(size / FrameBytes);
            if (frames > 0)
            {
                var cycle = buffer->Time != 0 ? (long)buffer->Time : t.Now;
                var first = cycle - delay;
                line.OnCapture(new ReadOnlySpan<float>((byte*)d->Data + offset, frames * Channels), first, delay);
            }

            Queue(buffer);
        }

        _meter.End(before);
    }
}

/// <summary>
/// The playback half: each cycle the sink asks for a block; the delay line fills it with the time its first frame will
/// be heard: the cycle time plus the stream's delay to the device, what is queued and buffered before it (pw_time), and
/// the one cycle a queued buffer waits (<see cref="PwTiming.PlaybackExtraCycles"/>).
/// Cycles the sink ran without us (ticks advancing more than a block) are counted.
/// </summary>
internal sealed unsafe class PlaybackStream(PwConnection connection, string name, IReadOnlyList<KeyValuePair<string, string>> properties, DelayLine line, int quantum)
    : PwStream(connection, name, properties)
{
    private CallbackMeter _meter;
    private long _latencySumNs;
    private long _latencyCount;
    private long _lastLatencyNs;
    private ulong _lastTicks;
    private int _lastFrames;
    private long _lateCycles;
    private int _lastQuantum;

    public long AllocatedBytes => _meter.AllocatedBytes;

    public long Cycles => _meter.Calls;

    /// <summary>The sink path's own latency (from the cycle to heard), mean, ns.</summary>
    public double MeanLatencyNs => _latencyCount == 0 ? 0 : _latencySumNs / (double)_latencyCount;

    public long LastLatencyNs => Volatile.Read(ref _lastLatencyNs);

    /// <summary>The frames per cycle the sink last asked for.</summary>
    public int LastQuantum => Volatile.Read(ref _lastQuantum);

    /// <summary>Cycles where the sink's clock advanced more than a block and a half since the last (our callback was late).</summary>
    public long LateCycles => Volatile.Read(ref _lateCycles);

    protected override void OnProcess()
    {
        var before = CallbackMeter.Begin();
        var buffer = Dequeue();
        if (buffer == null)
        {
            _meter.End(before);
            return;
        }

        var d = buffer->Buffer->NDatas > 0 ? buffer->Buffer->Datas : null;
        if (d == null || d->Data == null || d->Chunk == null)
        {
            Queue(buffer);
            _meter.End(before);
            return;
        }

        var maxFrames = (int)(d->MaxSize / FrameBytes);
        var frames = (int)Math.Min(buffer->Requested > 0 ? buffer->Requested : (ulong)quantum, (ulong)maxFrames);
        var output = new Span<float>(d->Data, frames * Channels);
        if (TryGetTime(out var t))
        {
            if (_lastFrames > 0 && t.Ticks > _lastTicks && t.Ticks - _lastTicks > (ulong)(_lastFrames * 3 / 2))
            {
                Volatile.Write(ref _lateCycles, _lateCycles + 1);
            }

            _lastTicks = t.Ticks;
            _lastFrames = frames;
            var latency = PwTiming.DelayNs(t) + PwTiming.FramesNs(t.Queued + t.Buffered + (ulong)PwTiming.PlaybackExtraCycles * (ulong)frames);
            Volatile.Write(ref _lastLatencyNs, latency);
            Volatile.Write(ref _lastQuantum, frames);
            _latencySumNs += latency;
            _latencyCount++;
            line.OnPlayback(output, frames, t.Now + latency, latency);
        }
        else
        {
            output.Clear();
        }

        d->Chunk->Offset = 0;
        d->Chunk->Stride = FrameBytes;
        d->Chunk->Size = (uint)(frames * FrameBytes);
        buffer->Size = (ulong)frames;
        Queue(buffer);
        _meter.End(before);
    }
}

/// <summary>The monotonic clock, for the supervisor's timeouts.</summary>
internal static class Clock
{
    public static long NowNs => Monotonic.NowNs;
}
