namespace FrameMotionPlus.Audio.Dsp;

/// <summary>Configuration of a <see cref="DelayLine"/> (48 kHz stereo float).</summary>
public sealed record DelayLineOptions
{
    public const int SampleRate = 48_000;

    /// <summary>Ring size, frames (a power of two): 65,536 is 1.37 s.</summary>
    public int RingFrames { get; init; } = 1 << 16;

    /// <summary>Kept back from the ring's oldest end for the capture side's writes during a read, frames (341 ms).</summary>
    public int GuardFrames { get; init; } = 1 << 14;

    /// <summary>Added to the video latency (positive: the sound later), ns: the manual trim.</summary>
    public long TrimNs { get; init; }

    /// <summary>The video latency assumed until the first measurement arrives, ns.</summary>
    public long InitialVideoLatencyNs { get; init; } = 40_000_000;

    /// <summary>Extra room above the path's own minimum delay (scheduling lateness of the callbacks), ns.</summary>
    public long MarginNs { get; init; } = 2_000_000;

    /// <summary>A gap in the capture timestamps longer than this is filled with silence (lost frames), ns.</summary>
    public long GapThresholdNs { get; init; } = 1_500_000;

    /// <summary>Gaps up to this are filled; a longer one is a discontinuity (the playback side resynchronises), ns.</summary>
    public long MaxGapFillNs { get; init; } = 200_000_000;

    /// <summary>Silent until the first video latency arrives (<see cref="DelayLine.SetVideoLatency"/>): no sound before a picture.</summary>
    public bool SilentUntilVideoLatency { get; init; }

    /// <summary>Fade in and out (start, resynchronisation, mute), frames.</summary>
    public int FadeFrames { get; init; } = 480;

    /// <summary>Cross-fade of a jump, frames (20 ms).</summary>
    public int CrossFadeFrames { get; init; } = 960;

    /// <summary>
    /// This long after the first video latency arrived, the line moves to the (by then settled) latency once more with a
    /// cross-fade; the statistics start a second later, ns.
    /// </summary>
    public long SettleNs { get; init; } = 5_000_000_000;

    public DelayControllerOptions Controller { get; init; } = new();
}

/// <summary>What the playback side is doing.</summary>
public enum DelayLineState
{
    /// <summary>No capture data yet: silence.</summary>
    Waiting = 0,

    /// <summary>Playing on target (resampling to hold it).</summary>
    Playing = 1,

    /// <summary>The capture data ran out (an underrun, the source gone): silence until it is back.</summary>
    Starved = 2,

    /// <summary>Muted on request (stopping, or the capture not linked to the intended source): silence.</summary>
    Muted = 3,
}

/// <summary>
/// The delay line: the capture side appends frames with the capture time of their first frame; the playback side asks
/// for blocks with the time their first frame will be heard. Each block's delay is measured from those timestamps (the
/// capture time of the read position against the time it will be heard), and <see cref="DelayController"/> resamples the
/// read so that delay holds at the video latency plus the trim, clamped to what this path can do (its own capture and
/// sink latency, a block of lookahead and a margin; when that exceeds the target the sound is as early as it can be and the
/// shortfall is counted). Capture gaps are filled with silence so the timeline stays true; an underrun fades out and
/// resynchronises when data is back; an overrun (the playback side stalled past the ring) resynchronises. The two sides
/// may run on different threads (one each): they share only the ring, the capture timeline and a few volatile fields.
/// No allocation, no lock and no blocking on either side.
/// </summary>
public sealed class DelayLine
{
    public const int Rate = DelayLineOptions.SampleRate;

    private const double NsPerFrame = 1e9 / Rate;

    private readonly DelayLineOptions _o;
    private readonly SampleRing _ring;
    private readonly CaptureTimeline _timeline = new();
    private readonly ResamplingReader _reader = new();
    private readonly DelayController _controller;
    private readonly double _maxRatio;

    // Capture side (its thread only), and what it hands over.
    private long _captureEndNs = long.MinValue;
    private long _captureLatencyNs;
    private int _captureFrames;

    // Requests from other threads.
    private long _videoLatencyNs;
    private long _videoLatencySetAtFrame = -1;
    private int _muted;
    private int _relock;
    private int _resetCaptureClock;

