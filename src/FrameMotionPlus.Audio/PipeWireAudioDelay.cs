using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FrameMotionPlus.Audio.Dsp;
using FrameMotionPlus.Audio.PipeWire;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Threading;

namespace FrameMotionPlus.Audio;

/// <summary>What a <see cref="PipeWireAudioDelay"/> connected to, as it started.</summary>
public sealed record AudioSetup
{
    public required string LibraryVersion { get; init; }

    public required string Source { get; init; }

    public string? SourceDescription { get; init; }

    /// <summary>The sink the sound plays to: named, or the configured default at the start. It plays to no other output device (a recorder may take a copy).</summary>
    public string? Sink { get; init; }

    public string? SinkDescription { get; init; }

    /// <summary>The sink was not named: it is the user's configured default at the start (re-pinned if they change it).</summary>
    public bool FromDefault { get; init; }

    /// <summary>
    /// The default output the user selected, when the sound could not start there because it was not present: <see cref="Sink"/>
    /// is then the system's current default instead, and the sound moves to the selected one when it appears. Null otherwise.
    /// </summary>
    public string? AbsentSelectedDefault { get; init; }

    /// <summary>The playback stream is linked to <see cref="Sink"/> (and no other output device) as it starts.</summary>
    public bool SinkLinked { get; init; }

    public bool Streaming { get; init; }

    /// <summary>The capture path's latency PipeWire reports (age of a quantum's first frame at its cycle), ns.</summary>
    public long CaptureLatencyNs { get; init; }

    /// <summary>The sink path's latency PipeWire reports (a frame handed over at a cycle is heard this much later), ns.</summary>
    public long SinkLatencyNs { get; init; }

    /// <summary>Frames per graph cycle on the sink's side.</summary>
    public int Quantum { get; init; }

    /// <summary>The least total delay this path can hold (both latencies, a block of lookahead and a margin), ns.</summary>
    public long MinimumDelayNs { get; init; }
}

/// <summary>What a <see cref="PipeWireAudioDelay"/> did, once disposed.</summary>
public sealed record AudioReport
{
    public required AudioSetup Setup { get; init; }

    public required DelayLineStatistics Statistics { get; init; }

    public long TrimNs { get; init; }

    public double CaptureLatencyMeanNs { get; init; }

    public double SinkLatencyMeanNs { get; init; }

    public long CaptureCycles { get; init; }

    public long PlaybackCycles { get; init; }

    /// <summary>Sink cycles our callback was late for (the sink played silence for us there).</summary>
    public long LateCycles { get; init; }

    /// <summary>Bytes allocated inside the real-time callbacks after their warm-up (must be 0).</summary>
    public long CallbackAllocatedBytes { get; init; }

    /// <summary>Times the sound was re-pinned to a newly configured default sink (no sink named).</summary>
    public int SinkChanges { get; init; }

    public string? FinalSink { get; init; }

    /// <summary>Times the chosen sink went away (the sound muted until it came back).</summary>
    public int SinkLosses { get; init; }

    /// <summary>Times the playback stream was found linked to another output device than the chosen sink, or to a node still not announced after the registry was read again (muted and cut).</summary>
    public int WrongSinkLinks { get; init; }

    /// <summary>
    /// Times the playback stream was found linked to a node the registry had not announced and the guard read the whole
    /// registry again before judging it (muted meanwhile; <see cref="UnannouncedRecorders"/> of them were then recorders).
    /// </summary>
    public int RegistryResyncs { get; init; }

    /// <summary>Of <see cref="RegistryResyncs"/>: the node was a recorder after all (the sound resumed; nothing was cut).</summary>
    public int UnannouncedRecorders { get; init; }

    /// <summary>Registry objects (or metadata properties) whose handling threw on the guard's connection (each missed by its model).</summary>
    public int RegistryHandlerErrors { get; init; }

    public int SourceLosses { get; init; }

    public int CaptureRestarts { get; init; }

    public int PlaybackRestarts { get; init; }

    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>A process callback threw (a bug; it stopped processing): its message.</summary>
    public string? Fault { get; init; }

    /// <summary>False when the teardown did not finish in time (the streams were left to the process exit).</summary>
    public bool TornDown { get; init; }

    public bool ConnectionLost { get; init; }
}

