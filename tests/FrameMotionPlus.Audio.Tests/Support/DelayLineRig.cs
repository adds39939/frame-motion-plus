using FrameMotionPlus.Audio.Dsp;
using FrameMotionPlus.Core.Statistics;

namespace FrameMotionPlus.Audio.Tests.Support;

/// <summary>
/// A simulated capture card and sink around a real <see cref="DelayLine"/>: two independent sample clocks (each 48 kHz
/// times 1 + its drift), each calling the line in blocks at its own cycle times, in time order, exactly as the PipeWire
/// callbacks do. The capture's reported timestamps can carry jitter; the content is a function of the true capture time,
/// so what comes out can be checked against what went in: clicks (single-frame impulses at known capture times) found in
/// the output give the delay actually achieved, independent of anything the line reports. Deterministic.
/// </summary>
internal sealed class DelayLineRig
{
    private const double Rate = DelayLine.Rate;
    private readonly DeterministicRandom _random;
    private readonly float[] _captureBlock;
    private readonly float[] _playbackBlock;
    private long _captureBlocks;
    private long _playbackBlocks;
    private long _captureFrame;

    public DelayLineRig(DelayLine line, RigOptions options)
    {
        Line = line;
        O = options;
        _random = new DeterministicRandom(options.Seed);
        _captureBlock = new float[options.CaptureQuantum * 2];
        _playbackBlock = new float[8192 * 2];
    }

    public DelayLine Line { get; }

    public RigOptions O { get; }

    /// <summary>Simulated time, ns (the next callback's).</summary>
    public long NowNs { get; private set; }

    /// <summary>Every output frame with its true heard time (when recording).</summary>
    public List<(long HeardNs, float Left, float Right)> Output { get; } = [];

    public bool Record { get; set; }

    /// <summary>Called before each playback block (tests change the target or mute here).</summary>
    public Action<DelayLineRig>? BeforePlayback { get; set; }

    /// <summary>Capture stops delivering between these times (the source gone), ns.</summary>
    public (long From, long To) CaptureOutage { get; set; } = (long.MaxValue, long.MaxValue);

    /// <summary>Added to the sink's latency from now on (another sink), ns.</summary>
    public long SinkLatencyShiftNs { get; set; }

    /// <summary>Playback is not called between these times (a sink switch, a stall), ns.</summary>
    public (long From, long To) PlaybackOutage { get; set; } = (long.MaxValue, long.MaxValue);

    /// <summary>The true capture time of capture frame <paramref name="frame"/>.</summary>
    public long CaptureTimeOf(long frame) => O.CaptureStartNs + (long)Math.Round(frame * 1e9 / (Rate * (1 + (O.CaptureDriftPpm * 1e-6))));

    /// <summary>Runs until <paramref name="untilNs"/>.</summary>
    public void Run(long untilNs)
    {
        while (true)
        {
            var nextCapture = CaptureTimeOf(_captureBlocks * O.CaptureQuantum) + O.CaptureLatencyNs;
            var nextPlayback = O.PlaybackStartNs + (long)Math.Round((_playbackBlocks * O.PlaybackQuantum) * 1e9 / (Rate * (1 + (O.PlaybackDriftPpm * 1e-6))));
            var next = Math.Min(nextCapture, nextPlayback);
            if (next > untilNs)
            {
                NowNs = untilNs;
                return;
            }

            NowNs = next;
            if (nextCapture <= nextPlayback)
            {
                Capture(nextCapture);
            }
            else
            {
                Playback(nextPlayback);
            }
        }
    }

    private void Capture(long callbackNs)
    {
        var q = O.CaptureQuantum;
        var firstFrame = _captureFrame;
        _captureBlocks++;
        _captureFrame += q;
        if (callbackNs >= CaptureOutage.From && callbackNs < CaptureOutage.To)
        {
            return; // lost: the line will see the gap (or the stop)
        }

        for (var i = 0; i < q; i++)
        {
            var v = O.Content(firstFrame + i, this);
            _captureBlock[2 * i] = v;
            _captureBlock[(2 * i) + 1] = -v;
        }

        var first = CaptureTimeOf(firstFrame) + _random.NextSymmetric(O.TimestampJitterNs);
        Line.OnCapture(_captureBlock.AsSpan(0, q * 2), first, callbackNs - CaptureTimeOf(firstFrame));
    }