    // Playback side (its thread only).
    private int _state;
    private double _lastRatio = 1;
    private long _peakPhaseNs;
    private long _peakPhaseAgeFrames;
    private long _currentDelayNs;
    private long _measureFromFrame = long.MaxValue;
    private long _settledRelockFrame = long.MaxValue;
    private long _lastPathNs;

    public DelayLine(DelayLineOptions? options = null)
    {
        _o = options ?? new DelayLineOptions();
        _ring = new SampleRing(_o.RingFrames, _o.GuardFrames);
        _controller = new DelayController(_o.Controller);
        _maxRatio = 1 + (_o.Controller.MaxRatioPpm * 1e-6);
        _videoLatencyNs = _o.InitialVideoLatencyNs;
        Statistics = new DelayLineStatistics();
    }

    public DelayLineOptions Options => _o;

    /// <summary>Counters and the measured window; written by the two sides, read once they have stopped.</summary>
    public DelayLineStatistics Statistics { get; }

    public DelayLineState State => (DelayLineState)Volatile.Read(ref _state);

    /// <summary>The total delay of the last block (capture time to heard), ns; 0 before the first.</summary>
    public long CurrentDelayNs => Volatile.Read(ref _currentDelayNs);

    /// <summary>The ratio of the last block (input frames per output frame).</summary>
    public double Ratio => Volatile.Read(ref _lastRatio);

    /// <summary>The video latency the delay follows (capture timestamp to first pixel), ns: any thread.</summary>
    public void SetVideoLatency(long latencyNs)
    {
        Volatile.Write(ref _videoLatencyNs, latencyNs);
        if (Volatile.Read(ref _videoLatencySetAtFrame) < 0)
        {
            // The first measurement: the playback side moves straight to it (a cross-fade, not seconds of resampling from
            // the initial guess) and starts the settle time from its next block.
            Volatile.Write(ref _videoLatencySetAtFrame, 0);
            Volatile.Write(ref _relock, 1);
        }
    }

    public long VideoLatencyNs => Volatile.Read(ref _videoLatencyNs);

    /// <summary>Silence (faded) while true: any thread.</summary>
    public void SetMuted(bool muted) => Volatile.Write(ref _muted, muted ? 1 : 0);

    /// <summary>Whether it is muted on request (<see cref="SetMuted"/>).</summary>
    public bool Muted => Volatile.Read(ref _muted) != 0;

    /// <summary>
    /// Capture side: <paramref name="interleaved"/> stereo frames, the first captured at <paramref name="firstFrameNs"/>
    /// (CLOCK_MONOTONIC); <paramref name="latencyNs"/> is how long before this call that first frame was captured (the
    /// capture path's own latency, for the minimum delay).
    /// </summary>
    public void OnCapture(ReadOnlySpan<float> interleaved, long firstFrameNs, long latencyNs)
    {
        var frames = interleaved.Length / SampleRing.Channels;
        if (frames == 0)
        {
            return;
        }

        var s = Statistics;
        if (_captureEndNs != long.MinValue)
        {
            var gap = firstFrameNs - _captureEndNs;
            if (gap > _o.GapThresholdNs && gap <= _o.MaxGapFillNs)
            {
                var fill = (int)Math.Round(gap / NsPerFrame);
                _ring.WriteSilence(fill);
                s.CaptureGaps++;
                s.CaptureGapFrames += fill;
            }
            else if (gap > _o.MaxGapFillNs || gap < -_o.MaxGapFillNs)
            {
                // The capture timeline jumped (the source restarted): its clock is measured from here, and the ring is
                // cleared so nothing from before the outage can be played (faded in) when the line starts again.
                s.CaptureDiscontinuities++;
                s.FirstCaptureEndNs = 0;
                _ring.WriteSilence(_ring.Capacity);
            }
        }

        _ring.Write(interleaved);
        _captureEndNs = firstFrameNs + (long)Math.Round(frames * NsPerFrame);
        Volatile.Write(ref _captureLatencyNs, latencyNs);
        Volatile.Write(ref _captureFrames, frames);
        _timeline.Publish(_ring.Written, _captureEndNs);
        s.FramesCaptured += frames;
        s.CaptureBlocks++;
        if (Volatile.Read(ref _resetCaptureClock) != 0)
        {
            // The measured window began (the playback side asked): the card's clock is measured from here, after
            // PipeWire's own clock estimate has settled.
            Volatile.Write(ref _resetCaptureClock, 0);
            s.FirstCaptureEndNs = 0;
        }

        if (s.FirstCaptureEndNs == 0)
        {
            s.FirstCaptureEndNs = _captureEndNs;
            s.FirstCaptureEndFrame = _ring.Written;
        }

        s.LastCaptureEndNs = _captureEndNs;
        s.LastCaptureEndFrame = _ring.Written;
    }

