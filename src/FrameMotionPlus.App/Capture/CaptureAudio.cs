using System.Globalization;
using FrameMotionPlus.Audio;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Capture;

/// <summary>
/// <c>fmp capture</c>'s sound: the capture device's audio source (the sound card on the same USB device, or
/// <c>--audio-source</c>) through a <see cref="PipeWireAudioDelay"/> to the default sink (or <c>--audio-sink</c>), delayed by
/// the measured video latency plus <c>--audio-offset-ms</c>. Started before the video opens (so it is streaming by the
/// time the setup is printed); the session feeds it the latency and stops it; disposed after the run. Anything that keeps
/// it from starting leaves it off with one line: the video always runs.
/// </summary>
internal sealed class CaptureAudio : IDisposable
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    private readonly AudioChoice _choice;
    private AudioReport? _report;
    private bool _disposed;

    private CaptureAudio(AudioChoice choice, PipeWireAudioDelay? delay, string? offReason)
    {
        _choice = choice;
        Delay = delay;
        OffReason = offReason;
    }

    /// <summary>The running delay line, or null when audio is off.</summary>
    public PipeWireAudioDelay? Delay { get; }

    /// <summary>Why audio is off (null when on).</summary>
    public string? OffReason { get; }

    /// <summary>Resolves the source and sink and starts the streams; never throws for a missing piece (audio is then off).</summary>
    public static CaptureAudio Start(CaptureOptions options, string device, TextWriter events)
    {
        if (options.Audio == AudioChoice.Off)
        {
            return new CaptureAudio(options.Audio, null, "--audio off");
        }

        try
        {
            if (!AudioDevices.IsAvailable(out var why))
            {
                return new CaptureAudio(options.Audio, null, why);
            }

            var source = options.AudioSource;
            if (source is null)
            {
                if (!AudioDevices.TrySnapshot(out var graph, out why))
                {
                    return new CaptureAudio(options.Audio, null, why);
                }

                source = AudioDevices.SourceForVideoDevice(graph, device)?.Name;
                if (source is null)
                {
                    return new CaptureAudio(options.Audio, null, $"no audio source goes with {device} (no sound card on the same USB device; --audio-source names one)");
                }
            }

            var delayOptions = new AudioDelayOptions
            {
                Source = source,
                Sink = options.AudioSink,
                TrimNs = Nanoseconds.FromMilliseconds(options.AudioOffsetMs),
                OnEvent = e => events.WriteLine($"fmp: audio: {e}"),
                LogIntervalS = double.TryParse(Environment.GetEnvironmentVariable("FMP_AUDIO_LOG"), NumberStyles.Float, Ic, out var every) ? every : 0,
                OnLog = line => events.WriteLine($"fmp: audio: {line}"),
            };
            if (!PipeWireAudioDelay.TryCreate(delayOptions, out var delay, out why))
            {
                return new CaptureAudio(options.Audio, null, why);
            }

            try
            {
                delay.Start();
            }
            catch
            {
                delay.Dispose();
                throw;
            }

            return new CaptureAudio(options.Audio, delay, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new CaptureAudio(options.Audio, null, $"could not start: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The audio lines of the setup (waits up to 1.5 s for the streams, which started before the video opened).</summary>
    public void PrintSetup(TextWriter o, CaptureOptions options)
    {
        if (Delay is null)
        {
            o.WriteLine($"  audio       {(_choice == AudioChoice.On ? "OFF" : "off")}: {OffReason}");
            return;
        }

        var s = Delay.StartAndDescribe();
        var sink = $"{s.SinkDescription ?? s.Sink} ({s.Sink})";
        o.WriteLine($"  audio       {s.SourceDescription ?? s.Source} ({s.Source}) → {sink}" +
            $"{(s.AbsentSelectedDefault is { } absent ? $": the system's current default output, because the one you selected ({absent}) is not present; the sound moves to it when it appears"
                : s.FromDefault ? ": your selected default output (select another and the sound follows)" : ": as --audio-sink asked")}; " +
            $"it plays to no other output device (a recorder such as a screen share may take a copy): muted (and said) if it goes away, never moved elsewhere{(s.SinkLinked ? "" : "; NOT linked to it yet")}");
        o.WriteLine(string.Create(Ic,
            $"              F32 stereo 48 kHz through PipeWire {s.LibraryVersion} (it converts the card's own format); {(s.Streaming ? "streaming" : "NOT streaming yet")}; " +
            $"quantum {s.Quantum} frames ({s.Quantum / 48.0:0.0} ms); reported latency: capture {Ms(s.CaptureLatencyNs)} ms, sink {Ms(s.SinkLatencyNs)} ms " +
            $"(its delay to the device plus the cycle a queued buffer waits); the least delay this path holds: {Ms(s.MinimumDelayNs)} ms"));
        o.WriteLine(string.Create(Ic,
            $"              the sound follows the measured video latency (capture timestamp → first pixel) {(options.AudioOffsetMs == 0 ? "with no trim" : $"plus --audio-offset-ms {options.AudioOffsetMs:0.###}")}. " +
            $"Heard another way too (the TV on the card's HDMI out, a headset on the controller)? That sound is not delayed and leads the picture: mute it there."));
    }

    /// <summary>Stops and releases the streams (bounded); the report for the summary.</summary>
    public AudioReport? Finish()
    {
        if (Delay is null)
        {
            return null;
        }

        if (!_disposed)
        {
            _disposed = true;
            Delay.Dispose();
            _report = Delay.Report();
        }

        return _report;
    }

    /// <summary>
    /// The audio lines of the summary; problems go to <paramref name="warnings"/>. <paramref name="videoLatencyMeanNs"/> is
    /// the video's own mean latency over its measured window (a real frame's first pixel less its capture timestamp).
    /// </summary>
    public void PrintSummary(TextWriter o, List<string> warnings, double videoLatencyMeanNs = 0)
    {
        var r = Finish();
        if (r is null)
        {
            // The reason was printed once, with the setup (or by --list).
            o.WriteLine($"  audio       {(_choice == AudioChoice.On ? "OFF" : "off")} (the reason is in the setup above)");
            if (_choice == AudioChoice.On)
            {
                warnings.Add("audio was asked for and did not start (the reason is in the setup above)");
            }

            return;
        }

        var s = r.Statistics;
        var played = s.FramesPlayed / 48_000.0;
        o.WriteLine(string.Create(Ic,
            $"  audio       {played:0.0} s played from {r.Setup.SourceDescription ?? r.Setup.Source} to {r.FinalSink ?? r.Setup.Sink ?? "no sink"}" +
            $"{(r.SinkChanges > 0 ? $" (moved {r.SinkChanges}x as you selected another default output)" : "")}" +
            $"{(r.SinkLosses > 0 ? $"; the sink went away {r.SinkLosses}x (muted until it was back)" : "")}" +
            $"{(r.WrongSinkLinks > 0 ? $"; linked to another output device {r.WrongSinkLinks}x (muted and cut each time)" : "")}"));
        if (s.MeasuredBlocks == 0)
        {
            o.WriteLine("              nothing measured (the run ended before the sound had followed the video's latency for 6 s)");
        }
        else
        {
            o.WriteLine(string.Create(Ic,
                $"              delay target (video latency {Ms(s.VideoMean)} + trim {Ms(r.TrimNs)}{(s.ShortBlocks > 0 ? ", raised to what the path can do" : "")}): mean {Ms(s.TargetMean)} ms " +
                $"(min {Ms(s.TargetMin)}, max {Ms(s.TargetMax)}); achieved mean {Ms(s.DelayMean)} ms, low-passed min {Ms(s.FilteredDelayMin)}, max {Ms(s.FilteredDelayMax)} " +
                $"(per block {Ms(s.DelayMin)} to {Ms(s.DelayMax)}: timestamp jitter); error mean {Ms(s.ErrorMean)} ms, rms {Ms(s.ErrorRms)}"));
            var againstRun = videoLatencyMeanNs > 0
                ? string.Create(Ic, $"; against the video's own mean over its measured window ({Ms(videoLatencyMeanNs)} ms): {Ms(s.DelayMean - videoLatencyMeanNs)} ms")
                : "";
            o.WriteLine(string.Create(Ic,
                $"              A/V offset from timestamps: the sound is heard {Ms(s.OffsetMean)} ms after its picture (mean against the latency followed; min {Ms(s.OffsetMin)}, max {Ms(s.OffsetMax)}" +
                $"{againstRun}; positive = later, the trim included); it excludes the card's own audio-versus-video delay, the TV's processing of either and the distance to the speakers"));
            if (s.ShortBlocks > 0)
            {
                o.WriteLine(string.Create(Ic,
                    $"              the path cannot go below {Ms(s.MinimumMean)} ms: in {100.0 * s.ShortBlocks / s.MeasuredBlocks:0.#}% of the run the sound lagged its picture by {Ms(s.ShortfallMean)} ms (mean)"));
                warnings.Add(string.Create(Ic, $"the sound lags the picture by {Ms(s.ShortfallMean)} ms: this sink's path is slower than the video"));
            }
        }

        var stages = string.Create(Ic,
            $"capture {Ms(r.CaptureLatencyMeanNs)} + delay line {Ms(s.DelayMean - r.CaptureLatencyMeanNs - r.SinkLatencyMeanNs)} + sink {Ms(r.SinkLatencyMeanNs)} ms");
        var clocks = double.IsNaN(s.CaptureClockPpm) || double.IsNaN(s.SinkClockPpm)
            ? "clocks not measured (too short)"
            : string.Create(Ic, $"clocks against CLOCK_MONOTONIC: card {s.CaptureClockPpm:+0.0;-0.0} ppm, sink {s.SinkClockPpm:+0.0;-0.0} ppm (difference {(s.CaptureClockPpm - s.SinkClockPpm):+0.0;-0.0})");
        o.WriteLine(string.Create(Ic,
            $"              stages {stages}; drift correction {(s.RatioMean - 1) * 1e6:+0.0;-0.0} ppm (learned {s.LastIntegral * 1e6:+0.0;-0.0}; ratio {(s.RatioMin - 1) * 1e6:+0.0;-0.0} to {(s.RatioMax - 1) * 1e6:+0.0;-0.0}); {clocks}"));
        o.WriteLine(string.Create(Ic,
            $"              underruns {s.Underruns}, overruns {s.Overruns}, resyncs {s.Resyncs}, jumps {s.Jumps}, re-locks {s.Relocks} (path changes {s.PathChanges}, sink discontinuities {s.SinkDiscontinuities}), " +
            $"capture gaps {s.CaptureGaps} ({s.CaptureGapFrames} frames), " +
            $"capture discontinuities {s.CaptureDiscontinuities}, late sink cycles {r.LateCycles}, muted blocks {s.MutedBlocks}; source lost {r.SourceLosses}x, " +
            $"captured again {r.CaptureRestarts}x; data-thread callbacks allocated {r.CallbackAllocatedBytes} B ({r.CaptureCycles} + {r.PlaybackCycles} cycles)"));
        foreach (var e in r.Events)
        {
            o.WriteLine($"              {e}");
        }

        if (r.CallbackAllocatedBytes > 0)
        {
            warnings.Add($"the audio callbacks allocated {r.CallbackAllocatedBytes} B");
        }

        if (s.Underruns + s.Overruns > 0)
        {
            warnings.Add($"audio: {s.Underruns} underrun(s), {s.Overruns} overrun(s)");
        }

        if (r.Fault is { } fault)
        {
            warnings.Add($"audio stopped on a fault: {fault}");
        }

        if (!r.TornDown)
        {
            warnings.Add("the audio teardown did not finish in 2 s (left to the process exit)");
        }

        if (r.ConnectionLost)
        {
            warnings.Add("the PipeWire connection was lost during the run");
        }
    }

    public void Dispose() => Finish();

    private static string Ms(double ns) => (ns / 1e6).ToString("0.00", Ic);
}
