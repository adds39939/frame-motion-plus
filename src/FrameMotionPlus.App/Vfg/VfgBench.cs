using System.Diagnostics;
using FrameMotionPlus.App.Bench;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.App.Vfg;

/// <summary>One cell of the matrix: size, mode, format, multiplier, and what it measured.</summary>
internal sealed record VfgCellResult
{
    public required (int Width, int Height) Size { get; init; }

    public required FrameGenQuality Mode { get; init; }

    public required PixelFormat Format { get; init; }

    public required int Multiplier { get; init; }

    /// <summary>d: GPU span of each generated frame's run (CUDA events around the work on VFG's stream), ns.</summary>
    public Distribution D { get; init; }

    /// <summary>Host (CPU wall) time of each Enqueue call (the waits, rebinding, parameters and NvVFX_Run), ns.</summary>
    public Distribution Host { get; init; }

    public VfgLoadReport Load { get; init; }

    /// <summary>Generated frames whose slot had passed when their run started (paced runs only).</summary>
    public long Late { get; init; }

    /// <summary>Generated frames read back and checked.</summary>
    public int Checked { get; init; }

    /// <summary>Of those, outputs identical to a neighbour (a copy: shot-change handling bypassed the model).</summary>
    public int Copies { get; init; }

    /// <summary>Of the checked ones with a known truth, those closer to it than the linear blend (PSNR over the moving rows).</summary>
    public int BeatBlend { get; init; }

    public int TruthChecked { get; init; }

    public double MeanPsnrVfg { get; init; }

    public double MeanPsnrBlend { get; init; }

    /// <summary>Runs whose GPU span was not recorded (the timer ring was full); they are missing from <see cref="D"/>.</summary>
    public long DroppedSpans { get; init; }

    public bool IsGateCell => Size == (2560, 1440) && Mode == FrameGenQuality.Medium;

    /// <summary>Why the cell is not a valid measurement, if it is not.</summary>
    public string? Problem => Copies > 0
        ? $"{Copies} of {Checked} checked outputs were copies of a neighbour, not interpolations"
        : TruthChecked > 0 && BeatBlend < TruthChecked
            ? $"only {BeatBlend} of {TruthChecked} checked outputs were closer to the true frame than the blend"
            : null;
}

/// <summary>
/// <c>fmp bench vfg</c>: the design doc's Phase 0 benchmark. For each cell of sizes × modes × formats × multipliers, a
/// <see cref="VfgGenerator"/> configured for it (load and warm-up) generates frames between neighbouring frames of
/// moving content into four rotating outputs (the output rebound every run, as into ring slots), paced at the
/// pipeline's cadence. d is read from the generator's GPU spans; the host time of each Enqueue alongside. Every
/// <see cref="VfgBenchOptions.VerifyEvery"/>-th output is read back and checked to be a genuine interpolation.
/// </summary>
internal sealed class VfgBench
{
    private const int Outputs = 4;

    private readonly VfgBenchOptions _o;
    private readonly CudaContext _cuda;
    private readonly ImageKernels _kernels;
    private readonly TextWriter? _progress;

    public VfgBench(VfgBenchOptions options, CudaContext cuda, ImageKernels kernels, TextWriter? progress)
    {
        _o = options;
        _cuda = cuda;
        _kernels = kernels;
        _progress = progress;
    }

    public List<VfgCellResult> Run()
    {
        var results = new List<VfgCellResult>();
        using var generator = new VfgGenerator(_cuda, new VfgOptions { AutomaticShotChangeDetection = _o.ShotChangeDetection, MaxLoadedInstances = 1 });
        foreach (var size in _o.Sizes)
        {
            foreach (var format in _o.Formats)
            {
                using var content = VfgContent.Create(_o.Content, _o.Video, _cuda, _kernels, size.Width, size.Height, format);
                var outputs = new CudaDeviceBuffer[Outputs];
                try
                {
                    for (var i = 0; i < Outputs; i++)
                    {
                        outputs[i] = CudaDeviceBuffer.AllocateImage(_cuda, size.Width, size.Height, format);
                    }

                    using var checker = _o.VerifyEvery > 0 ? new OutputChecker(_cuda, content) : null;
                    foreach (var mode in _o.Modes)
                    {
                        foreach (var multiplier in _o.Multipliers)
                        {
                            var cell = RunCell(generator, content, outputs, checker, mode, multiplier);
                            results.Add(cell);
                            _progress?.WriteLine(VfgReport.Row(cell));
                            _progress?.Flush();
                        }
                    }
                }
                finally
                {
                    generator.Stream.Synchronize();
                    foreach (var o in outputs)
                    {
                        o?.Dispose();
                    }
                }
            }
        }

        return results;
    }

