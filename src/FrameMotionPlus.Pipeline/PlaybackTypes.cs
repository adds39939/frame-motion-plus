using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Sources.V4l2;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.Pipeline;

/// <summary>A GPU, driver, library or display the session needs is not there. The message is one line.</summary>
public sealed class PlaybackUnavailableException : Exception
{
    public PlaybackUnavailableException()
    {
    }

    public PlaybackUnavailableException(string message)
        : base(message)
    {
    }

    public PlaybackUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Test seams. Each hook runs on the thread named; null costs nothing.</summary>
public sealed class PlaybackHooks
{
    /// <summary>Builds the generator instead of the session (wrap one to inject faults or slowness). It must generate on
    /// <c>stream</c>, the ring writes' stream, so its writes stay in order with the real-frame copies.</summary>
    public Func<CudaContext, ImageKernels, CudaStream, GeneratorHandle>? GeneratorFactory { get; init; }

    /// <summary>Generator thread, before each write.</summary>
    public Action<WriteJob>? BeforeWrite { get; init; }

    /// <summary>Present thread, before each present is handled.</summary>
    public Action<PresentJob>? BeforePresent { get; init; }

    /// <summary>Present thread, virtual clock: after each present was accepted (read the target back here).</summary>
    public Action<PresentedFrame>? OnPresented { get; init; }

    /// <summary>Pacing thread, once per loop turn.</summary>
    public Action? PacingTurn { get; init; }

    /// <summary>Present thread: a ring write's present for which it returns true is dropped (counted as refused).</summary>
    public Func<PresentJob, bool>? DropPresent { get; init; }

    /// <summary>
    /// Wraps the generator the session built (fault injection: <c>fmp play</c>'s <c>FMP_PLAY_FAULT</c>, tests). It is given
    /// the ring writes' stream and the session's kernels; the wrapper must generate on that stream.
    /// </summary>
    public Func<GeneratorHandle, CudaStream, ImageKernels, GeneratorHandle>? WrapGenerator { get; init; }

    /// <summary>Copy every presented frame to host memory (offscreen), so <see cref="OnPresented"/> can read it back.</summary>
    public bool OffscreenReadback { get; init; }

    /// <summary>Control thread: a capture changed between SDR and HDR10 (followed, or by hand); called once per change seen.</summary>
    public Action<Sources.V4l2.ColorSwitch>? OnColorSwitch { get; init; }

    /// <summary>
    /// Tests: an offscreen session on the real clock presents as a window does, to this model of a compositor's FIFO (its
    /// latch, missed refreshes, discards), which reports the first-pixel times.
    /// </summary>
    internal SimulatedCompositor? SimulatedDisplay { get; init; }
}

/// <summary>What a session's source delivers (a file or a capture), in the terms every source shares.</summary>
public readonly record struct SourceDescription
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The frames' RGB format (RGBA8, or RGB10A2 for 10-bit sources).</summary>
    public PixelFormat Format { get; init; }

    /// <summary>What the frames' pixels are (identity matrix, full range, the content's primaries and transfer).</summary>
    public ColorMetadata Color { get; init; }

    /// <summary>The file's frame rate, or the capture signal's rate.</summary>
    public FrameRate NominalRate { get; init; }

    public bool IsLive { get; init; }

    /// <summary>One line: what it is.</summary>
    public string? Text { get; init; }
}

/// <summary>What a capture source negotiated and resolved (for the start-up lines).</summary>
public sealed record CaptureSetup
{
    public required V4l2DeviceInfo Device { get; init; }

    public required NegotiatedFormat Format { get; init; }

    public required CaptureColor Color { get; init; }

    public required CaptureBufferMode BufferMode { get; init; }

    public IReadOnlyList<string> RefusedBufferModes { get; init; } = [];

    public int Buffers { get; init; }

    public bool SourceChangeEvents { get; init; }

    public string? Input { get; init; }

    public bool Cadence { get; init; }

    public CadenceOptions? CadenceOptions { get; init; }
}

/// <summary>What <see cref="PlaybackSession.Open"/> resolved.</summary>
public sealed class PlaybackSetup
{
    /// <summary>The file's stream (file playback only).</summary>
    public FileStreamInfo? Stream { get; init; }

    /// <summary>What the source delivers, file or capture.</summary>
    public SourceDescription Source { get; init; }

    /// <summary>The capture's negotiation (capture only).</summary>
    public CaptureSetup? Capture { get; init; }

    public required string GeneratorName { get; init; }

    public GeneratorKind Generator { get; init; }

    public FrameGenQualityName Quality { get; init; }

    public VfgLoadReport? VfgLoad { get; init; }

    public string? VfxSdk { get; init; }

    public bool GeneratorMayBlock { get; init; }

    public required string Display { get; init; }

    public string? FeedbackMode { get; init; }

    public int RingSlots { get; init; }

    public int RingPitchBytes { get; init; }

    public int OperationsPerWrite { get; init; }

    public long MaxLead { get; init; }