    /// <summary>
    /// Playback side: fills <paramref name="output"/> with <paramref name="frames"/> interleaved stereo frames whose first
    /// will be heard at <paramref name="firstFrameHeardNs"/>; <paramref name="latencyNs"/> is the sink path's own latency
    /// (from now to heard).
    /// </summary>
    public void OnPlayback(Span<float> output, int frames, long firstFrameHeardNs, long latencyNs)
    {
        var s = Statistics;
        var block = output[..(frames * SampleRing.Channels)];
        if (s.FirstHeardNs == 0)
        {
            s.FirstHeardNs = firstFrameHeardNs;
            s.FirstHeardFrame = s.FramesRequested;
        }
        else if (Math.Abs(firstFrameHeardNs - (s.LastHeardNs + ((s.FramesRequested - s.LastHeardFrame) * NsPerFrame))) > 20_000_000)
        {
            // The sink's timeline jumped (moved to another sink, a stall): its clock is measured from here, and the delay
            // is put right at once with a cross-fade (not glided at 2 ms/s).
            s.SinkDiscontinuities++;
            s.FirstHeardNs = firstFrameHeardNs;
            s.FirstHeardFrame = s.FramesRequested;
            Volatile.Write(ref _relock, 1);
        }


        s.LastHeardNs = firstFrameHeardNs;
        s.LastHeardFrame = s.FramesRequested;
        s.PlaybackBlocks++;
        s.FramesRequested += frames;
        var dt = frames / (double)Rate;
        if (!_timeline.TryRead(out var endFrame, out var endNs))
        {
            block.Clear();
            SetState(DelayLineState.Waiting);
            return;
        }

        // The path's own latency changed (another sink or source, another quantum): put right at once, as above.
        var path = latencyNs + Volatile.Read(ref _captureLatencyNs);
        if (_lastPathNs != 0 && Math.Abs(path - _lastPathNs) > 1_000_000)
        {
            s.PathChanges++;
            Volatile.Write(ref _relock, 1);
        }

        _lastPathNs = path;
        var video = Volatile.Read(ref _videoLatencyNs);
        var requested = video + _o.TrimNs;

        // What this path can do at the least: the newest capture data must reach past this block's reads at every phase of
        // the two callbacks (the capture latency of a block's first frame plus the sink's latency, plus the block and the
        // kernel's lookahead, plus the margin), and never less than the worst phase seen recently.
        var needNs = ((frames * _maxRatio) + SincKernel.After + 1) * NsPerFrame;
        var phase = firstFrameHeardNs - endNs;
        var minimum = Math.Max(Volatile.Read(ref _captureLatencyNs) + latencyNs + needNs + _o.MarginNs, _peakPhaseNs + needNs + 500_000);
        var maximum = ((_o.RingFrames - _o.GuardFrames - (2 * frames)) * NsPerFrame) - Math.Max(0, phase);
        var target = Math.Clamp(requested, minimum, Math.Max(minimum, maximum));

        if (Volatile.Read(ref _muted) != 0 || (_o.SilentUntilVideoLatency && Volatile.Read(ref _videoLatencySetAtFrame) < 0))
        {
            Mute(block, frames, endFrame);
            return;
        }

        if (Current is DelayLineState.Waiting or DelayLineState.Muted or DelayLineState.Starved)
        {
            // (Re)start: the read position whose capture time is the target before the heard time, faded in; wait while
            // the data does not reach that far yet.
            if (phase + needNs > target)
            {
                block.Clear();
                return;
            }

            if (Current == DelayLineState.Starved)
            {
                s.Resyncs++;
            }

            _reader.Seek(endFrame - ((target - phase) / NsPerFrame));
            _reader.SetGain(0);
            _reader.RampGain(1, _o.FadeFrames);
            _peakPhaseNs = (long)phase;
            _peakPhaseAgeFrames = 0;
            _controller.Lock(target);
            _lastRatio = _controller.Ratio;
            SetState(DelayLineState.Playing);
        }

        if (Volatile.Read(ref _relock) != 0)
        {
            Volatile.Write(ref _relock, 0);
            var jump = phase + ((endFrame - _reader.Position) * NsPerFrame) - target;
            var to = _reader.Position + (jump / NsPerFrame);
            if (Math.Abs(jump) > 1_000_000 && !_reader.CrossFading && to >= _ring.OldestReadable(endFrame) + SincKernel.Before + 1
                && to + (frames * _maxRatio) + SincKernel.After + 1 <= endFrame)
            {
                _reader.CrossFadeTo(to, _o.CrossFadeFrames);
                s.Relocks++;
            }

            _controller.Lock(target);
        }

        // Overrun: the playback side fell so far behind that the frames it needs were overwritten.
        if (_reader.FirstFrameNeeded() < _ring.OldestReadable(endFrame))
        {
            s.Overruns++;
            block.Clear();
            _controller.Unlock();
            _reader.SetGain(0);
            SetState(DelayLineState.Starved);
            return;
        }

        var measured = phase + ((endFrame - _reader.Position) * NsPerFrame);
        var decision = _controller.Update(measured, target, dt);
        if (decision.JumpNs != 0)
        {
            var to = _reader.Position + (decision.JumpNs / NsPerFrame);
            var lowest = _ring.OldestReadable(endFrame) + SincKernel.Before + 1;
            if (to >= lowest)
            {
                _reader.CrossFadeTo(to, _o.CrossFadeFrames);
                s.Jumps++;
                measured -= decision.JumpNs;
            }

            _controller.Jumped();
        }

        // Underrun: the data does not reach the end of this block's reads. Play what is there, fading out, then silence.
        if (_reader.EndFrameNeeded(frames, Math.Max(_lastRatio, decision.Ratio)) > endFrame)
        {
            s.Underruns++;
            var available = (int)Math.Max(0, Math.Floor((endFrame - SincKernel.After - 1 - _reader.Position) / Math.Max(_lastRatio, decision.Ratio)));
            available = Math.Min(available, frames);
            if (available > 0 && !_reader.CrossFading)
            {
                _reader.RampGain(0, available);
                _reader.Render(_ring, block, available, _lastRatio, _lastRatio);
            }
            else
            {
                available = 0;
            }

            block[(available * SampleRing.Channels)..].Clear();
            _controller.Unlock();
            SetState(DelayLineState.Starved);
            return;
        }

        _reader.Render(_ring, block, frames, _lastRatio, decision.Ratio);
        _lastRatio = decision.Ratio;
        TrackPeakPhase(phase, frames); // only while playing: an outage's growing phase is not the path's
        Volatile.Write(ref _currentDelayNs, (long)measured);
        Record(s, frames, measured, target, requested, video, minimum, decision);
    }

