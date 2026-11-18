using System.Globalization;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Capture;

/// <summary>What <c>fmp capture</c> prints: devices (--list), what it negotiated at the start, the capture's part of the summary.</summary>
internal static class CapturePrinter
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void PrintDevice(TextWriter o, CaptureDeviceDescription d, bool isDefault)
    {
        o.WriteLine($"{d.Info.Path}: {d.Info.Card} ({d.Info.Driver}, {d.Info.BusInfo}){(isDefault ? "  [the default device]" : "")}");
        if (d.Current is { } current)
        {
            o.WriteLine($"  current     {current}");
        }

        if (d.Input is { } input)
        {
            o.WriteLine($"  input       {input}");
        }

        foreach (var (format, sizes) in d.Formats)
        {
            var kind = CaptureFormats.FromFourCc(format.FourCc);
            var note = format.Compressed
                ? "compressed: never used, a decode step adds latency"
                : kind == CapturePixelFormat.Unknown ? "not supported by this source" : kind == CapturePixelFormat.P010 ? "10-bit: HDR" : "8-bit";
            o.WriteLine($"  {format.Name,-4}        {format.Description} ({note})");
            foreach (var size in sizes)
            {
                var rates = size.Intervals.Count == 0 ? "no rates reported" : string.Join(", ", size.Intervals.Select(i => i.ToString()));
                o.WriteLine(string.Create(Ic, $"              {size.Width}x{size.Height}{(size.Stepwise ? " (range end)" : "")}: {rates} fps"));
            }
        }

        o.WriteLine("  presets     " + string.Join(", ", CapturePreset.All.Where(p => d.Modes.Any(m => m.Format == p.Format && m.Width == p.Width && m.Height == p.Height && Math.Abs(m.Fps - p.Fps) < 0.01)).Select(p => p.Name)) switch
        {
            "" => "none of fmp capture's presets",
            var list => list,
        });
    }

    public static void PrintSetup(TextWriter o, CaptureOptions options, PlaybackOptions playback, PlaybackSetup s, string why, CudaContext cuda)
    {
        var c = s.Capture ?? throw new InvalidOperationException("Not a capture session.");
        var f = c.Format;
        o.WriteLine($"fmp capture: {c.Device.Path} - {c.Device.Card} ({c.Device.Driver}, {c.Device.BusInfo})");
        o.WriteLine($"  asked       {(options.Preset is { } preset ? $"--mode {preset.Name}: {preset.Note}" : options.Request.ToString())}");
        o.WriteLine($"  negotiated  {f.Describe()}{(f.Changes.Count == 0 ? " (as asked)" : "; the driver changed: " + string.Join("; ", f.Changes))}");
        o.WriteLine($"  colour      the device reports {c.Color.Reported} → {c.Color.Origin}; frames {s.Source.Format} " +
            $"{s.Source.Color.Transfer}/{s.Source.Color.Primaries} (identity matrix, full range, code values passed through)");
        o.WriteLine(string.Create(Ic,
            $"  buffers     {CaptureFormatsText(c)}; source-change events {(c.SourceChangeEvents ? "subscribed" : "not offered by the driver")}" +
            $"{(c.Input is { } input ? $"; input {input}" : "")}"));
        if (c.RefusedBufferModes.Count > 0)
        {
            o.WriteLine($"              refused first: {string.Join("; ", c.RefusedBufferModes)}");
        }

        o.WriteLine(c.Cadence && c.CadenceOptions is { } cadence
            ? string.Create(Ic,
                $"  cadence     on: {CadenceGrid.CellSize}x{CadenceGrid.CellSize}-sample cells, {CadenceGrid.CellSize * CadenceGrid.TileCells}x{CadenceGrid.CellSize * CadenceGrid.TileCells}-pixel tiles; " +
                $"repeat at or below / new at or above: SDR {cadence.Sdr.Low:0.0000} / {cadence.Sdr.High:0.0000}, PQ {cadence.Pq.Low:0.0000} / {cadence.Pq.High:0.0000} of full scale " +
                $"({(s.Source.Color.IsHdr ? "PQ" : "SDR")} thresholds in use)")
            : "  cadence     off (--no-cadence): every captured frame counts as new");
        o.WriteLine($"  generator   {s.GeneratorName}: {why}{(s.GeneratorMayBlock ? "; its Enqueue blocks for the generation, so it runs on the generator thread" : "")}");
        if (s.VfgLoad is { } load)
        {
            o.WriteLine(string.Create(Ic,
                $"              {s.VfxSdk}; loaded in {load.LoadNs / 1e6:0.0} ms, warmed up in {load.WarmupNs / 1e6:0.0} ms, {load.DeviceBytes / 1048576.0:0} MiB; " +
                $"automatic shot-change detection on; output written straight into the ring slots"));
        }

        o.WriteLine($"  device      {s.DeviceName}, Vulkan driver {s.VulkanDriver}; CUDA context scheduling {options.CudaSync}");
        o.WriteLine($"  display     {s.Display}; transform {s.Transform}");
        o.WriteLine(string.Create(Ic,
            $"  feedback    {s.FeedbackMode}; presents in flight capped at {(s.PresentsInFlightCap == 0 ? "nothing (cannot cap)" : s.PresentsInFlightCap.ToString(Ic))}"));
        o.WriteLine(string.Create(Ic,
            $"  ring        {s.RingSlots} slots, pitch {s.RingPitchBytes} B; bridge budget {s.OperationsPerWrite} CUDA operations per write, lead at most {s.MaxLead}"));
        var rate = s.Source.NominalRate;
        var cap = options.OutputHz is { } hz && rate.IsValid
            ? string.Create(Ic, $"--output-hz {hz:0.###} → cap {Math.Clamp((int)Math.Round(hz / rate.Hz), 1, 8)}")
            : $"cap {playback.Multiplier}";
        o.WriteLine(string.Create(Ic,
            $"  rates       signal {(rate.IsValid ? rate.Hz.ToString("0.###", Ic) + " fps" : "unknown")} (the game's own rate is what the cadence detector finds), multiplier {cap}; " +
            $"warm-up {playback.WarmupNs / 1e9:0.#} s{(playback.DurationNs > 0 ? $", duration {playback.DurationNs / 1e9:0.###} s" : ", until stopped")}"));
        if (f.Format == CapturePixelFormat.P010 && f.Height >= 2160 && f.Interval.Fps < 31)
        {
            o.WriteLine("  note        2160p P010 at 30: the card itself takes 30 of the console's 60 Hz; a game running at 60 loses every other frame " +
                "before the app sees it. For 60 fps games use --mode 1440p60-hdr.");
        }

        _ = cuda;
    }

    /// <summary>The capture's lines of the summary.</summary>
    public static void PrintCapture(TextWriter o, PlaybackReport r, CaptureSummary c)
    {
        var k = c.Counters;
        o.WriteLine(string.Create(Ic,
            $"  capture     {k.Dequeued} dequeued (stale, skipped as newest won: {k.SkippedStale}; dropped by the driver: {k.DriverDropped}; error-flagged {k.ErrorFrames}, " +
            $"short {k.ShortFrames}); {k.Captured} captured → {k.Unique} unique, {k.Repeats} repeats dropped, {k.DroppedBehind} dropped as the pipeline was behind, " +
            $"{k.Published} published ({k.Primed} of them repeats published to start on a still picture), {r.FramesRead} read by the pacing thread"));
        o.WriteLine(string.Create(Ic,
            $"              detected game rate {(k.DetectedFps > 0 ? k.DetectedFps.ToString("0.00", Ic) + " fps" : "not yet known")} (signal interval {Ms(c.SignalIntervalNs)} ms); " +
            $"holds {k.Holds} (gaps over twice the game's interval); black frames {k.BlackFrames}; no-signal episodes {k.NoSignalEpisodes}; " +
            $"state at the end {c.State}; timestamps repaired {k.TimestampRepairs}, not CLOCK_MONOTONIC {k.NonMonotonicTimestamps}"));
        o.WriteLine(string.Create(Ic,
            $"  capture t   timestamp intervals mean {Ms(c.Intervals.Mean)} ms, sd {Us(c.Intervals.StdDev)} µs, min {Ms(c.Intervals.Min)}, max {Ms(c.Intervals.Max)} ms over {c.Intervals.Count}; " +
            $"{c.DequeueLag.Ms("dequeue − timestamp (USB delivery)")}; {c.CaptureToAvailable.Ms("timestamp → frame available")}; {c.UploadWait.Ms("dequeue → upload judged")}"));
        o.WriteLine(string.Create(Ic,
            $"  capture gpu {c.UploadGpu.Ms("upload")}; {c.CadenceGpu.Ms("cadence metric")}; {c.ConvertGpu.Ms("convert")}"));
        o.WriteLine(string.Create(Ic,
            $"  cadence     metric (millionths of full scale) of new pictures: min {c.UniqueMetric.Min}, p50 {c.UniqueMetric.P50} over {c.UniqueMetric.Count}; " +
            $"of repeats: p50 {c.RepeatMetric.P50}, p99 {c.RepeatMetric.P99}, max {c.RepeatMetric.Max} over {c.RepeatMetric.Count}; " +
            $"unique-frame intervals p50 {Ms(c.UniqueIntervals.P50)}, p99 {Ms(c.UniqueIntervals.P99)}, max {Ms(c.UniqueIntervals.Max)} ms"));
        if (c.ThreadReport is { } thread)
        {
            o.WriteLine($"  capture th  {thread}");
        }
    }

    /// <summary>The latency line of a capture run: measured from the capture timestamp, and D against the latency model.</summary>
    public static void PrintLatency(TextWriter o, PlaybackReport r, LatencyEstimate model)
    {
        var s = r.Statistics;
        var d = r.Diagnostics;
        var lat = s.RealFrameLatency;
        var added = d.HoldNs + d.LeadNs + d.AlignmentNs;
        o.WriteLine(string.Create(Ic,
            $"  added       by frame generation (the scheduler's D less its arrival lag): hold {Ms(d.HoldNs)} + lead {Ms(d.LeadNs)} + alignment {Ms(d.AlignmentNs)} = " +
            $"{Ms(added)} ms; latency model ΔL = d + (M−1)/M·T = {Ms(model.GenerationTimeNs)} + {Ms(model.HoldNs)} = {Ms(model.AddedLatencyNs)} ms " +
            $"(M {model.Multiplier}, T {Ms(model.SourceIntervalNs)}); difference {Ms(added - model.AddedLatencyNs)} ms (the lead's wake and margin, the stride's alignment)"));
        o.WriteLine(lat.Count == 0
            ? "  latency     no real frame shown in the measured window"
            : string.Create(Ic,
                $"  latency     a real frame is shown {Ms(lat.Mean)} ms after its capture timestamp (p99 {Ms(lat.Percentile(0.99))}): arrival lag {Ms(d.ArrivalLagNs)} " +
                $"(USB delivery, upload, conversion) + the generation's {Ms(added)} + present lead {Ms(s.PresentLeads.Percentile(0.5))} + the stride's rounding; " +
                $"before the timestamp, the card's own HDMI-to-USB latency is not measurable here (photodiode or camera)"));
    }

    private static string CaptureFormatsText(CaptureSetup c) => string.Create(Ic,
        $"{c.Buffers} x {CaptureBuffersName(c.BufferMode)}, {Environment.SystemPageSize}-byte aligned");

    private static string CaptureBuffersName(CaptureBufferMode mode) => mode switch
    {
        CaptureBufferMode.UserPtrPinned => "USERPTR into CUDA pinned memory (the driver writes where the GPU copies from)",
        CaptureBufferMode.UserPtrRegistered => "USERPTR into anonymous memory registered with CUDA",
        CaptureBufferMode.MmapRegistered => "MMAP driver buffers registered with CUDA",
        _ => mode.ToString(),
    };

    private static string Ms(double ns) => (ns / 1e6).ToString("0.000", Ic);

    private static string Us(double ns) => (ns / 1e3).ToString("0.0", Ic);
}
