using System.Diagnostics;
using System.Globalization;
using System.Text;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File;

namespace FrameMotionPlus.App.Decode;

/// <summary>What one <c>fmp decode</c> run found.</summary>
internal sealed class DecodeResult
{
    public FileStreamInfo? Stream { get; set; }

    public long Frames { get; set; }

    public bool ReachedEnd { get; set; }

    public long Discontinuities { get; set; }

    public long FormatChanges { get; set; }

    public long NonIncreasing { get; set; }

    public long MinIntervalNs { get; set; } = long.MaxValue;

    public long MaxIntervalNs { get; set; }

    public double MeanIntervalNs { get; set; }

    public bool VariableFrameRate { get; set; }

    public long VerifiedFrames { get; set; }

    public long YuvMismatches { get; set; }

    public long RgbMismatches { get; set; }

    public long SoftwareFramesShort { get; set; }

    /// <summary>Frames the software decode had beyond ours at the end of the file or of a lap.</summary>
    public long SoftwareFramesExtra { get; set; }

    /// <summary>Frames whose stream timestamps jumped (marked discontinuities that are neither a loop nor a seek).</summary>
    public long TimestampJumps { get; set; }

    /// <summary>The first few of them: frame id and media time.</summary>
    public List<(long Frame, long MediaNs)> JumpFrames { get; } = [];

    /// <summary>Content-time steps under 1 µs (a repaired collision is 1 ns).</summary>
    public long CollapsedSteps { get; set; }

    /// <summary>Two or more collapsed steps in a row: frames squeezed together, which the source must never deliver.</summary>
    public long CollapsedRuns { get; set; }

    /// <summary>The source could not be freed: its decode thread did not stop.</summary>
    public bool Leaked { get; set; }

    public string? DemuxError { get; set; }

    public string? FirstMismatch { get; set; }

    public long ConsumerAllocatedBytes { get; set; }

    public int Gen0 { get; set; }

    public int Gen1 { get; set; }

    public int Gen2 { get; set; }

    public FileSourceStatistics Statistics { get; set; }

    public StallReport? Baseline { get; set; }

    public StallReport? Stall { get; set; }

    public List<DecodeTiming> Timings { get; } = [];

    public string? Failure { get; set; }

    public double Seconds { get; set; }

    public bool Passed => Problems().Count == 0;

    /// <summary>Every check that failed, in words (empty: the run passed).</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        void Check(bool failed, string what)
        {
            if (failed)
            {
                p.Add(what);
            }
        }

        var s = Statistics;
        Check(Failure is not null, "the run failed");
        Check(YuvMismatches > 0, $"{YuvMismatches} decoded frames differ from the software decode");
        Check(RgbMismatches > 0, $"{RgbMismatches} converted frames differ from the CPU reference");
        Check(SoftwareFramesShort > 0, $"the software decode had {SoftwareFramesShort} frames fewer");
        Check(SoftwareFramesExtra > 0, $"the software decode had {SoftwareFramesExtra} frames more");
        Check(NonIncreasing > 0, $"{NonIncreasing} content times did not increase");
        Check(CollapsedRuns > 0, $"{CollapsedRuns} runs of content-time steps under 1 µs");
        Check(DemuxError is not null, "demuxing ended early");
        Check(s.CorruptPackets + s.CorruptFrames > 0, $"{s.CorruptPackets} corrupt packets, {s.CorruptFrames} concealed frames");
        Check(s.FailedSeeks > 0, $"{s.FailedSeeks} loops or seeks could not be made");
        Check(s.PoolGrowth > 0, $"FFmpeg's frame pool grew {s.PoolGrowth} times during playback");
        Check(s.DecodeThreadAllocatedBytes > 0, $"the decode thread allocated {s.DecodeThreadAllocatedBytes} B per frame");
        Check(ConsumerAllocatedBytes > 0, $"the consumer path allocated {ConsumerAllocatedBytes} B");
        Check(Leaked, "the source could not be freed");
        return p;
    }
}