    private void Mute(Span<float> block, int frames, long endFrame)
    {
        if (Current == DelayLineState.Playing && _reader.Gain > 0 && !_reader.CrossFading
            && _reader.FirstFrameNeeded() >= _ring.OldestReadable(endFrame)
            && _reader.EndFrameNeeded(frames, _lastRatio) <= endFrame)
        {
            // Fade out over this block, then silence.
            _reader.RampGain(0, Math.Min(frames, _o.FadeFrames));
            _reader.Render(_ring, block, frames, _lastRatio, _lastRatio);
        }
        else
        {
            block.Clear();
        }

        _controller.Unlock();
        SetState(DelayLineState.Muted);
        Statistics.MutedBlocks++;
    }

    private void TrackPeakPhase(double phase, int frames)
    {
        // The worst phase in roughly the last 30 s (held, then it decays to the newest).
        _peakPhaseAgeFrames += frames;
        if (phase >= _peakPhaseNs || _peakPhaseAgeFrames > 30L * Rate)
        {
            _peakPhaseNs = (long)phase;
            _peakPhaseAgeFrames = 0;
        }
    }

    private void Record(DelayLineStatistics s, int frames, double measured, double target, double requested, long video, double minimum, DelayDecision d)
    {
        s.FramesPlayed += frames;
        var settleFrom = Volatile.Read(ref _videoLatencySetAtFrame);
        if (settleFrom == 0)
        {
            // The first block after the first video latency: the settle time starts here. At its end the video latency
            // has had its running mean (the first estimate moves faster than the 2 ms/s glide can follow), so the line
            // moves to it once more with a cross-fade, and the statistics start a second after that.
            Volatile.Write(ref _videoLatencySetAtFrame, s.FramesPlayed);
            _settledRelockFrame = s.FramesPlayed + (long)(_o.SettleNs / NsPerFrame);
            _measureFromFrame = _settledRelockFrame + Rate;
        }

        if (s.FramesPlayed >= _settledRelockFrame)
        {
            _settledRelockFrame = long.MaxValue;
            Volatile.Write(ref _relock, 1);
        }

        s.LastRatio = d.Ratio;
        s.LastIntegral = _controller.IntegralTerm;
        s.LastTargetNs = target;
        s.LastMinimumNs = minimum;
        s.LastDelayNs = measured;
        if (s.FramesPlayed < _measureFromFrame)
        {
            return;
        }

        if (s.MeasuredBlocks == 0)
        {
            // Both clocks are measured over the window, not from the start (PipeWire's estimates take a few seconds).
            s.FirstHeardNs = s.LastHeardNs;
            s.FirstHeardFrame = s.LastHeardFrame;
            Volatile.Write(ref _resetCaptureClock, 1);
        }

        s.MeasuredBlocks++;
        s.MeasuredFrames += frames;
        s.DelaySum += measured;
        s.DelayMin = Math.Min(s.DelayMin, measured);
        s.DelayMax = Math.Max(s.DelayMax, measured);
        var filtered = d.TargetNs + d.FilteredErrorNs;
        s.FilteredDelayMin = Math.Min(s.FilteredDelayMin, filtered);
        s.FilteredDelayMax = Math.Max(s.FilteredDelayMax, filtered);
        var error = measured - target;
        s.ErrorSum += error;
        s.ErrorSquares += error * error;
        s.ErrorMin = Math.Min(s.ErrorMin, error);
        s.ErrorMax = Math.Max(s.ErrorMax, error);
        s.TargetSum += target;
        s.TargetMin = Math.Min(s.TargetMin, target);
        s.TargetMax = Math.Max(s.TargetMax, target);
        s.VideoSum += video;
        var offset = measured - video;
        s.OffsetSum += offset;
        s.OffsetMin = Math.Min(s.OffsetMin, offset);
        s.OffsetMax = Math.Max(s.OffsetMax, offset);
        s.RatioSum += d.Ratio;
        s.RatioMin = Math.Min(s.RatioMin, d.Ratio);
        s.RatioMax = Math.Max(s.RatioMax, d.Ratio);
        s.MinimumSum += minimum;
        if (requested < minimum)
        {
            s.ShortBlocks++;
            s.ShortfallSum += minimum - requested;
        }
    }

