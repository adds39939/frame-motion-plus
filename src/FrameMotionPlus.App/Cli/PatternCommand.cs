using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Bench;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp pattern</c>: the Phase 2 demo and gate. CUDA test pattern → presentation ring → Vulkan presenter at fixed
/// refresh on a dedicated thread, on a window or headless (every frame verified). Exit 0 when the run is clean; 1 on a
/// failure, a dropped frame offscreen, a validation message or a managed allocation in the loop; 69 when the GPU,
/// driver, display or layer it needs is not there. Ctrl+C (SIGINT) or SIGTERM stops the loop after the current frame;
/// the run then tears down and reports its verdict as usual (exit code by the verdict). A second signal is not caught,
/// so it ends the process at once should the teardown itself hang.
/// </summary>
internal static class PatternCommand
{
    /// <summary>EX_UNAVAILABLE in sysexits.h: a required service (here a GPU, display or layer) is not available.</summary>
    public const int ExitUnavailable = 69;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = PatternOptions.Parse(args);
        var ic = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(ic,
            $"fmp pattern: {options.Width}x{options.Height} {options.FormatName}, {(options.Offscreen ? "offscreen" : "windowed")}, " +
            $"{(options.Frames is { } f ? $"{f} frames" : $"{options.DurationSeconds:0.##} s")}{(options.Unpaced ? ", unpaced" : "")}, " +
            $"{(options.Vfg ? $"VFG {Vfg.VfgReport.ModeName(options.Quality)} (2x: real, generated, ...)" : $"blend {(options.Blend ? "on (2x: real, generated, ...)" : "off")}")}, " +
            $"validation {(options.Validate ? "on" : "off")}"));
        output.Flush();

        // Validation messages are echoed from whichever thread made the Vulkan call, signal notices from a signal handler.
        error = TextWriter.Synchronized(error);
        PatternResult result;
        try
        {
            using var run = new PatternRun(options, options.Validate ? error : null);
            var signals = 0;
            void OnSignal(PosixSignalContext context)
            {
                if (Interlocked.Increment(ref signals) == 1)
                {
                    context.Cancel = true; // the first one: stop cleanly
                    run.StopByUser();
                    error.WriteLine($"fmp: pattern stopping on {context.Signal} (again to end at once)");
                }
            }

            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            result = run.Run();
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: pattern {e.Message}");
            return ExitUnavailable;
        }