    public int PresentsInFlightCap { get; init; }

    public string? Transform { get; init; }

    /// <summary>The output at the start: SDR or HDR10, the images' format and colour space, the HDR metadata sent.</summary>
    public string? Output { get; init; }

    /// <summary>Whether frames are shown pixel for pixel or resampled to the output (and how), at the start.</summary>
    public string? Scaling { get; init; }

    public required string DeviceName { get; init; }

    public required string VulkanDriver { get; init; }
}

/// <summary>A quality mode's display name (avoids boxing an enum into a string on any path that matters).</summary>
public readonly record struct FrameGenQualityName(string Name)
{
    public override string ToString() => Name;
}

/// <summary>Everything a playback measured, for the summary and the gate.</summary>
public sealed class PlaybackReport
{
    public required PlaybackOptions Options { get; init; }

    public required PlaybackSetup Setup { get; init; }

    public PlaybackStatistics Statistics { get; init; } = new();

    public StopReason StopReason { get; set; }

    public Exception? Failure { get; set; }

    public List<Exception> TeardownErrors { get; } = [];

    /// <summary>The display rate the session ran at (after the pre-roll) and whether it was the output's own.</summary>
    public FrameRate DisplayRate { get; set; }

    public bool DisplayRateKnown { get; set; }

    public long DisplayRateChanges { get; set; }

    /// <summary>The multiplier cap the scheduler ran with.</summary>
    public int Multiplier { get; set; }

    /// <summary>The present lead added to the scheduler's margin.</summary>
    public long PresentLeadNs { get; set; }

    public long PlaybackNs { get; set; }

    public long WarmupNs { get; set; }

    public bool Measured { get; set; }

    public SchedulerCounters Counters { get; set; }

    public SchedulerDiagnostics Diagnostics { get; set; }

    public long InitialGenerationTimeNs { get; set; }

    public long MarginNs { get; set; }

    public long FramesRead { get; set; }

    public long Discontinuities { get; set; }

    public long SkippedBackPressure { get; set; }

    public long SkippedPresenterBacklog { get; set; }

    public long MaxOutstanding { get; set; }

    public long DisplayUpdates { get; set; }

    public long Copies { get; set; }

    public long GeneratedWrites { get; set; }

    public long Fallbacks { get; set; }

    public long GeneratorFaults { get; set; }

    public bool GeneratorFaulted { get; set; }

    public string? FirstGeneratorFault { get; set; }

    public long Shown { get; set; }

    /// <summary>Shows timed by their first pixel (present timing) rather than by their report.</summary>
    public long TimedShows { get; set; }

    public long Fillers { get; set; }

    public long DroppedLate { get; set; }

    public long DroppedRefused { get; set; }

    public long PresentRefusals { get; set; }

    public long LateShows { get; set; }

    /// <summary>How a file's clock was stretched to lock to the display (1: not).</summary>
    public double Resample { get; set; } = 1.0;

    /// <summary>The clock mode the run used: fixed refresh, or variable refresh.</summary>
    public RefreshMode RefreshModeUsed { get; set; } = RefreshMode.Fixed;

    /// <summary>The variable-refresh probe before playback (fullscreen, or --clock vrr); null when not run.</summary>
    public VrrProbeResult? VrrProbe { get; set; }

    /// <summary>Auto chose fixed refresh although the display follows the presents: the output rate the source would have
    /// needed at the multiplier (fps; 0 when that was not why).</summary>
    public double VrrTooFast { get; set; }

    /// <summary>The compositor's latch measured before playback (windowed, adaptive lead), and the lead it gave; null when not run.</summary>
    public LatchSweepResult? LatchSweep { get; set; }

    /// <summary>Every change of the present lead in the run: when (ns, CLOCK_MONOTONIC), to what, why.</summary>
    public List<(long AtNs, long LeadNs, LeadChange Why)> LeadChanges { get; } = [];

    /// <summary>Late shows after which the compositor gave the refresh back itself (it discarded a queued present).</summary>
    public long CompositorCatchUps { get; set; }

    /// <summary>Late shows after which the present thread gave refreshes back (the compositor did not).</summary>
    public long OwnCatchUps { get; set; }

    public long MaxPresentsInFlight { get; set; }

    public PresenterStats PresenterStats { get; set; }

    public long OutOfOrderSignals { get; set; }

    public int AbandonedImports { get; set; }

    public int UnreapedImports { get; set; }

    public FileSourceStatistics SourceStatistics { get; set; }

    /// <summary>The capture's counters and distributions (capture only).</summary>
    public CaptureSummary? Capture { get; set; }

    public bool SourceLeaked { get; set; }

    /// <summary>GPU work did not finish at teardown: nothing GPU-side was freed (see <see cref="Abandoned"/>); the caller
    /// should end the process once it has reported.</summary>
    public bool GpuAbandoned { get; set; }

    /// <summary>What teardown left allocated, and why (null when it freed everything).</summary>
    public string? Abandoned { get; set; }