    private DelayLineState Current => (DelayLineState)_state;

    private void SetState(DelayLineState state) => Volatile.Write(ref _state, (int)state);
}

/// <summary>
/// The delay line's counters and its measured window (from the settle time after the first video latency). Written by
/// the capture and playback sides (each field by one of them), read once both have stopped.
/// </summary>
public sealed class DelayLineStatistics
{
    public long CaptureBlocks { get; set; }

    public long FramesCaptured { get; set; }

    public long CaptureGaps { get; set; }

    public long CaptureGapFrames { get; set; }

    public long CaptureDiscontinuities { get; set; }

    public long FirstCaptureEndNs { get; set; }

    public long FirstCaptureEndFrame { get; set; }

    public long LastCaptureEndNs { get; set; }

    public long LastCaptureEndFrame { get; set; }

    public long PlaybackBlocks { get; set; }

    public long SinkDiscontinuities { get; set; }

    /// <summary>Changes of more than 1 ms in the path's own latency (capture plus sink): another sink, source or quantum.</summary>
    public long PathChanges { get; set; }

    /// <summary>
    /// Cross-faded moves straight to the target that were more than 1 ms: at the first video latency, once it has settled,
    /// and after a sink discontinuity or a path change.
    /// </summary>
    public long Relocks { get; set; }