/// <summary>
/// <c>fmp decode</c>: plays a file through the real source path (FFmpeg demux, NVDEC, the convert kernel, the frame
/// history) as fast as it decodes and checks it: content times strictly increasing; with <c>--verify</c> every decoded
/// NV12/P010 picture bit-exact against the ffmpeg CLI's software decode and every converted frame bit-exact against
/// the CPU reference (<see cref="YuvReference"/>). Meanwhile another thread's CUDA calls are timed
/// (<see cref="StallProbe"/>) and the consumer's allocations counted.
/// </summary>
internal sealed class DecodeRun
{
    private readonly DecodeOptions _o;
    private readonly TextWriter _out;

    public DecodeRun(DecodeOptions options, TextWriter output)
    {
        _o = options;
        _out = output;
    }

    /// <summary>Checks the GPU and FFmpeg (and, for --verify, the ffmpeg CLI); throws <see cref="HardwareUnavailableException"/> otherwise.</summary>
    public static CudaDevice Require(DecodeOptions options)
    {
        if (!CudaDriver.TryInitialize(out var reason))
        {
            throw new HardwareUnavailableException($"needs an NVIDIA GPU with the CUDA driver: {reason}");
        }

        if (CudaDriver.DeviceCount == 0)
        {
            throw new HardwareUnavailableException("needs an NVIDIA GPU: the CUDA driver reports no device");
        }

        if (!FileFrameSource.IsAvailable(out var ffmpeg))
        {
            throw new HardwareUnavailableException($"needs the FFmpeg 9 shared libraries: {ffmpeg}");
        }

        if (options.Verify && !SoftwareDecode.Exists(options.Ffmpeg))
        {
            throw new HardwareUnavailableException($"--verify needs the ffmpeg CLI ('{options.Ffmpeg}' does not run)");
        }

        return CudaDriver.GetDevice(0);
    }

    public DecodeResult Run(CudaContext cuda, ImageKernels kernels)
    {
        var result = new DecodeResult();
        using var source = new FileFrameSource(cuda, _o.Path, new FileSourceOptions
        {
            Loop = _o.Loop,
            KeepDecodedPlanes = _o.Verify,
            DecodeAhead = _o.DecodeAhead,
            Kernels = kernels,
        });
        var info = source.StreamInfo;
        result.Stream = info;
        PrintStream(info);
        var limit = _o.Frames > 0 ? _o.Frames
            : _o.DurationS > 0 ? long.MaxValue
            : _o.Loop ? (info.DeclaredFrames > 0 ? 3 * info.DeclaredFrames : 600)
            : long.MaxValue;
        var deadline = _o.DurationS > 0 ? Stopwatch.GetTimestamp() + (long)(_o.DurationS * Stopwatch.Frequency) : long.MaxValue;

        if (_o.StallProbe)
        {
            // The same probe with nothing else running first, so its own noise floor is known.
            using var baseline = StallProbe.Start(cuda, kernels);
            Thread.Sleep(500);
            result.Baseline = baseline.Stop();
        }

        using var verifier = _o.Verify ? new FrameVerifier(_o, info, cuda) : null;
        using var probe = _o.StallProbe ? StallProbe.Start(cuda, kernels) : null;
        var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var started = Stopwatch.GetTimestamp();
        source.Start();
        long last = long.MinValue, intervals = 0;
        double intervalSum = 0;
        var tick = info.TimeBaseDen > 0 ? 1e9 * info.TimeBaseNum / info.TimeBaseDen : 0;
        var collapsedBefore = false;
        var idleSince = Stopwatch.GetTimestamp();
        while (result.Frames < limit && Stopwatch.GetTimestamp() < deadline)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var got = source.TryRead(out var frame);
            result.ConsumerAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            DrainTimings(source, result);
            if (!got)
            {
                if (source.Failure is { } failure)
                {
                    result.Failure = $"the decode thread failed: {failure.Message}";
                    break;
                }

                if (source.IsEndOfStream)
                {
                    result.ReachedEnd = true;
                    break;
                }

                if (Stopwatch.GetElapsedTime(idleSince) > TimeSpan.FromSeconds(30))
                {
                    result.Failure = "no frame for 30 s";
                    break;
                }

                Thread.Sleep(0);
                continue;
            }

            idleSince = Stopwatch.GetTimestamp();
            _ = source.TryGetFrameInfo(frame.FrameId, out var position);
            if ((position.Events & FileFrameEvents.TimestampJump) != 0)
            {
                result.TimestampJumps++;
                if (result.JumpFrames.Count < 5)
                {
                    result.JumpFrames.Add((frame.FrameId, position.MediaTimeNs));
                }
            }

            if (last != long.MinValue)
            {
                var step = frame.ContentTimeNs - last;
                var collapsed = step < 1_000;
                result.CollapsedSteps += collapsed ? 1 : 0;
                result.CollapsedRuns += collapsed && collapsedBefore ? 1 : 0;
                collapsedBefore = collapsed;
            }

            if ((frame.Marks & SourceFrameMarks.Discontinuity) != 0)
            {
                result.Discontinuities++;
                last = long.MinValue; // no interval across a discontinuity
            }

            if ((frame.Marks & SourceFrameMarks.FormatChanged) != 0)
            {
                result.FormatChanges++;
            }

            if (last != long.MinValue)
            {
                var interval = frame.ContentTimeNs - last;
                if (interval <= 0)
                {
                    result.NonIncreasing++;
                }

                result.MinIntervalNs = Math.Min(result.MinIntervalNs, interval);
                result.MaxIntervalNs = Math.Max(result.MaxIntervalNs, interval);
                intervalSum += interval;
                intervals++;
            }

            last = frame.ContentTimeNs;
            verifier?.Check(source, frame, position, result);
            before = GC.GetAllocatedBytesForCurrentThread();
            source.Release(frame.FrameId);
            result.ConsumerAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            result.Frames++;
        }