    private void Playback(long callbackNs)
    {
        var q = O.PlaybackQuantum;
        _playbackBlocks++;
        if (callbackNs >= PlaybackOutage.From && callbackNs < PlaybackOutage.To)
        {
            return;
        }

        BeforePlayback?.Invoke(this);
        var latency = O.PlaybackLatencyNs + SinkLatencyShiftNs;
        var heard = callbackNs + latency;
        Line.OnPlayback(_playbackBlock, q, heard + _random.NextSymmetric(O.TimestampJitterNs), latency);
        if (Record)
        {
            var nsPerFrame = 1e9 / (Rate * (1 + (O.PlaybackDriftPpm * 1e-6)));
            for (var i = 0; i < q; i++)
            {
                Output.Add((heard + (long)Math.Round(i * nsPerFrame), _playbackBlock[2 * i], _playbackBlock[(2 * i) + 1]));
            }
        }
    }

    /// <summary>
    /// The delays of the clicks found in the recorded output: for each output peak above half the click's height, its
    /// heard time (parabolic interpolation between frames) less the true capture time of the click it is.
    /// </summary>
    public List<double> ClickDelays(long clickEveryFrames, float height)
    {
        var delays = new List<double>();
        for (var i = 1; i < Output.Count - 1; i++)
        {
            var y0 = Output[i - 1].Left;
            var y1 = Output[i].Left;
            var y2 = Output[i + 1].Left;
            if (y1 < height / 2 || y1 < y0 || y1 < y2)
            {
                continue;
            }

            var denominator = y0 - (2 * y1) + y2;
            var offset = denominator == 0 ? 0 : 0.5 * (y0 - y2) / denominator;
            var nsPerFrame = Output[i + 1].HeardNs - Output[i].HeardNs;
            var heard = Output[i].HeardNs + (offset * nsPerFrame);

            // Which click: the one whose capture time is nearest heard − the expected delay.
            var guess = heard - O.ExpectedDelayNs;
            var frame = Math.Round((guess - O.CaptureStartNs) * (Rate * (1 + (O.CaptureDriftPpm * 1e-6))) / 1e9 / clickEveryFrames) * clickEveryFrames;
            delays.Add(heard - CaptureTimeOf((long)frame));
            i += 8;
        }

        return delays;
    }
}

/// <summary>The simulated clocks and paths.</summary>
internal sealed record RigOptions
{
    public int CaptureQuantum { get; init; } = 256;

    public int PlaybackQuantum { get; init; } = 256;

    public double CaptureDriftPpm { get; init; }

    public double PlaybackDriftPpm { get; init; }

    /// <summary>Age of a block's first frame when its callback runs, ns (PipeWire reports this for the card: 384 frames).</summary>
    public long CaptureLatencyNs { get; init; } = 8_000_000;

    /// <summary>From a playback callback to the first frame heard, ns (the HDMI sink: 256 frames).</summary>
    public long PlaybackLatencyNs { get; init; } = 5_333_333;

    public long CaptureStartNs { get; init; } = 1_000_000_000;

    public long PlaybackStartNs { get; init; } = 1_002_345_678;

    /// <summary>Uniform jitter on every reported timestamp, ± ns.</summary>
    public long TimestampJitterNs { get; init; }

    public ulong Seed { get; init; } = 1;

    /// <summary>For telling clicks apart: the delay they are expected near, ns.</summary>
    public long ExpectedDelayNs { get; init; } = 40_000_000;

    /// <summary>The content of capture frame f (left; right is its negative).</summary>
    public Func<long, DelayLineRig, float> Content { get; init; } = static (_, _) => 0f;
}