    public long FirstHeardNs { get; set; }

    public long FirstHeardFrame { get; set; }

    public long LastHeardNs { get; set; }

    public long LastHeardFrame { get; set; }

    public long FramesRequested { get; set; }

    /// <summary>Frames played from the ring (not silence for want of data or on mute).</summary>
    public long FramesPlayed { get; set; }

    public long MutedBlocks { get; set; }

    public long Underruns { get; set; }

    public long Overruns { get; set; }

    /// <summary>Restarts after the data ran out (an underrun, the source gone and back).</summary>
    public long Resyncs { get; set; }

    /// <summary>Cross-faded corrections of a large error (a sink switch, a stall).</summary>
    public long Jumps { get; set; }

    public double LastRatio { get; set; } = 1;

    public double LastIntegral { get; set; }

    public double LastTargetNs { get; set; }

    public double LastMinimumNs { get; set; }

    public double LastDelayNs { get; set; }

    public long MeasuredBlocks { get; set; }

    public long MeasuredFrames { get; set; }

    public double DelaySum { get; set; }

    public double DelayMin { get; set; } = double.MaxValue;

    public double DelayMax { get; set; } = double.MinValue;

    /// <summary>The delay low-passed as the controller sees it (0.5 s): its extremes, without the timestamps' jitter.</summary>
    public double FilteredDelayMin { get; set; } = double.MaxValue;

    public double FilteredDelayMax { get; set; } = double.MinValue;

    public double ErrorSum { get; set; }

    public double ErrorSquares { get; set; }

    public double ErrorMin { get; set; } = double.MaxValue;

    public double ErrorMax { get; set; } = double.MinValue;

    public double TargetSum { get; set; }

    public double TargetMin { get; set; } = double.MaxValue;

    public double TargetMax { get; set; } = double.MinValue;

    public double VideoSum { get; set; }

    public double OffsetSum { get; set; }

    public double OffsetMin { get; set; } = double.MaxValue;

    public double OffsetMax { get; set; } = double.MinValue;

    public double RatioSum { get; set; }

    public double RatioMin { get; set; } = double.MaxValue;

    public double RatioMax { get; set; } = double.MinValue;

    public double MinimumSum { get; set; }

    /// <summary>Measured blocks where the target asked for was below what the path can do (the sound lags by the shortfall).</summary>
    public long ShortBlocks { get; set; }

    public double ShortfallSum { get; set; }

    public double DelayMean => MeasuredBlocks == 0 ? 0 : DelaySum / MeasuredBlocks;

    public double ErrorMean => MeasuredBlocks == 0 ? 0 : ErrorSum / MeasuredBlocks;

    public double ErrorRms => MeasuredBlocks == 0 ? 0 : Math.Sqrt(ErrorSquares / MeasuredBlocks);

    public double TargetMean => MeasuredBlocks == 0 ? 0 : TargetSum / MeasuredBlocks;

    public double VideoMean => MeasuredBlocks == 0 ? 0 : VideoSum / MeasuredBlocks;

    public double OffsetMean => MeasuredBlocks == 0 ? 0 : OffsetSum / MeasuredBlocks;

    public double RatioMean => MeasuredBlocks == 0 ? 1 : RatioSum / MeasuredBlocks;

    public double MinimumMean => MeasuredBlocks == 0 ? 0 : MinimumSum / MeasuredBlocks;

    public double ShortfallMean => ShortBlocks == 0 ? 0 : ShortfallSum / ShortBlocks;

    /// <summary>The capture clock against CLOCK_MONOTONIC over the run, ppm (frames written per second of capture time); NaN when too short.</summary>
    public double CaptureClockPpm => ClockPpm(LastCaptureEndFrame - FirstCaptureEndFrame, LastCaptureEndNs - FirstCaptureEndNs);

    /// <summary>The sink's clock against CLOCK_MONOTONIC over the run, ppm (frames taken per second of heard time); NaN when too short.</summary>
    public double SinkClockPpm => ClockPpm(LastHeardFrame - FirstHeardFrame, LastHeardNs - FirstHeardNs);

    private static double ClockPpm(long frames, long ns) => ns < 10_000_000_000 ? double.NaN : ((frames / (ns / 1e9) / DelayLine.Rate) - 1) * 1e6;
}