        result.Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        try
        {
            source.Stop();
        }
        catch (TimeoutException e)
        {
            // Disposing cannot free it either (FileFrameSource.Leaked): report it rather than wait again.
            result.Failure ??= e.Message;
            result.Leaked = true;
        }

        result.Stall = probe?.Stop();
        if (result.Failure is null)
        {
            source.ComputeStream.Synchronize();
            DrainTimings(source, result); // the decode thread is stopped: this collects what completed since
        }

        result.Gen0 = GC.CollectionCount(0) - gc0.Item1;
        result.Gen1 = GC.CollectionCount(1) - gc0.Item2;
        result.Gen2 = GC.CollectionCount(2) - gc0.Item3;
        result.Statistics = source.Statistics;
        result.MeanIntervalNs = intervals > 0 ? intervalSum / intervals : 0;
        if (intervals == 0)
        {
            result.MinIntervalNs = 0;
        }

        // Variable frame rate: intervals that differ by more than the time base's rounding and 2 % of the mean.
        result.VariableFrameRate = intervals > 1 && result.MaxIntervalNs - result.MinIntervalNs > Math.Max(2 * tick, 0.02 * result.MeanIntervalNs);
        result.DemuxError = source.DemuxError;
        if (source.DemuxError is { } demux)
        {
            _out.WriteLine($"  note        demuxing ended early: {demux} (a truncated or damaged file)");
        }

        if (source.SeekError is { } seek)
        {
            _out.WriteLine($"  note        {seek}");
        }

        if (verifier is not null)
        {
            if (result.ReachedEnd)
            {
                verifier.EndOfPass(); // the software decode must have no frame left either
            }

            result.SoftwareFramesShort += verifier.SoftwareShort;
            result.SoftwareFramesExtra += verifier.SoftwareExtra;
        }