/// <summary>
/// The capture card's sound, delayed to its picture: a PipeWire capture stream on the card's audio source and a playback
/// stream to the chosen sink (by default the default sink, followed when it changes), joined by a <see cref="DelayLine"/>
/// whose total delay (capture time to heard, from PipeWire's timestamps) follows the video latency given to
/// <see cref="SetTargetDelay"/> plus the trim. Both process callbacks run on PipeWire's data thread and only touch the
/// delay line. The sink is resolved once at the start (the named one, or the user's configured default) and the playback
/// stream is pinned to it (target.object, node.dont-reconnect, node.dont-move, node.dont-fallback: WirePlumber neither
/// follows the default with it nor moves it on request). A supervisor thread (not real-time, sleeping 50 ms between looks)
/// watches the graph: the sound is muted unless the capture stream is linked to exactly the intended source (never a
/// microphone instead) and the playback stream to the chosen sink and no other output device (never a headset instead;
/// a recorder taking a copy, such as a screen share's sound, is left alone); a wrong link is cut;
/// a source or sink that goes away and comes back is connected again. <see cref="Stop"/> fades out and returns at once;
/// <see cref="Dispose"/> waits (bounded) for the teardown, after which nothing of it is left in the PipeWire graph.
/// </summary>
public sealed class PipeWireAudioDelay : IAudioDelay
{
    private const long RetryNs = 2 * Nanoseconds.PerSecond;

    /// <summary>A stream found linked to the wrong node is re-made after this, doubling each time up to <see cref="RetryNs"/>.</summary>
    private const long WrongLinkBackoffNs = 250 * Nanoseconds.PerMillisecond;

    /// <summary>
    /// The longest a resync of the registry may wait for the server (a fresh registry announcing everything takes a few
    /// milliseconds on a desktop): the bound on how long a peer not announced keeps the sound muted before it is judged.
    /// With a server that does not answer the wait ends after about a second, not this: the loop's timed wait counts in
    /// whole seconds and the deadline is checked when it wakes. A resync that timed out concludes nothing (the stream is cut).
    /// </summary>
    internal const int ResyncTimeoutMs = 250;

    private readonly AudioDelayOptions _o;
    private readonly DelayLine _line;
    private readonly PwConnection _connection;
    private readonly List<string> _events = [];
    private readonly Lock _eventsGate = new();
    private CaptureStream? _capture;
    private PlaybackStream? _playback;
    private DedicatedThread? _supervisor;
    private int _stop;
    private bool _started;
    private bool _disposed;
    private bool _tornDown;
    private AudioSetup? _setup;

    // Supervisor state (its thread only).
    private int _seenGraph = -1;
    private int _seenCaptureState = int.MinValue;
    private int _seenPlaybackState = int.MinValue;
    private string _sink;
    private bool _sourcePresent = true;
    private bool _sinkPresent = true;
    private bool _captureLinked;
    private bool _playbackLinked;
    private bool _playbackOk;
    private bool _playbackWasOk;
    private long _captureUnlinkedSinceNs;
    private long _playbackUnlinkedSinceNs;
    private long _nextCaptureRetryNs;
    private long _nextPlaybackRetryNs;
    private long _captureWrongBackoffNs = WrongLinkBackoffNs;
    private long _playbackWrongBackoffNs = WrongLinkBackoffNs;
    private long _nextCaptureWrongNs;
    private long _nextPlaybackWrongNs;
    private int _sinkChanges;
    private int _sinkLosses;
    private int _wrongSinkLinks;
    private int _registryResyncs;
    private int _unannouncedRecorders;
    private bool _handlerErrorsReported;

    // Peers not announced that a resync has already been spent on (each gets one while it stays linked).
    private readonly HashSet<uint> _resyncedPeers = [];
    private int _recorders;
    private int _sourceLosses;
    private int _captureRestarts;
    private int _playbackRestarts;
    private bool _lostReported;
    private string? _fault;
    private long _nextLogNs;
    private long _startedNs;

    // Totals of streams replaced during the run.
    private long _retiredAllocated;
    private long _retiredLate;

    private PipeWireAudioDelay(AudioDelayOptions options, PwConnection connection, string sink)
    {
        _o = options;
        _connection = connection;
        _sink = sink;
        _line = new DelayLine(new DelayLineOptions
        {
            TrimNs = options.TrimNs,
            InitialVideoLatencyNs = options.InitialVideoLatencyNs,
            SettleNs = options.SettleNs,
            SilentUntilVideoLatency = options.SilentUntilVideo,
        });
        _line.SetMuted(true); // until the supervisor has seen the capture linked to the intended source
    }

    /// <summary>The delay line (tests; the statistics).</summary>
    public DelayLine Line => _line;

    /// <summary>The playback stream's node id now (tests: what it is linked to), or PW_ID_ANY.</summary>
    internal uint PlaybackNodeId
    {
        get
        {
            using (_connection.Lock())
            {
                return _playback?.NodeIdLocked() ?? PwDefs.IdAny;
            }
        }
    }

    public long CurrentDelayNs => _line.CurrentDelayNs;

    /// <summary>Tests: the guard connection's registry events (<see cref="AudioDelayOptions.RegistryLogCapacity"/>), oldest first.</summary>
    internal List<string> RegistryLog()
    {
        if (_connection.Lost || _tornDown)
        {
            return [];
        }

        using (_connection.Lock())
        {
            return _connection.RegistryLogLocked();
        }
    }

    /// <summary>What it connected to (after <see cref="Start"/>).</summary>
    public AudioSetup? Setup => _setup;