    private VfgCellResult RunCell(VfgGenerator generator, VfgContent content, CudaDeviceBuffer[] outputs, OutputChecker? checker, FrameGenQuality mode, int multiplier)
    {
        var config = new FrameGenConfig { Width = content.Width, Height = content.Height, Format = content.Format, Quality = mode };
        var loads = generator.Loads;
        generator.Configure(config);
        var load = generator.Loads != loads ? generator.LastLoad : default; // default: the previous cell's instance, no load
        var droppedBefore = generator.DroppedGpuTimes;
        while (generator.TryReadGpuTime(out _))
        {
        }

        using var done = CudaEvent.Create(_cuda);
        var perPair = multiplier - 1;
        var total = _o.WarmupFrames + _o.Frames;
        var d = new long[_o.Frames];
        var host = new long[_o.Frames];
        var dCount = 0;
        long late = 0;
        var periodNs = _o.Rate.PeriodNs;
        var start = Monotonic.NowNs + (20 * Nanoseconds.PerMillisecond);
        var targets = outputs.Select(o => o.AsFrame()).ToArray();
        var tally = new CheckTally();
        var spans = 0;
        var syncEvery = Math.Clamp(900 / (OperationsPerRun(mode) + 6), 1, 16);

        for (var n = 0; n < total; n++)
        {
            var pair = n / perPair;
            var inPair = n % perPair;
            var quarter = multiplier == 2 ? 2 : inPair + 1;
            var t = quarter / 4f;
            var (from, to) = content.Pair(pair);

            if (!_o.Unpaced)
            {
                // The generated slots of each source pair: slots 1 .. M-1 of M (slot 0 shows the real frame).
                var deadline = start + (long)((((long)pair * multiplier) + inPair + 1) * periodNs);
                var woke = DeadlineWaiter.Sleep.WaitUntil(deadline);
                if (woke - deadline > periodNs && n >= _o.WarmupFrames)
                {
                    late++;
                }
            }

            var output = targets[n % Outputs];
            var t0 = Stopwatch.GetTimestamp();
            generator.Enqueue(content.Frame(from), content.Frame(to), t, output, done.Signal);
            var hostNs = (long)Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
            var measured = n >= _o.WarmupFrames;
            if (measured)
            {
                host[n - _o.WarmupFrames] = hostNs;
            }

            if (checker is not null && measured && (n - _o.WarmupFrames) % _o.VerifyEvery == 0)
            {
                done.Synchronize();
                checker.Check(from, to, quarter, output, tally);
            }

            if (!_o.ShotChangeDetection && n % syncEvery == syncEvery - 1)
            {
                // Enqueue does not wait with detection off: keep the GPU at most syncEvery runs behind, so neither the
                // timer ring (32 spans) nor the stream's ~1,024-operation queue fills (a run is about 28, 33 or 170
                // operations in Low, Medium and High, plus 6 for the waits, span and signal); a full queue would make
                // the call itself block and inflate the host times.
                done.Synchronize();
            }

            Drain(generator, d, ref dCount, ref spans);
        }

        done.Synchronize();
        generator.Stream.Synchronize();
        Drain(generator, d, ref dCount, ref spans);
        return new VfgCellResult
        {
            Size = (content.Width, content.Height),
            Mode = mode,
            Format = content.Format,
            Multiplier = multiplier,
            D = Distribution.Of(d.AsSpan(0, dCount)),
            Host = Distribution.Of(host.AsSpan(0, _o.Frames)),
            Load = load,
            Late = late,
            Checked = tally.Checked,
            Copies = tally.Copies,
            BeatBlend = tally.BeatBlend,
            TruthChecked = tally.TruthChecked,
            MeanPsnrVfg = tally.TruthChecked == 0 ? 0 : tally.PsnrVfg / tally.TruthChecked,
            MeanPsnrBlend = tally.TruthChecked == 0 ? 0 : tally.PsnrBlend / tally.TruthChecked,
            DroppedSpans = generator.DroppedGpuTimes - droppedBefore,
        };
    }

    /// <summary>Stream operations one VFG run enqueues (measured in CharacterizationTests, the same at 1080p and 2160p).</summary>
    internal static int OperationsPerRun(FrameGenQuality mode) => mode switch
    {
        FrameGenQuality.Low => 29,
        FrameGenQuality.High => 171,
        _ => 33,
    };

