using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.App.Pattern;

/// <summary>What one <c>fmp pattern</c> run did and measured.</summary>
internal sealed class PatternResult
{
    public required PatternOptions Options { get; init; }

    public string DeviceName { get; set; } = string.Empty;

    public string DeviceUuid { get; set; } = string.Empty;

    public int CudaDriverVersion { get; set; }

    public string VulkanDriver { get; set; } = string.Empty;

    public bool ValidationEnabled { get; set; }

    /// <summary>The refresh rate paced at, at the end of the run.</summary>
    public FrameRate Rate { get; set; }

    /// <summary>False when a window never learnt its display's rate and the run was paced at the presenter's guess.</summary>
    public bool RateKnown { get; set; } = true;

    /// <summary>Times the paced rate changed during the run (a window's display rate becoming known or changing).</summary>
    public int RateChanges { get; set; }

    public int PitchBytes { get; set; }

    public long SlotBytes { get; set; }

    public string TargetDescription { get; set; } = string.Empty;

    public string Transform { get; set; } = string.Empty;

    public string PresentThread { get; set; } = string.Empty;

    public long Frames { get; set; }

    public int WarmupFrames { get; set; }

    public bool StoppedByUser { get; set; }

    public long Presented { get; set; }

    public long Dropped { get; set; }

    /// <summary>Refresh slots with no free image, so nothing was produced (windowed FIFO back-pressure).</summary>
    public long Skipped { get; set; }

    /// <summary>Slots where the bridge refused to hand out a write (too far ahead of the presenter's releases), so nothing was produced.</summary>
    public long HeldBack { get; set; }

    /// <summary>The presenter's cap on presents in flight; 0 = it cannot cap them (no present feedback).</summary>
    public int MaxFramesInFlight { get; set; }

    /// <summary>How the presenter learns that a present was shown.</summary>
    public string FeedbackMode { get; set; } = string.Empty;

    /// <summary>Why automatic feedback selection found none, when it did not.</summary>
    public string? FeedbackFallbackReason { get; set; }

    /// <summary>The bridge's hard limit on writes handed out whose GPU work has not finished.</summary>
    public long MaxOutstanding { get; set; }

    /// <summary>The most writes outstanding at any point (sampled as each write is acquired, so at least 1).</summary>
    public long MaxOutstandingSeen { get; set; }

    /// <summary>The most presents in flight right after a present, over the measured slots (after the warm-up).</summary>
    public long MaxPresentsInFlightSeen { get; set; }

    /// <summary>Times the bridge saw the ring's written value go down: writes signalled out of order (a producer bug).</summary>
    public long OutOfOrderSignals { get; set; }

    /// <summary>Ring imports the bridge could not drain at teardown and handed to its reaper.</summary>
    public long AbandonedImports { get; set; }

    /// <summary>
    /// Of those, the ones the reaper had not freed within 3 s of the presenter's disposal (leaked GPU memory and
    /// descriptors). The run's bridge and presenter are both gone by then and no other bridge is live, so nothing holds
    /// a finished import any more: one still there is a failure.
    /// </summary>
    public int UnreapedImports { get; set; }

    /// <summary>200 µs polls for a free frame slot (presents complete at vblanks, which the slot grid is not locked to).</summary>
    public long ImageWaits { get; set; }

    /// <summary>Slots the present thread woke for more than one refresh period late.</summary>
    public long Late { get; set; }

    public long Verified { get; set; }

    public long Mismatches { get; set; }

    public string? FirstMismatch { get; set; }

    public long LoopNs { get; set; }

    public long MeasuredFrames { get; set; }

    /// <summary>Bytes the present thread allocated after the warm-up; -1 when nothing was measured.</summary>
    public long AllocatedBytes { get; set; } = -1;

    public GcSnapshot Gc { get; set; }

    public PatternSeries? Series { get; set; }

    public PresenterStats PresenterStats { get; set; }

    public long TimerDrops { get; set; }

    public int ValidationProblems { get; set; }