    /// <summary>
    /// Connects to PipeWire, checks that <see cref="AudioDelayOptions.Source"/> exists and resolves the sink once: the named
    /// one, else the user's configured default (default.configured.audio.sink), else WirePlumber's current default. False
    /// with one line when audio cannot work here: not Linux, no libpipewire, no server, no such node.
    /// </summary>
    public static bool TryCreate(AudioDelayOptions options, [NotNullWhen(true)] out PipeWireAudioDelay? delay, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        delay = null;
        if (!PwConnection.TryConnect("fmp-audio", out var connection, out reason, registryLogCapacity: options.RegistryLogCapacity))
        {
            return false;
        }

        connection.HideNodeOnceForTests = options.HideNodeOnceForTests;
        connection.HideNodeAlwaysForTests = options.HideNodeAlwaysForTests;

        string? sink = null;
        using (connection.Lock())
        {
            var source = connection.FindNodeLocked(options.Source);
            if (source is null || (!options.SourceIsSinkMonitor && !source.IsSource) || (options.SourceIsSinkMonitor && !source.IsSink))
            {
                reason = $"no PipeWire audio {(options.SourceIsSinkMonitor ? "sink" : "source")} named {options.Source}";
            }
            else if (options.Sink is { } named)
            {
                sink = ResolveSink([.. connection.NodesLocked().Where(n => n.IsSink).Select(n => (n.Name, n.Description))], named, out reason);
            }
            else
            {
                sink = connection.DefaultConfiguredSink is { } configured && connection.FindNodeLocked(configured) is { IsSink: true } ? configured
                    : connection.DefaultSink is { } current && connection.FindNodeLocked(current) is { IsSink: true } ? current
                    : null;
                reason = sink is null ? "PipeWire has no default sink to play to (--audio-sink names one)" : null;
            }
        }

        if (reason is not null || sink is null)
        {
            connection.Dispose();
            reason ??= "no sink";
            return false;
        }

        delay = new PipeWireAudioDelay(options, connection, sink);
        return true;
    }