    /// <summary>The presenter's last rebuild of the output for a colour switch (default: none).</summary>
    public OutputColorChange LastOutputColorChange { get; set; }

    /// <summary>The output at the end (after a fullscreen change or colour switches); see <see cref="PlaybackSetup.Output"/>.</summary>
    public string? OutputAtEnd { get; set; }

    /// <summary>Whether frames were shown pixel for pixel or resampled at the end; see <see cref="PlaybackSetup.Scaling"/>.</summary>
    public string? ScalingAtEnd { get; set; }

    /// <summary>Times the present lead went up (a present's content missed the compositor's latch).</summary>
    public long LeadRaises { get; set; }

    /// <summary>The largest present lead used, and the one at the end, ns.</summary>
    public long PeakLeadNs { get; set; }

    public long FinalLeadNs { get; set; }

    /// <summary>What was added to the file's content times so its first frame is the first shown (long.MinValue: never set).</summary>
    public long ContentOffsetNs { get; set; } = long.MinValue;

    /// <summary>The frame id of the first slot that showed a frame (the first frame of the file is 0).</summary>
    public long FirstShownFrame { get; set; } = -1;

    /// <summary>The telemetry verifier's result (windowed runs with first-pixel times); null when it did not run.</summary>
    public VerificationResult? Verification { get; set; }

    /// <summary>Why the verifier did not run (null when it did).</summary>
    public string? VerificationSkipped { get; set; }

    /// <summary>Slots the verifier's buffer had no room for (its verdict then covers the rest only).</summary>
    public long VerifierRowsDropped { get; set; }

    public int ValidationProblems { get; set; }

    public string? ValidationReport { get; set; }

    public bool ValidationEnabled { get; set; }

    public GcSnapshot MeasuredGc { get; set; }

    public GcSnapshot RunGc { get; set; }

    public string GcMode { get; set; } = "";

    public ThreadCost Pacing { get; set; }

    public ThreadCost Writer { get; set; }

    public ThreadCost Presenter { get; set; }

    public ThreadCost Control { get; set; }

    public string? PacingThread { get; set; }

    public string? WriterThread { get; set; }

    public string? PresentThread { get; set; }

    public long TelemetryRecords { get; set; }

    public long TelemetryDropped { get; set; }

    public string? CsvPath { get; set; }

    /// <summary>Hard failures: the run did not do what it must (separate from the gate's miss accounting).</summary>
    public List<string> Failures()
    {
        var f = new List<string>();
        if (Failure is { } e)
        {
            f.Add($"the run failed: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
        }

        if ((!Measured || Statistics.Slots == 0) && StopReason != StopReason.User)
        {
            f.Add("nothing was measured (the run ended before the end of its warm-up)");
        }

        if (Verification is { Disagreements: > 0 } v)
        {
            f.Add($"the live accounting disagrees with the telemetry verifier on {v.Disagreements} slot(s) or refresh(es): {v.Describe(8)}");
        }

        foreach (var t in TeardownErrors)
        {
            f.Add($"teardown: {t.GetType().Name}: {t.Message.ReplaceLineEndings(" ")}");
        }

        if (ValidationProblems > 0)
        {
            f.Add($"{ValidationProblems} Vulkan validation message(s)");
        }

        if (OutOfOrderSignals > 0)
        {
            f.Add($"ring writes signalled out of order {OutOfOrderSignals}x");
        }

        if (PresenterStats.RejectedRequests > 0)
        {
            f.Add($"{PresenterStats.RejectedRequests} present request(s) rejected by the presenter");
        }

        if (UnreapedImports > 0)
        {
            f.Add($"{UnreapedImports} ring import(s) not freed");
        }

        if (SourceLeaked)
        {
            f.Add(Capture is null ? "the source could not be freed (its decode thread did not stop)" : "the capture source could not be freed");
        }

        if (Measured)
        {
            foreach (var (name, cost) in Threads)
            {
                if (cost.AllocatedBytes > 0)
                {
                    f.Add($"the {name} thread allocated {cost.AllocatedBytes} B in the measured window");
                }
            }

            if (SourceStatistics.DecodeThreadAllocatedBytes > 0)
            {
                f.Add($"the decode thread allocated {SourceStatistics.DecodeThreadAllocatedBytes} B");
            }

            if (Capture is { Counters.DequeueThreadAllocatedBytes: > 0 } c)
            {
                f.Add($"the capture thread allocated {c.Counters.DequeueThreadAllocatedBytes} B");
            }
        }

        if (SourceStatistics.PoolGrowth > 0)
        {
            f.Add($"FFmpeg's frame pool grew {SourceStatistics.PoolGrowth}x during playback (CUDA allocations)");
        }

        return f;
    }

    public IEnumerable<(string Name, ThreadCost Cost)> Threads =>
        [("pacing", Pacing), ("generator", Writer), ("present", Presenter), ("control", Control)];
}

/// <summary>A pipeline thread's cost over the measured window.</summary>
public readonly record struct ThreadCost(long AllocatedBytes, long CpuNs, long Turns);