        return result;
    }

    public void Print(DecodeResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        var s = r.Statistics;
        _out.WriteLine(string.Create(inv, $"  frames      {r.Frames} in {r.Seconds:0.00} s ({(r.Seconds > 0 ? r.Frames / r.Seconds : 0):0} fps){(r.ReachedEnd ? ", to the end of the file" : string.Empty)}; laps {s.Loops}; decoded {s.FramesDecoded}"));
        _out.WriteLine(string.Create(inv, $"  pts         interval min {r.MinIntervalNs / 1e6:0.000} / mean {r.MeanIntervalNs / 1e6:0.000} / max {r.MaxIntervalNs / 1e6:0.000} ms, {(r.VariableFrameRate ? "variable frame rate" : "constant frame rate")}; non-increasing {r.NonIncreasing}; repaired {s.PtsRepairs}, missing {s.PtsMissing}; discontinuities {r.Discontinuities}, format changes {r.FormatChanges}"));
        var jumps = string.Join(", ", r.JumpFrames.Select(j => string.Create(inv, $"frame {j.Frame} at media {j.MediaNs / 1e9:0.000} s")));
        _out.WriteLine(string.Create(inv, $"  timestamps  {r.TimestampJumps} jumps in the stream's own timestamps{(jumps.Length > 0 ? $" ({jumps}{(r.TimestampJumps > r.JumpFrames.Count ? ", ..." : string.Empty)})" : string.Empty)}; {s.PtsRepairs} isolated collisions moved 1 ns on; {r.CollapsedSteps} steps under 1 µs, {r.CollapsedRuns} runs of them"));
        var gaps = r.Stream is { DiscontinuousTimestamps: true }
            ? "collapsed to one frame (the container's clock may jump)"
            : "kept (the container's timeline is continuous)";
        _out.WriteLine($"              jumps back continue one frame on; gaps forward over 10 s are marked and {gaps}");
        if (s.Seeks + s.Loops > 0)
        {
            _out.WriteLine($"  restarts    {s.Loops} loops, {s.Seeks} seeks; {s.SeeksFromStart} from the first byte; {s.FailedSeeks} failed; {s.StaleFramesDropped} frames from before them dropped");
        }

        var timings = r.Timings;
        if (timings.Count > 0)
        {
            _out.WriteLine($"  gpu         decode→copy span {Stats(timings.Where(t => t.DecodeCopyGpuNs > 0).Select(t => t.DecodeCopyGpuNs))}; convert {Stats(timings.Select(t => t.ConvertGpuNs))}");
            _out.WriteLine($"  host        per frame in FFmpeg on the decode thread {Stats(timings.Where(t => t.HostDecodeNs > 0).Select(t => t.HostDecodeNs))}");
            _out.WriteLine(string.Create(inv, $"  queue       depth mean {timings.Average(t => t.QueueDepth):0.0}, max {s.MaxQueueDepth}; back-pressure waits {s.BackPressureWaits}"));
        }

        _out.WriteLine($"  memory      FFmpeg frame-pool growth after start-up {s.PoolGrowth}; decode thread {s.DecodeThreadAllocatedBytes} B allocated after its first frames; consumer path {r.ConsumerAllocatedBytes} B; GC {r.Gen0}/{r.Gen1}/{r.Gen2} (gen0/1/2)");
        if (r.Stall is { } stall)
        {
            _out.WriteLine($"  other thread {stall}{(r.Baseline is { } b ? $" (alone, before decoding: worst {b.MaxNs / 1e6:0.000} ms, p99.9 {b.P999Ns / 1e6:0.000} ms)" : string.Empty)}");
        }

        if (s.CorruptFrames + s.CorruptPackets > 0)
        {
            _out.WriteLine($"  corrupt     {s.CorruptPackets} packets refused, {s.CorruptFrames} frames concealed");
        }

        if (_o.Verify)
        {
            _out.WriteLine($"  verify      {r.VerifiedFrames} frames: decoded planes {(r.YuvMismatches == 0 ? "bit-exact with" : $"differ in {r.YuvMismatches} frames from")} the software decode; RGB {(r.RgbMismatches == 0 ? "bit-exact with" : $"differs in {r.RgbMismatches} frames from")} the CPU reference{(r.SoftwareFramesShort > 0 ? $"; the software decode had {r.SoftwareFramesShort} frames fewer" : string.Empty)}");
            if (r.FirstMismatch is { } m)
            {
                _out.WriteLine($"  first       {m}");
            }
        }

        if (r.Failure is { } f)
        {
            _out.WriteLine($"  failure     {f}");
        }

        var problems = r.Problems();
        _out.WriteLine($"  verdict     {(problems.Count == 0 ? "pass" : "FAIL: " + string.Join("; ", problems))}");
    }

    public static void WriteCsv(string path, DecodeResult r)
    {
        var b = new StringBuilder("frame,content_ns,media_ns,host_decode_ns,decode_copy_gpu_ns,convert_gpu_ns,queue_depth,discontinuity,format_changed,events\n");
        foreach (var t in r.Timings)
        {
            b.Append(CultureInfo.InvariantCulture,
                $"{t.FrameId},{t.ContentTimeNs},{t.MediaTimeNs},{t.HostDecodeNs},{t.DecodeCopyGpuNs},{t.ConvertGpuNs},{t.QueueDepth},{((t.Marks & SourceFrameMarks.Discontinuity) != 0 ? 1 : 0)},{((t.Marks & SourceFrameMarks.FormatChanged) != 0 ? 1 : 0)},{Events(t.Events)}\n");
        }

        File.WriteAllText(path, b.ToString());
    }

    private static string Events(FileFrameEvents events) => events == FileFrameEvents.None ? string.Empty
        : events.ToString().Replace(", ", "|", StringComparison.Ordinal);

    private static void DrainTimings(FileFrameSource source, DecodeResult result)
    {
        while (source.TryReadTiming(out var timing))
        {
            result.Timings.Add(timing);
        }
    }

    private static string Stats(IEnumerable<long> values)
    {
        var v = values.Order().ToArray();
        if (v.Length == 0)
        {
            return "n/a";
        }

        static string Ms(long ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
        return $"p50 {Ms(v[v.Length / 2])} / p99 {Ms(v[Math.Min(v.Length - 1, (int)(v.Length * 0.99))])} / max {Ms(v[^1])} ms";
    }

    private void PrintStream(FileStreamInfo i)
    {
        var inv = CultureInfo.InvariantCulture;
        _out.WriteLine($"fmp decode {i.Path}");
        _out.WriteLine($"  ffmpeg      {FileFrameSource.FFmpegVersions}");
        _out.WriteLine($"  stream      {i.Describe()}");
        _out.WriteLine(string.Create(inv, $"  timing      time base {i.TimeBaseNum}/{i.TimeBaseDen}, start {(i.StartTicks == long.MinValue ? "unknown" : i.StartTicks.ToString(inv))}, duration {i.DurationNs / 1e9:0.000} s, {(i.DeclaredFrames > 0 ? $"{i.DeclaredFrames} frames declared" : "frame count not declared")}"));
        _out.WriteLine($"  colour      {i.RawColor}");
        if (i.ContainerRawColor != i.RawColor)
        {
            _out.WriteLine($"  container   {i.ContainerRawColor}");
        }

        var c = i.Color;
        _out.WriteLine($"  frames      RGB ({c.Matrix} matrix, {c.Range} range), {c.Primaries} primaries, {c.Transfer} transfer{(c.IsHdr ? " (HDR, passed through)" : string.Empty)}");
        if (c.Mastering.IsSpecified)
        {
            var m = c.Mastering;
            _out.WriteLine(string.Create(inv, $"  mastering   R {m.Red.X:0.####},{m.Red.Y:0.####} G {m.Green.X:0.####},{m.Green.Y:0.####} B {m.Blue.X:0.####},{m.Blue.Y:0.####} W {m.WhitePoint.X:0.####},{m.WhitePoint.Y:0.####}, {m.MinLuminanceNits:0.####}-{m.MaxLuminanceNits:0.#} nits"));
        }

        if (c.LightLevel.IsSpecified)
        {
            _out.WriteLine($"  light level MaxCLL {c.LightLevel.MaxCll}, MaxFALL {c.LightLevel.MaxFall} nits");
        }

        _out.WriteLine($"  convert     {i.Conversion}");
        _out.Flush();
    }

    /// <summary>The per-frame checks of <c>--verify</c>.</summary>
    private sealed class FrameVerifier : IDisposable
    {
        private readonly DecodeOptions _o;
        private readonly FileStreamInfo _info;
        private readonly CudaStream _download;
        private readonly CudaDeviceBuffer _lumaStaging;
        private readonly CudaDeviceBuffer _chromaStaging;
        private readonly int _bps;
        private readonly byte[] _software;
        private readonly byte[] _luma;
        private readonly byte[] _chroma;
        private readonly uint[] _rgb;
        private readonly uint[] _expected;
        private readonly ushort[] _y;
        private readonly ushort[] _cb;
        private readonly ushort[] _cr;
        private readonly Dictionary<(MatrixCoefficients, ColorRange, ChromaSiting), YuvReference> _references = [];
        private SoftwareDecode? _decode;

        public FrameVerifier(DecodeOptions options, FileStreamInfo info, CudaContext cuda)
        {
            _o = options;
            _info = info;
            var pixFmt = SoftwareDecode.ProbePixelFormat(SoftwareDecode.Sibling(options.Ffmpeg, "ffprobe"), options.Path);
            _bps = pixFmt switch
            {
                "yuv420p" or "yuvj420p" => 1,
                "yuv420p10le" => 2,
                _ => throw new InvalidOperationException($"--verify compares 4:2:0 8- or 10-bit software decodes; ffmpeg decodes this stream as {pixFmt}"),
            };
            if ((_bps == 2) != (info.BitDepth > 8))
            {
                throw new InvalidOperationException($"the software decode is {pixFmt}, the stream {info.BitDepth}-bit");
            }

            var w = info.Width;
            var h = info.Height;
            var cw = (w + 1) / 2;
            var ch = (h + 1) / 2;
            _download = CudaStream.Create(cuda);
            _lumaStaging = CudaDeviceBuffer.Allocate(cuda, (long)w * h * _bps);
            _chromaStaging = CudaDeviceBuffer.Allocate(cuda, (long)cw * 2 * ch * _bps);
            _software = new byte[((long)w * h + (2L * cw * ch)) * _bps];
            _luma = new byte[(long)w * h * _bps];
            _chroma = new byte[(long)cw * 2 * ch * _bps];
            _rgb = new uint[w * h];
            _expected = new uint[w * h];
            _y = new ushort[w * h];
            _cb = new ushort[cw * ch];
            _cr = new ushort[cw * ch];
        }

        public long SoftwareShort { get; private set; }

        public long SoftwareExtra { get; private set; }

        /// <summary>
        /// The file ended (or a lap did): the software decode must have no frame left. Its leftovers are counted, then it
        /// is closed (the next pass starts a new one).
        /// </summary>
        public void EndOfPass()
        {
            if (_decode is null)
            {
                return;
            }

            while (_decode.Read(_software))
            {
                SoftwareExtra++;
            }

            _decode.Dispose();
            _decode = null;
        }

        public void Check(FileFrameSource source, in SourceFrame frame, in FileFrameInfo position, DecodeResult result)
        {
            // The file restarted (a new lap): so does the software decode, which first must have ended where ours did. A jump
            // in the stream's own timestamps is not a restart: the software decode goes on through it.
            if ((position.Events & FileFrameEvents.Loop) != 0)
            {
                var extra = SoftwareExtra;
                EndOfPass();
                if (SoftwareExtra > extra)
                {
                    Mismatch(result, $"frame {frame.FrameId}: a new lap began while the software decode still had {SoftwareExtra - extra} frames of the last one");
                }
            }

            if ((position.Events & FileFrameEvents.Seek) != 0)
            {
                throw new InvalidOperationException("--verify follows the file from its start and cannot follow a seek");
            }

            _decode ??= SoftwareDecode.Start(_o.Ffmpeg, _o.Path, _info.Width, _info.Height, _bps);

            if (!source.TryGetPlanes(frame.FrameId, out var planes))
            {
                throw new InvalidOperationException("the source kept no decoded planes");
            }

            var w = _info.Width;
            var h = _info.Height;
            var cw = (w + 1) / 2;
            var ch = (h + 1) / 2;
            _download.Wait(frame.Ready);
            _download.Copy2DAsync(_lumaStaging.Address, (long)w * _bps, planes.Luma, planes.LumaPitch, (long)w * _bps, h);
            _download.Copy2DAsync(_chromaStaging.Address, (long)cw * 2 * _bps, planes.Chroma, planes.ChromaPitch, (long)cw * 2 * _bps, ch);
            _download.CopyToHost<byte>(_lumaStaging.Address, _luma);
            _download.CopyToHost<byte>(_chromaStaging.Address, _chroma);
            _download.CopyFrameToHost(frame.Frame, _rgb);
            Deinterleave(cw, ch);
            result.VerifiedFrames++;
            if (!_decode.Read(_software))
            {
                SoftwareShort++;
                Mismatch(result, $"frame {frame.FrameId}: the software decode has no frame here ({_decode.Errors})");
                return;
            }

            if (!PlanesEqual(cw, ch, out var where))
            {
                result.YuvMismatches++;
                Mismatch(result, $"frame {frame.FrameId} (content {frame.ContentTimeNs / 1e6:0.000} ms): decoded {where} differs from the software decode");
            }

            // The reference converts as the frame was converted: the stream's matrix, range and siting at that frame (the
            // frame's own colour metadata describes its RGB pixels, not the YCbCr they came from).
            var conversion = position.Conversion;
            var key = (conversion.Matrix, conversion.Range, conversion.Siting);
            if (!_references.TryGetValue(key, out var reference))
            {
                reference = YuvReference.Create(conversion.Matrix, conversion.Range, _info.BitDepth, conversion.Siting, _info.OutputFormat);
                _references[key] = reference;
            }

            reference.Convert(_y, _cb, _cr, w, h, _expected);
            if (!_rgb.AsSpan().SequenceEqual(_expected))
            {
                result.RgbMismatches++;
                var i = _rgb.AsSpan().CommonPrefixLength(_expected);
                Mismatch(result, string.Create(CultureInfo.InvariantCulture,
                    $"frame {frame.FrameId}: RGB pixel ({i % w}, {i / w}) is 0x{_rgb[i]:x8}, the CPU reference 0x{_expected[i]:x8}"));
            }
        }

        public void Dispose()
        {
            _decode?.Dispose();
            _download.Synchronize();
            _lumaStaging.Dispose();
            _chromaStaging.Dispose();
            _download.Dispose();
        }

        private static void Mismatch(DecodeResult result, string text) => result.FirstMismatch ??= text;

        /// <summary>Our planes into code values: luma, and the interleaved chroma split (P010: the high 10 bits).</summary>
        private void Deinterleave(int cw, int ch)
        {
            if (_bps == 1)
            {
                for (var i = 0; i < _y.Length; i++)
                {
                    _y[i] = _luma[i];
                }

                for (var i = 0; i < cw * ch; i++)
                {
                    _cb[i] = _chroma[2 * i];
                    _cr[i] = _chroma[(2 * i) + 1];
                }

                return;
            }

            for (var i = 0; i < _y.Length; i++)
            {
                _y[i] = (ushort)(BitConverter.ToUInt16(_luma, 2 * i) >> 6);
            }

            for (var i = 0; i < cw * ch; i++)
            {
                _cb[i] = (ushort)(BitConverter.ToUInt16(_chroma, 4 * i) >> 6);
                _cr[i] = (ushort)(BitConverter.ToUInt16(_chroma, (4 * i) + 2) >> 6);
            }
        }

        /// <summary>Software decode (planar Y, U, V; 10-bit in the low bits) against ours; P010's low six bits must be zero.</summary>
        private bool PlanesEqual(int cw, int ch, out string where)
        {
            var w = _info.Width;
            var h = _info.Height;
            var n = w * h;
            for (var i = 0; i < n; i++)
            {
                var sw = _bps == 1 ? _software[i] : BitConverter.ToUInt16(_software, 2 * i);
                var lowBits = _bps == 1 ? 0 : BitConverter.ToUInt16(_luma, 2 * i) & 0x3F;
                if (_y[i] != sw || lowBits != 0)
                {
                    // Code values on both sides (P010's sample shifted down to its 10 bits); P010's six low bits must be 0.
                    where = $"luma ({i % w}, {i / w}): {_y[i]} vs {sw}{(lowBits != 0 ? $" (P010 low bits 0x{lowBits:x2})" : string.Empty)}";
                    return false;
                }
            }

            for (var i = 0; i < cw * ch; i++)
            {
                var u = _bps == 1 ? _software[n + i] : BitConverter.ToUInt16(_software, 2 * (n + i));
                var v = _bps == 1 ? _software[n + (cw * ch) + i] : BitConverter.ToUInt16(_software, 2 * (n + (cw * ch) + i));
                if (_cb[i] != u || _cr[i] != v)
                {
                    where = $"chroma ({i % cw}, {i / cw}): {_cb[i]},{_cr[i]} vs {u},{v}";
                    return false;
                }
            }

            where = string.Empty;
            return true;
        }
    }
}