    public string? ValidationReport { get; set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<Exception> TeardownErrors { get; set; } = [];

    /// <summary>With --vfg: where the SDK came from.</summary>
    public string? VfxSdk { get; set; }

    /// <summary>The generator's <c>Capabilities.EnqueueMayBlock</c> (VFG with automatic shot-change detection: true).</summary>
    public bool GeneratorMayBlock { get; set; }

    /// <summary>With --vfg: the instance's load and warm-up before the loop.</summary>
    public VfgLoadReport VfgLoad { get; set; }

    /// <summary>With --vfg offscreen: generated frames scored against the true middle frame (wrapping pairs excluded).</summary>
    public long GeneratedScored { get; set; }

    /// <summary>Of those, closer to the truth than the blend, not a copy of a neighbour, static bars intact.</summary>
    public long GeneratedGenuine { get; set; }

    /// <summary>Generated frames between neighbours the box wrapped between (no middle to compare with).</summary>
    public long GeneratedWrap { get; set; }

    /// <summary>Mean absolute error (code values) over the box band: VFG against the truth, and the blend against it.</summary>
    public double MeanToTruth { get; set; }

    public double MeanBlendToTruth { get; set; }

    public InterpolationScore WorstScore { get; set; }

    public string? FirstBadGenerated { get; set; }

    /// <summary>Things that did not fail the run but limit what it shows, one line each.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        if (MaxFramesInFlight == 0)
        {
            warnings.Add("the presenter cannot cap presents in flight (no present feedback on this surface), so the design's " +
                         $"2-in-flight bound on latency does not hold here{(FeedbackFallbackReason is { } why ? ": " + why : "")}");
        }

        return warnings;
    }

    /// <summary>Why the run failed, one line each; empty when it passed.</summary>
    public IReadOnlyList<string> Failures()
    {
        var failures = new List<string>();
        if (Failure is { } e)
        {
            failures.Add($"the present loop failed: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
        }

        foreach (var t in TeardownErrors)
        {
            failures.Add($"teardown failed: {t.GetType().Name}: {t.Message.ReplaceLineEndings(" ")}");
        }

        if (ValidationProblems > 0)
        {
            failures.Add($"{ValidationProblems} validation message(s)");
        }

        if (Options.Offscreen && !Options.Unpaced && (Dropped > 0 || Skipped > 0 || HeldBack > 0))
        {
            failures.Add($"{Dropped} dropped, {Skipped} skipped and {HeldBack} held-back frame(s) offscreen, where every frame must be shown");
        }

        if (PresenterStats.RejectedRequests > 0)
        {
            // The presenter refuses a request that is stale (an older ring) or past its bounds (more than 500 releases
            // waiting, or a write more than 500 slots past the last release); a refused write is neither shown,
            // retained nor released. The loop only asks for slots the bridge handed out for the current ring, and the
            // bridge keeps at most 250 writes unfinished, half the presenter's waiting bound, so none of that can
            // happen here: a rejection is a bug in the bridge or the loop.
            failures.Add($"{PresenterStats.RejectedRequests} present request(s) rejected by the presenter (stale, or past its bounds)");
        }

        if (OutOfOrderSignals > 0)
        {
            failures.Add($"the ring's written value went down {OutOfOrderSignals} time(s): writes were signalled out of order");
        }

        if (UnreapedImports > 0)
        {
            failures.Add($"{UnreapedImports} ring import(s) not freed within 3 s of the presenter letting go (their queued work never finished)");
        }

        if (GeneratedScored > GeneratedGenuine)
        {
            failures.Add($"{GeneratedScored - GeneratedGenuine} of {GeneratedScored} generated frame(s) were not genuine interpolations; first: {FirstBadGenerated}");
        }

        if (Options.Vfg && Options.Offscreen && Options.Verify && Failure is null && Presented > 20 && GeneratedScored == 0)
        {
            failures.Add("no generated frame was scored");
        }

        if (Mismatches > 0)
        {
            failures.Add($"{Mismatches} frame(s) failed verification; first: {FirstMismatch}");
        }

        if (Options.Offscreen && Options.Verify && Failure is null && Verified != Presented)
        {
            failures.Add($"{Verified} of {Presented} presented frames verified");
        }

        if (AllocatedBytes > 0)
        {
            failures.Add($"{AllocatedBytes} bytes allocated on the present thread in {MeasuredFrames} measured slots");
        }
        else if (AllocatedBytes < 0 && Failure is null && !StoppedByUser)
        {
            failures.Add("no frames after the warm-up, so allocations were not measured (run more frames)");
        }

        if (Presented == 0 && Failure is null && !StoppedByUser)
        {
            failures.Add("nothing was presented");
        }

        return failures;
    }
}