    /// <summary>
    /// The sink <paramref name="asked"/> names: a node name exactly, else the one sink whose name or description contains it
    /// (ignoring case: "inzone" finds the INZONE headset). Null with one line when none or several match (listed).
    /// </summary>
    internal static string? ResolveSink(IReadOnlyList<(string Name, string? Description)> sinks, string asked, out string? reason)
    {
        reason = null;
        foreach (var (name, _) in sinks)
        {
            if (name == asked)
            {
                return name;
            }
        }

        var matches = sinks.Where(s => s.Name.Contains(asked, StringComparison.OrdinalIgnoreCase)
            || (s.Description?.Contains(asked, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (matches.Count == 1)
        {
            return matches[0].Name;
        }

        reason = matches.Count == 0
            ? $"no PipeWire audio sink matches '{asked}' (fmp capture --list shows the sinks)"
            : $"--audio-sink '{asked}' matches {matches.Count} sinks: {string.Join("; ", matches.Select(m => m.Description is null ? m.Name : $"{m.Name} ({m.Description})"))}: give more of a name";
        return null;
    }

    /// <summary>Connects both streams and waits (up to <paramref name="waitMs"/>) until they stream; returns what it got.</summary>
    public AudioSetup StartAndDescribe(int waitMs = 1500)
    {
        Start();
        var deadline = Clock.NowNs + (waitMs * Nanoseconds.PerMillisecond);
        while (Clock.NowNs < deadline && !(Streaming && _playback!.LastLatencyNs > 0 && _capture?.LastDelayNs > 0 && Volatile.Read(ref _playbackLinked)))
        {
            Thread.Sleep(10);
        }

        Thread.Sleep(30); // a few blocks: the minimum delay is known
        _setup = Describe();
        return _setup;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _startedNs = Clock.NowNs;
        _capture = NewCapture();
        _playback = NewPlayback();
        _ = _capture.Connect(capture: true);
        _ = _playback.Connect(capture: false);
        _supervisor = DedicatedThread.Start("fmp-audio", ThreadSchedulingRequest.Default, Supervise);
    }

    /// <summary>The video latency to follow (capture timestamp to first pixel), ns; the trim is added here. Any thread, allocation-free.</summary>
    public void SetTargetDelay(long delayNs) => _line.SetVideoLatency(delayNs);

    /// <summary>Fades the sound out and starts the teardown; returns at once (any thread).</summary>
    public void Stop()
    {
        _line.SetMuted(true);
        Volatile.Write(ref _stop, 1);
    }

    /// <summary>Stops, waits up to 2 s for the teardown, frees. Safe to call again.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        if (_supervisor is { } s)
        {
            if (s.Join(TimeSpan.FromSeconds(2)))
            {
                _tornDown = true;
            }
        }
        else
        {
            // No supervisor (never started, or Start failed part-way): whatever was made goes here.
            Teardown();
            _tornDown = true;
        }
    }

    /// <summary>What happened (after <see cref="Dispose"/>; before, a snapshot).</summary>
    public AudioReport Report()
    {
        string[] events;
        lock (_eventsGate)
        {
            events = [.. _events];
        }

        return new AudioReport
        {
            Setup = _setup ?? Describe(),
            Statistics = _line.Statistics,
            TrimNs = _o.TrimNs,
            CaptureLatencyMeanNs = _capture?.MeanDelayNs ?? 0,
            SinkLatencyMeanNs = _playback?.MeanLatencyNs ?? 0,
            CaptureCycles = _capture?.Cycles ?? 0,
            PlaybackCycles = _playback?.Cycles ?? 0,
            LateCycles = _retiredLate + (_playback?.LateCycles ?? 0),
            CallbackAllocatedBytes = _retiredAllocated + (_capture?.AllocatedBytes ?? 0) + (_playback?.AllocatedBytes ?? 0),
            SinkChanges = _sinkChanges,
            FinalSink = Volatile.Read(ref _sink),
            SinkLosses = _sinkLosses,
            WrongSinkLinks = _wrongSinkLinks,
            RegistryResyncs = _registryResyncs,
            UnannouncedRecorders = _unannouncedRecorders,
            RegistryHandlerErrors = _connection.HandlerErrors,
            SourceLosses = _sourceLosses,
            CaptureRestarts = _captureRestarts,
            PlaybackRestarts = _playbackRestarts,
            Events = events,
            Fault = _fault ?? _capture?.Fault?.Message ?? _playback?.Fault?.Message,
            TornDown = _tornDown,
            ConnectionLost = _connection.Lost,
        };
    }

    private bool Streaming => _capture?.State == PwDefs.StreamStateStreaming && _playback?.State == PwDefs.StreamStateStreaming;

    private AudioSetup Describe()
    {
        string? sourceDescription = null, sinkDescription = null, absentSelected = null;
        var sink = Volatile.Read(ref _sink);
        if (!_connection.Lost && !_tornDown)
        {
            using (_connection.Lock())
            {
                sourceDescription = _connection.FindNodeLocked(_o.Source)?.Description;
                sinkDescription = sink is null ? null : _connection.FindNodeLocked(sink)?.Description;
                if (_o.Sink is null && _connection.DefaultConfiguredSink is { } selected && selected != sink)
                {
                    absentSelected = selected;
                }
            }
        }

        var s = _line.Statistics;
        return new AudioSetup
        {
            LibraryVersion = PwLibrary.Version ?? "?",
            Source = _o.Source,
            SourceDescription = sourceDescription,
            Sink = sink,
            SinkDescription = sinkDescription,
            FromDefault = _o.Sink is null,
            AbsentSelectedDefault = absentSelected,
            SinkLinked = Volatile.Read(ref _playbackLinked),
            Streaming = Streaming,
            CaptureLatencyNs = _capture?.LastDelayNs ?? 0,
            SinkLatencyNs = _playback?.LastLatencyNs ?? 0,
            Quantum = _playback?.LastQuantum ?? 0,
            MinimumDelayNs = (long)s.LastMinimumNs,
        };
    }

    private List<KeyValuePair<string, string>> CommonProperties(string category, string node, string description) =>
    [
        new(PwKeys.MediaType, "Audio"),
        new(PwKeys.MediaCategory, category),
        new(PwKeys.MediaName, description),
        new(PwKeys.NodeName, node),
        new(PwKeys.NodeDescription, description),
        new(PwKeys.AppName, "fmp"),
        // Which process this stream belongs to: a screen share that takes an application's sound (Discord's reads the
        // sink inputs through the PulseAudio API and groups them by process) finds the stream by these.
        new("application.process.id", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
        new("application.process.binary", Path.GetFileName(Environment.ProcessPath) ?? "fmp"),
        new(PwKeys.NodeLatency, string.Create(CultureInfo.InvariantCulture, $"{_o.Quantum}/{PwStream.Rate}")),
        // Nothing of this stream is saved in WirePlumber's state (volume, target): it plays at unity every time.
        new("state.restore-props", "false"),
        new("state.restore-target", "false"),
    ];

    private CaptureStream NewCapture()
    {
        var props = CommonProperties("Capture", _o.NodePrefix + "-capture", "Frame Motion Plus: capture card audio in");
        props.Add(new(PwKeys.TargetObject, _o.Source));

        // Linked to the card only, and never moved elsewhere when it goes away (WirePlumber would fall back to the
        // default source, a microphone); the supervisor captures again when it is back.
        props.Add(new(PwKeys.NodeDontReconnect, "true"));
        if (_o.SourceIsSinkMonitor)
        {
            props.Add(new(PwKeys.StreamCaptureSink, "true"));
        }

        return new CaptureStream(_connection, _o.NodePrefix + "-capture", props, _line);
    }

    private PlaybackStream NewPlayback()
    {
        var props = CommonProperties("Playback", _o.NodePrefix + "-playback", "Frame Motion Plus: capture card audio, delayed to the picture");

        // Pinned to the sink chosen at the start (verified on WirePlumber 0.5.19): dont-reconnect stops it being turned
        // into a follower of the default (checkFollowDefault sets target.node -1 otherwise) and moved when its sink goes
        // (it ends in error instead, and the supervisor connects it again when the sink is back); dont-move makes it ignore
        // a per-stream target in the metadata (the desktop's "move to" does that); dont-fallback keeps it unlinked rather
        // than on the default if the sink is missing as it connects. The supervisor's guard stands behind all three.
        props.Add(new(PwKeys.TargetObject, Volatile.Read(ref _sink)));
        props.Add(new(PwKeys.NodeDontReconnect, "true"));
        props.Add(new("node.dont-move", "true"));
        props.Add(new("node.dont-fallback", "true"));
        return new PlaybackStream(_connection, _o.NodePrefix + "-playback", props, _line, _o.Quantum);
    }

    /// <summary>An event for stderr and the report; <paramref name="minor"/> ones (recorders coming and going, which a
    /// share toggled often repeats) keep the last 16 of the report's 64 places for the others (a wrong link, a lost sink).</summary>
    private void Event(string text, bool minor = false)
    {
        lock (_eventsGate)
        {
            if (_events.Count < (minor ? 48 : 64))
            {
                _events.Add(string.Create(CultureInfo.InvariantCulture, $"{Clock.NowNs / 1e9:0.000} s: {text}"));
            }
        }

        _o.OnEvent?.Invoke(text);
    }

    /// <summary>The supervisor: watches the graph every 50 ms (allocation-free unless something changed), then tears down.</summary>
    private void Supervise()
    {
        try
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                Thread.Sleep(50);
                if (_connection.Lost)
                {
                    if (!_lostReported)
                    {
                        _lostReported = true;
                        _line.SetMuted(true);
                        Event($"the PipeWire connection was lost ({_connection.LostReason}): audio stopped");
                    }

                    continue;
                }

                Look();
                ReportHandlerErrors();
                Log();
            }

            // The playback side fades out over its next block (muted by Stop), then everything goes.
            Thread.Sleep(30);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _fault ??= $"supervisor: {e.GetType().Name}: {e.Message}";
            _line.SetMuted(true);
        }
        finally
        {
            Teardown();
        }
    }

    private void Look()
    {
        var now = Clock.NowNs;
        var captureState = _capture?.State ?? PwDefs.StreamStateUnconnected;
        var playbackState = _playback?.State ?? PwDefs.StreamStateUnconnected;
        var graph = _connection.GraphVersion;
        var changed = graph != _seenGraph || captureState != _seenCaptureState || playbackState != _seenPlaybackState;
        if (changed)
        {
            _seenGraph = graph;
            _seenCaptureState = captureState;
            _seenPlaybackState = playbackState;
            Examine(now, captureState, playbackState);
        }

        // The card's source is back (or the capture stream failed): capture again, at most every 2 s.
        var captureBad = _capture is null || captureState is PwDefs.StreamStateError or PwDefs.StreamStateUnconnected
            || (!_captureLinked && _captureUnlinkedSinceNs != 0 && now - _captureUnlinkedSinceNs > 1_500 * Nanoseconds.PerMillisecond);
        if (captureBad && _sourcePresent && now >= _nextCaptureRetryNs && _started)
        {
            _nextCaptureRetryNs = now + RetryNs;
            RestartCapture();
        }

        // The same for the chosen sink: its stream ends in error when the sink goes (it is never moved), so it is
        // connected again, to that sink only, once the sink is back.
        var playbackBad = _playback is null || playbackState is PwDefs.StreamStateError or PwDefs.StreamStateUnconnected
            || (!_playbackLinked && _playbackUnlinkedSinceNs != 0 && now - _playbackUnlinkedSinceNs > 1_500 * Nanoseconds.PerMillisecond);
        if (playbackBad && _sinkPresent && now >= _nextPlaybackRetryNs && _started)
        {
            _nextPlaybackRetryNs = now + RetryNs;
            RestartPlayback();
        }
    }

    /// <summary>The first registry handler failure on the guard's connection, said once (outside the loop lock).</summary>
    private void ReportHandlerErrors()
    {
        if (_handlerErrorsReported || _connection.HandlerErrors == 0)
        {
            return;
        }

        _handlerErrorsReported = true;
        Event($"the PipeWire registry handler failed on an object ({_connection.FirstHandlerError}); the guard's model may miss a node: a node it does not know is looked up again before anything is cut (the count is in the summary)");
    }

    /// <summary>Diagnostics (<see cref="AudioDelayOptions.LogIntervalS"/>): the delay line as it stands, every few seconds.</summary>
    private void Log()
    {
        if (_o.LogIntervalS <= 0 || _o.OnLog is not { } log)
        {
            return;
        }

        var now = Clock.NowNs;
        if (now < _nextLogNs)
        {
            return;
        }

        _nextLogNs = now + (long)(_o.LogIntervalS * Nanoseconds.PerSecond);
        var s = _line.Statistics;
        log(string.Create(CultureInfo.InvariantCulture,
            $"t {(now - _startedNs) / 1e9:0.0} s: {_line.State}; delay {_line.CurrentDelayNs / 1e6:0.000} ms, target {s.LastTargetNs / 1e6:0.000} (video {_line.VideoLatencyNs / 1e6:0.000} + trim {_o.TrimNs / 1e6:0.###}), " +
            $"least {s.LastMinimumNs / 1e6:0.000}; ratio {(_line.Ratio - 1) * 1e6:+0.0;-0.0} ppm, learned drift {s.LastIntegral * 1e6:+0.0;-0.0} ppm; " +
            $"underruns {s.Underruns}, jumps {s.Jumps}, resyncs {s.Resyncs}, gaps {s.CaptureGaps}"));
    }

    /// <summary>
    /// Something changed in the graph or a stream's state: who is linked to whom. The sound plays only while the capture
    /// stream takes exactly the intended source and the playback stream feeds the chosen sink and no other output device
    /// (<see cref="ClassifyPlaybackPeers"/>: recorders are left alone); a wrong link on
    /// either side mutes at once and the stream is cut and made again (backing off from 250 ms to 2 s). Events are emitted
    /// after the loop lock is released.
    /// </summary>
    private void Examine(long now, int captureState, int playbackState)
    {
        var events = (List<(string Text, bool Minor)>?)null;
        void Say(string text, bool minor = false) => (events ??= []).Add((text, minor));

        PwNode? source, sink, configured = null;
        List<uint> into = [];
        var peers = new List<(uint Id, bool Known, string Name, string? MediaClass)>();
        string? configuredName = null;
        using (_connection.Lock())
        {
            source = _connection.FindNodeLocked(_o.Source);
            sink = _connection.FindNodeLocked(_sink);
            if (_o.Sink is null && _connection.DefaultConfiguredSink is { } c && c != _sink)
            {
                configuredName = c;
                configured = _connection.FindNodeLocked(c);
            }

            var captureNode = _capture?.NodeIdLocked() ?? PwDefs.IdAny;
            if (captureNode != PwDefs.IdAny)
            {
                into = _connection.PeersIntoLocked(captureNode);
            }

            PlaybackPeersLocked(peers);
        }

        // A peer the registry has not announced and no output device besides: before it is judged (and the stream cut,
        // which would also break a screen share's link), the registry is read again from scratch, once per such peer, with
        // the sound muted meanwhile (at most ResyncTimeoutMs plus the fade). An output device is cut at once, as before.
        var route = ClassifyPlaybackPeers(peers, sink?.Id ?? PwDefs.IdAny);
        var registryIncomplete = false;
        if (route.OutputDevice is null && route.Unannounced.Count > 0 && !_resyncedPeers.IsSupersetOf(route.Unannounced))
        {
            _line.SetMuted(true);
            var unknown = route.Unannounced;
            var resyncStartNs = Clock.NowNs;
            bool caughtUp;
            using (_connection.Lock())
            {
                caughtUp = _connection.ResyncRegistryLocked(ResyncTimeoutMs);
                source = _connection.FindNodeLocked(_o.Source);
                sink = _connection.FindNodeLocked(_sink);
                into = _connection.PeersIntoLocked(_capture?.NodeIdLocked() ?? PwDefs.IdAny);
                peers.Clear();
                PlaybackPeersLocked(peers);
            }

            // The server did not answer in time: the model may lack the very link that would show an output device, so
            // nothing is concluded from it. The stream is cut and made again, as an unannounced peer always was before.
            registryIncomplete = !caughtUp;
            var took = string.Create(CultureInfo.InvariantCulture, $"{(Clock.NowNs - resyncStartNs) / 1e6:0.0} ms");
            _registryResyncs++;
            _resyncedPeers.UnionWith(unknown);
            route = ClassifyPlaybackPeers(peers, sink?.Id ?? PwDefs.IdAny);
            var nowKnown = peers.Where(p => p.Known && unknown.Contains(p.Id)).ToList();
            if (route.Foreign is null)
            {
                _unannouncedRecorders += nowKnown.Count > 0 ? 1 : 0; // known now (no output device: a recorder), not merely gone
                Say($"the playback stream was linked to a node the registry had not announced ({string.Join(", ", unknown.Select(u => $"node {u}"))}): muted while the registry was read again ({took}{(caughtUp ? "" : ", it did not catch up in time")}); it is {(nowKnown.Count == 0 ? "gone" : string.Join(", ", nowKnown.Select(p => $"{p.Name}, {p.MediaClass ?? "no media class"}")))}: nothing cut");
            }
            else
            {
                Say($"the playback stream was linked to a node the registry had not announced ({string.Join(", ", unknown.Select(u => $"node {u}"))}); after reading the registry again ({took}{(caughtUp ? "" : ", it did not catch up in time")}): {route.Foreign}");
            }
        }

        _resyncedPeers.IntersectWith(route.Unannounced);

        var restartPlayback = false;

        // No sink named and the user configured another default: the sound is re-pinned there (guarded the same way).
        if (configured is { IsSink: true } && configuredName is not null)
        {
            Volatile.Write(ref _sink, configuredName);
            sink = configured;
            _sinkChanges++;
            _sinkPresent = true;
            restartPlayback = true;
            Say($"your selected default output is now {configured.Description ?? configured.Name} ({configured.Name}): the sound moves there");
        }

        // Present or gone.
        if (source is null && _sourcePresent)
        {
            _sourcePresent = false;
            _sourceLosses++;
            Say($"the audio source {_o.Source} is gone (the card unplugged or reset?): silent until it is back");
        }
        else if (source is not null && !_sourcePresent)
        {
            _sourcePresent = true;
            _nextCaptureRetryNs = 0;
            Say($"the audio source {_o.Source} is back");
        }

        if (sink is null && _sinkPresent)
        {
            _sinkPresent = false;
            _sinkLosses++;
            Say($"the sink {_sink} is gone: the sound is muted until it is back (it is never moved to another sink)");
        }
        else if (sink is not null && !_sinkPresent)
        {
            _sinkPresent = true;
            _nextPlaybackRetryNs = 0;
            Say($"the sink {_sink} is back");
        }

        // The capture: linked to exactly the intended source, or muted (and a wrong link cut).
        var captureLinked = source is not null && into.Count > 0;
        var captureWrong = false;
        foreach (var peer in into)
        {
            if (source is null || peer != source.Id)
            {
                captureLinked = false;
                captureWrong = true;
            }
        }

        // The playback: it feeds the chosen sink and no other output device, or it is muted (and the wrong link cut).
        var playbackLinked = route.ToChosen && route.Foreign is null && !registryIncomplete;
        var playbackWrong = route.Foreign is not null || registryIncomplete;
        if (!playbackWrong && route.Recorders.Count != _recorders)
        {
            Say(route.Recorders.Count == 0
                ? "no recorder takes a copy of the sound any more"
                : $"{route.Recorders.Count} recorder(s) take a copy of the sound ({string.Join(", ", route.Recorders)}): left alone, the sound still plays to {_sink} only",
                minor: true);
            _recorders = route.Recorders.Count;
        }

        if (captureWrong || playbackWrong)
        {
            _line.SetMuted(true);
        }

        if (captureWrong && now >= _nextCaptureWrongNs)
        {
            Say("the capture stream was linked to another node than the card's source: muted, cut, captured again");
            _nextCaptureWrongNs = now + _captureWrongBackoffNs;
            _captureWrongBackoffNs = Math.Min(RetryNs, _captureWrongBackoffNs * 2);
            RestartCapture();
            captureLinked = false;
        }

        if (playbackWrong && now >= _nextPlaybackWrongNs)
        {
            _wrongSinkLinks++;
            Say($"the playback stream was linked to another node than {_sink} ({route.Foreign ?? "a node the registry did not confirm in time"}): muted, cut, connected to {_sink} again");
            _nextPlaybackWrongNs = now + _playbackWrongBackoffNs;
            _playbackWrongBackoffNs = Math.Min(RetryNs, _playbackWrongBackoffNs * 2);
            restartPlayback = true;
        }

        if (restartPlayback)
        {
            RestartPlayback();
            playbackLinked = false;
        }

        if (captureLinked != _captureLinked)
        {
            _captureLinked = captureLinked;
            _captureUnlinkedSinceNs = captureLinked ? 0 : now;
        }
        else if (!captureLinked && _captureUnlinkedSinceNs == 0)
        {
            _captureUnlinkedSinceNs = now;
        }

        if (playbackLinked != _playbackLinked)
        {
            Volatile.Write(ref _playbackLinked, playbackLinked);
            _playbackUnlinkedSinceNs = playbackLinked ? 0 : now;
        }
        else if (!playbackLinked && _playbackUnlinkedSinceNs == 0)
        {
            _playbackUnlinkedSinceNs = now;
        }

        var playbackOk = playbackLinked && !restartPlayback && playbackState == PwDefs.StreamStateStreaming;
        var captureOk = captureLinked && !captureWrong && captureState == PwDefs.StreamStateStreaming;
        if (playbackOk != _playbackOk)
        {
            // Said once each way, after the sound has once been playing to the sink.
            if (!playbackOk && _playbackWasOk)
            {
                Say($"the sound is muted: the playback stream is not on {_sink}");
            }
            else if (playbackOk && _playbackWasOk)
            {
                Say($"the sound resumes on {_sink}");
            }

            _playbackOk = playbackOk;
            _playbackWasOk |= playbackOk;
            if (playbackOk)
            {
                _playbackWrongBackoffNs = WrongLinkBackoffNs;
            }
        }

        if (captureOk)
        {
            _captureWrongBackoffNs = WrongLinkBackoffNs;
        }

        _line.SetMuted(!(captureOk && playbackOk) || Volatile.Read(ref _stop) != 0);
        if (events is not null)
        {
            foreach (var (text, minor) in events)
            {
                Event(text, minor);
            }
        }
    }

    /// <summary>The playback stream's peers as the model knows them (loop locked).</summary>
    private void PlaybackPeersLocked(List<(uint Id, bool Known, string Name, string? MediaClass)> peers)
    {
        var playbackNode = _playback?.NodeIdLocked() ?? PwDefs.IdAny;
        if (playbackNode == PwDefs.IdAny)
        {
            return;
        }

        foreach (var peer in _connection.PeersOutOfLocked(playbackNode))
        {
            var known = _connection.TryNodeKindLocked(peer, out var name, out var mediaClass);
            peers.Add((peer, known, name, mediaClass));
        }
    }

    /// <summary>
    /// What the playback stream feeds, peer by peer (<see cref="ClassifyPlaybackPeers"/>): <see cref="Foreign"/> names the
    /// first wrong peer, <see cref="OutputDevice"/> the first output device other than the chosen sink, and
    /// <see cref="Unannounced"/> lists the peers the registry has not announced.
    /// </summary>
    internal readonly record struct PlaybackRoute(bool ToChosen, string? Foreign, IReadOnlyList<string> Recorders, string? OutputDevice, IReadOnlyList<uint> Unannounced);

    /// <summary>
    /// The guard's rule for the playback stream's peers (the nodes its outputs are linked to). The chosen sink: linked. An
    /// output device (a sink of any kind, a null sink, a duplex device) other than it: wrong, the sound must not reach it.
    /// A node the registry has not announced: wrong too (where the sound goes is unknown; the registry announces a node
    /// before any link to it, so this is no race), but <see cref="Examine"/> reads the registry again before acting on it
    /// (a lost or mishandled announcement must not cut a screen share's link). Any other node is a recorder taking a copy of the sound (a capture
    /// stream such as a screen share's or OBS's, a PulseAudio monitor of the stream): left alone. Not caught: a loopback or
    /// filter whose capture half is a plain stream and whose playback half plays to another sink (its two halves are not
    /// linked in the graph and the registry does not say they belong together); filters that show themselves as a sink
    /// (an equaliser's virtual sink) are output devices and are caught. The route's Foreign names the first wrong peer.
    /// </summary>
    internal static PlaybackRoute ClassifyPlaybackPeers(IReadOnlyList<(uint Id, bool Known, string Name, string? MediaClass)> peers, uint chosenSink)
    {
        var toChosen = false;
        string? foreign = null, device = null;
        var recorders = new List<string>();
        var unannounced = new List<uint>();
        foreach (var (id, known, name, mediaClass) in peers)
        {
            if (chosenSink != PwDefs.IdAny && id == chosenSink)
            {
                toChosen = true;
            }
            else if (!known)
            {
                foreign ??= $"node {id}, not announced";
                unannounced.Add(id);
            }
            else if (PwNode.IsOutputDevice(mediaClass))
            {
                device ??= $"{name}, {mediaClass}";
                foreign ??= device;
            }
            else
            {
                recorders.Add($"{name}, {mediaClass ?? "no media class"}");
            }
        }

        return new PlaybackRoute(toChosen, foreign, recorders, device, unannounced);
    }

    private void RestartCapture()
    {
        if (Volatile.Read(ref _stop) != 0)
        {
            return;
        }

        if (_capture is { } old)
        {
            _retiredAllocated += old.AllocatedBytes;
            old.Dispose();
        }

        _capture = NewCapture();
        _ = _capture.Connect(capture: true);
        _captureRestarts++;
        _captureLinked = false;
        _captureUnlinkedSinceNs = Clock.NowNs;
        _seenGraph = -1;
    }

    private void RestartPlayback()
    {
        if (Volatile.Read(ref _stop) != 0)
        {
            return;
        }

        _line.SetMuted(true);
        if (_playback is { } old)
        {
            _retiredAllocated += old.AllocatedBytes;
            _retiredLate += old.LateCycles;
            old.Dispose();
        }

        _playback = NewPlayback();
        _ = _playback.Connect(capture: false);
        _playbackRestarts++;
        Volatile.Write(ref _playbackLinked, false);
        _playbackUnlinkedSinceNs = Clock.NowNs;
        _seenGraph = -1;
    }

    private void Teardown()
    {
        try
        {
            _capture?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _fault ??= $"teardown: {e.Message}";
        }

        try
        {
            _playback?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _fault ??= $"teardown: {e.Message}";
        }

        _connection.Dispose();
    }
}