    /// <summary>Reads finished spans (in order); the first <see cref="VfgBenchOptions.WarmupFrames"/> of a cell are its warm-up's.</summary>
    private void Drain(VfgGenerator generator, long[] samples, ref int count, ref int spans)
    {
        while (generator.TryReadGpuTime(out var ns))
        {
            spans++;
            if (spans > _o.WarmupFrames && count < samples.Length)
            {
                samples[count++] = ns;
            }
        }
    }

    /// <summary>Running totals of the output checks of one cell.</summary>
    private sealed class CheckTally
    {
        public int Checked;
        public int Copies;
        public int TruthChecked;
        public int BeatBlend;
        public double PsnrVfg;
        public double PsnrBlend;
    }

    /// <summary>Reads outputs back and checks them against their neighbours and, where known, the true frame.</summary>
    private sealed class OutputChecker : IDisposable
    {
        private readonly VfgContent _content;
        private readonly CudaStream _stream;
        private readonly CudaDeviceBuffer _truth;
        private readonly uint[] _out;
        private readonly uint[] _prev;
        private readonly uint[] _curr;
        private readonly uint[] _true;
        private readonly uint[] _scratch;

        public OutputChecker(CudaContext cuda, VfgContent content)
        {
            _content = content;
            _stream = CudaStream.Create(cuda);
            _truth = CudaDeviceBuffer.AllocateImage(cuda, content.Width, content.Height, content.Format);
            var n = content.Width * content.Height;
            _out = new uint[n];
            _prev = new uint[n];
            _curr = new uint[n];
            _true = new uint[n];
            _scratch = new uint[n];
        }

        public void Check(int from, int to, int quarter, in GpuFrame output, CheckTally tally)
        {
            _stream.CopyFrameToHost(output, _out);
            _stream.CopyFrameToHost(_content.Frame(from), _prev);
            _stream.CopyFrameToHost(_content.Frame(to), _curr);
            tally.Checked++;
            if (_out.AsSpan().SequenceEqual(_prev) || _out.AsSpan().SequenceEqual(_curr))
            {
                tally.Copies++;
                return;
            }

            if (!_content.HasTruth)
            {
                return;
            }

            _content.DrawTruth(_stream, from, to, quarter, _truth.AsFrame(), _scratch);
            _stream.CopyFrameToHost(_truth.AsFrame(), _true);
            var (y0, y1) = _content.MotionRows;
            var format = _content.Format;
            var step = _content.Kind == VfgContentKind.Pattern ? 1 : 2; // every other row of a full-frame pan: plenty
            var xEnd = _content.Width - _content.MotionPixels - 8; // content entering at the right edge has no truth
            var vfg = Psnr(_out, _true, null, 0, quarter / 4.0, y0, y1, step, xEnd, format);
            var blend = Psnr(_prev, _true, _curr, 0, quarter / 4.0, y0, y1, step, xEnd, format);
            tally.TruthChecked++;
            tally.PsnrVfg += vfg;
            tally.PsnrBlend += blend;
            tally.BeatBlend += vfg > blend ? 1 : 0;
        }

        /// <summary>
        /// PSNR of <paramref name="a"/> (or, with <paramref name="b2"/>, of the blend (1 − t)·a + t·b2) against
        /// <paramref name="truth"/>, over rows [y0, y1) in steps and columns [0, xEnd).
        /// </summary>
        private double Psnr(uint[] a, uint[] truth, uint[]? b2, int x0, double t, int y0, int y1, int rowStep, int xEnd, PixelFormat format)
        {
            var width = _content.Width;
            double sum = 0;
            long n = 0;
            for (var y = y0; y < y1; y += rowStep)
            {
                for (var x = x0; x < xEnd; x++)
                {
                    var i = (y * width) + x;
                    var (ar, ag, ab) = PixelCodes.Unpack(a[i], format);
                    double r = ar, g = ag, b = ab;
                    if (b2 is not null)
                    {
                        var (br, bg, bb) = PixelCodes.Unpack(b2[i], format);
                        r = Math.Round(((1 - t) * ar) + (t * br));
                        g = Math.Round(((1 - t) * ag) + (t * bg));
                        b = Math.Round(((1 - t) * ab) + (t * bb));
                    }

                    var (tr, tg, tb) = PixelCodes.Unpack(truth[i], format);
                    sum += ((r - tr) * (r - tr)) + ((g - tg) * (g - tg)) + ((b - tb) * (b - tb));
                    n += 3;
                }
            }

            var mse = sum / Math.Max(1, n);
            var peak = (double)PixelCodes.MaxCode(format);
            return mse == 0 ? 99 : 10 * Math.Log10(peak * peak / mse);
        }

        public void Dispose()
        {
            _truth.Dispose();
            _stream.Dispose();
        }
    }
}
