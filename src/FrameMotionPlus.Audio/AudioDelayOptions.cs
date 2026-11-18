using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Audio;

/// <summary>Configuration of a <see cref="PipeWireAudioDelay"/>.</summary>
public sealed record AudioDelayOptions
{
    /// <summary>The PipeWire node to capture (node.name), e.g. the capture card's audio source.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// The sink to play to: a node name, or a case-insensitive part of one sink's name or description; pinned to exactly that
    /// device. Null: the default output the user selected (default.configured.audio.sink, else WirePlumber's default), and
    /// the new one when they select another; never WirePlumber's own fallback.
    /// </summary>
    public string? Sink { get; init; }

    /// <summary>Capture a sink's monitor instead of a source (tests: a null sink as the source).</summary>
    public bool SourceIsSinkMonitor { get; init; }

    /// <summary>Added to the measured video latency (positive: the sound later), ns: <c>--audio-offset-ms</c>.</summary>
    public long TrimNs { get; init; }

    /// <summary>The video latency assumed until the pipeline has measured one, ns.</summary>
    public long InitialVideoLatencyNs { get; init; } = 40 * Nanoseconds.PerMillisecond;

    /// <summary>Silent until the first video latency arrives (the first real frame has been shown): no sound before a picture.</summary>
    public bool SilentUntilVideo { get; init; } = true;

    /// <summary>The graph quantum asked for (node.latency), frames at 48 kHz: 256 is 5.3 ms per cycle.</summary>
    public int Quantum { get; init; } = 256;

    /// <summary>A second cross-faded move to the settled video latency this long after the first arrived; the statistics start a second later.</summary>
    public long SettleNs { get; init; } = 5 * Nanoseconds.PerSecond;

    /// <summary>Node names of the two streams (tests run several engines side by side).</summary>
    public string NodePrefix { get; init; } = "fmp-audio";

    /// <summary>Told about events as they happen (the sink changed, the source went or came back), on the supervisor thread.</summary>
    public Action<string>? OnEvent { get; init; }

    /// <summary>Diagnostics: a line with the delay, target, ratio and learned drift every this many seconds (0: none).</summary>
    public double LogIntervalS { get; init; }

    /// <summary>Where the diagnostic lines go (the supervisor thread; allocates a little per line).</summary>
    public Action<string>? OnLog { get; init; }
}