        Print(output, result);
        return result.Failures().Count == 0 ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
    }

    private static void Print(TextWriter o, PatternResult r)
    {
        var ic = CultureInfo.InvariantCulture;
        var cudaVersion = string.Create(ic, $"{r.CudaDriverVersion / 1000}.{r.CudaDriverVersion % 1000 / 10}");
        o.WriteLine($"  device      {r.DeviceName} (UUID {r.DeviceUuid}), CUDA driver API {cudaVersion}, Vulkan driver {r.VulkanDriver}");
        if (r.VfxSdk is { } sdk)
        {
            o.WriteLine(string.Create(ic,
                $"  vfg         {sdk}; loaded in {r.VfgLoad.LoadNs / 1e6:0.0} ms, warmed up in {r.VfgLoad.WarmupNs / 1e6:0.0} ms, " +
                $"{r.VfgLoad.DeviceBytes / 1048576.0:0} MiB; on the ring stream, output written straight into the slots"));
        }

        if (r.GeneratorMayBlock)
        {
            o.WriteLine("  generator   Enqueue may block until the frame is generated (Capabilities.EnqueueMayBlock); this demo calls it on the present " +
                        "thread, which a pipeline must not do: it belongs on a generator thread of its own");
        }
        o.WriteLine(string.Create(ic,
            $"  ring        {r.Options.Slots} slots imported into CUDA, pitch {r.PitchBytes} B, {r.SlotBytes} B per slot"));
        o.WriteLine($"  target      {r.TargetDescription}; transform {r.Transform}");
        o.WriteLine(string.Create(ic,
            $"  feedback    {r.FeedbackMode}; presents in flight capped at {(r.MaxFramesInFlight == 0 ? "nothing (cannot cap)" : r.MaxFramesInFlight.ToString(ic))}" +
            $"{(r.FeedbackFallbackReason is { } why ? " (fallback: " + why + ")" : "")}"));
        o.WriteLine(r.Options.Unpaced
            ? $"  pacing      none (--unpaced): as fast as the bridge's back-pressure allows, no wait for a free image; present thread {r.PresentThread}"
            : string.Create(ic,
            $"  pacing      {r.Rate} Hz on CLOCK_MONOTONIC{(r.RateKnown ? "" : " (the display's rate never became known: a guess)")}" +
            $"{(r.RateChanges > 0 ? string.Create(ic, $" (rate changed {r.RateChanges}x during the run, e.g. once the window was on an output)") : "")}, " +
            $"present thread {r.PresentThread}"));

        var seconds = Nanoseconds.ToSeconds(r.LoopNs);
        var stats = r.PresenterStats;
        o.WriteLine(string.Create(ic,
            $"  frames      {r.Frames} slots in {seconds:0.00} s: {r.Presented} presented ({(seconds > 0 ? r.Presented / seconds : 0):0.0}/s), {r.Dropped} dropped, " +
            $"{r.Skipped} skipped (no free image within half a period; {r.ImageWaits} 200 us waits for one), {r.Late} woken over a period late; " +
            $"max presents in flight {r.MaxPresentsInFlightSeen}; {stats.RejectedRequests} rejected by the presenter; " +
            $"bridge lead at most {r.MaxOutstandingSeen} of {r.MaxOutstanding} allowed, {r.HeldBack} slot(s) held back" +
            $"{(r.OutOfOrderSignals > 0 ? string.Create(ic, $"; written went down {r.OutOfOrderSignals}x") : "")}" +
            $"{(r.StoppedByUser ? "; stopped by the user" : "")}"));
        o.WriteLine(r.AbandonedImports == 0
            ? "  teardown    the bridge drained the ring and freed its import"
            : string.Create(ic, $"  teardown    {r.AbandonedImports} ring import(s) could not be drained in time and went to the reaper; {r.UnreapedImports} not freed within 3 s of the presenter letting go"));
        if (r.Options.Offscreen)
        {
            o.WriteLine(!r.Options.Verify
                ? "  verified    not checked (--no-verify)"
                : r.Options.Vfg
                    ? string.Create(ic,
                        $"  verified    {r.Verified} of {r.Presented} presented frames: real frames bit-exact against the CPU reference, generated frames scored " +
                        $"against the true middle frame: {r.GeneratedGenuine} of {r.GeneratedScored} genuine ({r.GeneratedWrap} across the box's wrap not scored); " +
                        $"box band error {r.MeanToTruth:0.000} codes on average (worst {r.WorstScore.ToTruth:0.000}) vs the blend's {r.MeanBlendToTruth:0.000}")
                    : string.Create(ic, $"  verified    {r.Verified} of {r.Presented} presented frames bit-exact against the CPU reference" +
                                        $"{(r.Options.Blend ? " (generated frames as the t = 0.5 blend of their neighbours)" : "")}"));
        }

        if (r.Series is { } s)
        {
            o.WriteLine(string.Create(ic,
                $"  gpu (us)    after {r.WarmupFrames} warm-up frames      count      p50      p99      max     mean"));
            Row(o, r.Options.Generates ? "source pattern draw" : "pattern into the slot", s.Pattern);
            if (r.Options.Generates)
            {
                Row(o, "real frame copy to slot", s.Copy);
                Row(o, r.Options.Vfg ? "VFG t=0.5 into slot (d)" : "blend t=0.5 into slot", s.Blend);
            }

            Row(o, "present: slot copy", s.PresentCopy);
            Row(o, "present: scale/output pass", s.PresentPass);
            o.WriteLine("  cpu (us)");
            Row(o, "produce+signal+present", s.Cpu);
        }

        if (r.TimerDrops > 0)
        {
            o.WriteLine(string.Create(ic, $"              {r.TimerDrops} GPU spans were overwritten before they were read"));
        }

        var gc = r.Gc;
        o.WriteLine(r.AllocatedBytes >= 0
            ? string.Create(ic,
                $"  memory      {r.AllocatedBytes} B allocated on the present thread in {r.MeasuredFrames} measured slots; " +
                $"GC collections meanwhile gen0/1/2 {gc.Gen0Collections}/{gc.Gen1Collections}/{gc.Gen2Collections}")
            : r.Failure is not null
                ? "  memory      not measured (the loop failed before its end, where allocations are read)"
                : "  memory      not measured (no frames after the warm-up)");
        o.WriteLine(r.ValidationEnabled
            ? string.Create(ic, $"  validation  VK_LAYER_KHRONOS_validation with synchronization checks: {r.ValidationProblems} message(s)")
            : "  validation  off (--validate turns it on)");
        if (r.ValidationReport is { } report)
        {
            foreach (var line in report.Split('\n'))
            {
                o.WriteLine($"              {line.TrimEnd()}");
            }
        }

        foreach (var warning in r.Warnings())
        {
            o.WriteLine($"  WARNING     {warning}");
        }

        var failures = r.Failures();
        if (failures.Count == 0)
        {
            o.WriteLine(r.Warnings().Count == 0 ? "  verdict     PASS" : "  verdict     PASS (with the warning above)");
            return;
        }

        o.WriteLine("  verdict     FAIL");
        foreach (var failure in failures)
        {
            o.WriteLine($"              - {failure}");
        }
    }

    private static void Row(TextWriter o, string name, Distribution d)
    {
        if (d.Count == 0)
        {
            o.WriteLine($"    {name,-32} (no samples)");
            return;
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    {name,-32} {d.Count,7} {Us(d.P50),8} {Us(d.P99),8} {Us(d.Max),8} {d.Mean / 1000.0,8:0.0}"));
    }

    private static string Us(long ns) => (ns / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}
