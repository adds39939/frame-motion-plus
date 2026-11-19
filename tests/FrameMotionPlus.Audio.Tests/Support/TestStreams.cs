using FrameMotionPlus.Audio.PipeWire;
using FrameMotionPlus.Audio.PipeWire.Interop;

namespace FrameMotionPlus.Audio.Tests.Support;

/// <summary>
/// Plays a quiet click train (a single frame at 0.05 every <see cref="Every"/> frames, silence otherwise) to a target
/// sink and records when each click will be heard, from the stream's own timing (as the delay line's playback side
/// computes it). Allocation-free on the data thread.
/// </summary>
internal sealed unsafe class ClickGenerator(PwConnection connection, string name, string target)
    : PwStream(connection, name,
    [
        new(PwKeys.MediaType, "Audio"),
        new(PwKeys.MediaCategory, "Playback"),
        new(PwKeys.NodeName, name),
        new(PwKeys.NodeLatency, "256/48000"),
        new(PwKeys.TargetObject, target),
        new("state.restore-props", "false"),
        new("state.restore-target", "false"),
    ])
{
    public const int Every = 4_800;
    public const float Height = 0.05f;
    private readonly long[] _clickFrames = new long[512];
    private readonly long[] _heardNs = new long[512];
    private long _frame;
    private int _count;

    /// <summary>Clicks start after this many frames (the streams settle first).</summary>
    public long StartFrame { get; init; } = Every * 4;

    public int Count => Volatile.Read(ref _count);

    public bool Enabled { get; set; } = true;

    public (long Frame, long HeardNs) Click(int i) => (_clickFrames[i], _heardNs[i]);

    protected override void OnProcess()
    {
        var b = Dequeue();
        if (b == null)
        {
            return;
        }

        var d = b->Buffer->Datas;
        var frames = (int)Math.Min(b->Requested > 0 ? b->Requested : 256UL, d->MaxSize / (ulong)FrameBytes);
        var output = new Span<float>(d->Data, frames * Channels);
        output.Clear();
        if (TryGetTime(out var t) && Enabled)
        {
            var heard = t.Now + PwTiming.DelayNs(t) + PwTiming.FramesNs(t.Queued + t.Buffered + (ulong)PwTiming.PlaybackExtraCycles * (ulong)frames);
            for (var i = 0; i < frames; i++)
            {
                var f = _frame + i;
                if (f >= StartFrame && f % Every == 0 && _count < _clickFrames.Length)
                {
                    output[2 * i] = Height;
                    output[(2 * i) + 1] = Height;
                    _clickFrames[_count] = f;
                    _heardNs[_count] = heard + (long)(i * 1e9 / Rate);
                    Volatile.Write(ref _count, _count + 1);
                }
            }
        }

        _frame += frames;
        d->Chunk->Offset = 0;
        d->Chunk->Stride = FrameBytes;
        d->Chunk->Size = (uint)(frames * FrameBytes);
        b->Size = (ulong)frames;
        Queue(b);
    }
}

/// <summary>
/// Records a sink's monitor and finds clicks in it (peaks above half a click, located to a fraction of a frame by a
/// parabola through the peak and its neighbours), each with its capture time from the stream's timing (as the delay
/// line's capture side computes it). Allocation-free on the data thread.
/// </summary>
internal sealed unsafe class ClickRecorder(PwConnection connection, string name, string sink, bool captureSink = true)
    : PwStream(connection, name,
    [
        new(PwKeys.MediaType, "Audio"),
        new(PwKeys.MediaCategory, "Capture"),
        new(PwKeys.NodeName, name),
        new(PwKeys.TargetObject, sink),
        new(PwKeys.NodeLatency, "128/48000"),
        new(PwKeys.StreamCaptureSink, captureSink ? "true" : "false"),
        new(PwKeys.NodeDontReconnect, "true"),
        new("state.restore-props", "false"),
        new("state.restore-target", "false"),
    ])
{
    private readonly double[] _capturedNs = new double[512];
    private float _y0;
    private float _y1;
    private long _y1Ns;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public long Frames { get; private set; }

    public double CapturedNs(int i) => _capturedNs[i];

    protected override void OnProcess()
    {
        if (!TryGetTime(out var t))
        {
            while (Dequeue() is var e && e != null)
            {
                Queue(e);
            }

            return;
        }

        var delay = PwTiming.DelayNs(t) + PwTiming.FramesNs(t.Buffered);
        PwBuffer* b;
        while ((b = Dequeue()) != null)
        {
            var d = b->Buffer->Datas;
            var frames = (int)(Math.Min(d->Chunk->Size, d->MaxSize) / FrameBytes);
            var samples = new ReadOnlySpan<float>((byte*)d->Data + Math.Min(d->Chunk->Offset, d->MaxSize), frames * Channels);
            var first = (b->Time != 0 ? (long)b->Time : t.Now) - delay;
            for (var i = 0; i < frames; i++)
            {
                var y2 = samples[2 * i];
                if (_y1 > ClickGenerator.Height / 2 && _y1 >= _y0 && _y1 > y2 && _count < _capturedNs.Length)
                {
                    var denominator = _y0 - (2 * _y1) + y2;
                    var offset = denominator == 0 ? 0 : 0.5 * (_y0 - y2) / denominator;
                    _capturedNs[_count] = _y1Ns + (offset * 1e9 / Rate);
                    Volatile.Write(ref _count, _count + 1);
                }

                _y0 = _y1;
                _y1 = y2;
                _y1Ns = first + (long)(i * 1e9 / Rate);
            }

            Frames += frames;
            Queue(b);
        }
    }
}
