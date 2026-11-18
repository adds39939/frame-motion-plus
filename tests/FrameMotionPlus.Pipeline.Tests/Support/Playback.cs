using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.File;

namespace FrameMotionPlus.Pipeline.Tests.Support;

/// <summary>Runs a session on a thread of its own with a bound, so a hang fails the test instead of the run.</summary>
internal static class Playback
{
    /// <summary>Opens and runs a session; <paramref name="during"/> runs on the test thread while it plays.</summary>
    public static PlaybackReport Run(
        CudaContext cuda,
        PlaybackOptions options,
        PlaybackHooks? hooks = null,
        Action<PlaybackSession>? during = null,
        Action<PlaybackSession>? onOpen = null,
        double timeoutS = 120)
    {
        using var session = new PlaybackSession(options, cuda, hooks, validationEcho: options.Validate ? new LogWriter() : null);
        _ = session.Open();
        onOpen?.Invoke(session);
        PlaybackReport? report = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                report = session.Run();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-test-run" };
        thread.Start();
        during?.Invoke(session);
        if (!thread.Join(TimeSpan.FromSeconds(timeoutS)))
        {
            session.RequestStop(StopReason.User);
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"the session did not stop within 60 s of a stop request (after {timeoutS} s)");
            Assert.Fail($"the session did not finish within {timeoutS} s");
        }

        failure?.Throw();
        Assert.NotNull(report);
        Log.WriteLine(Describe(report));
        return report;
    }

    /// <summary>
    /// The report's failures except the per-thread allocation counts: a test hook that reads frames back runs on the present
    /// thread and allocates there (the allocation tests run without such hooks).
    /// </summary>
    public static List<string> FailuresIgnoringHookAllocations(PlaybackReport report) =>
        [.. report.Failures().Where(f => !f.Contains(" allocated ", StringComparison.Ordinal))];

    /// <summary>Options for a short offscreen run on the virtual clock (deterministic).</summary>
    public static PlaybackOptions Virtual(string path, GeneratorKind generator, int multiplier, double rateHz, double seconds) => new()
    {
        Path = path,
        Generator = generator,
        Multiplier = multiplier,
        Display = DisplayKind.Offscreen,
        Clock = PlaybackClock.Virtual,
        OffscreenRate = Core.Time.FrameRate.FromDouble(rateHz),
        DurationNs = (long)(seconds * 1e9),
        WarmupNs = 200_000_000,
    };

    public static string Describe(PlaybackReport r)
    {
        var s = r.Statistics;
        return $"stopped {r.StopReason}, failure {r.Failure?.Message ?? "none"}; {s.Slots} slots measured ({s.Real} real, {s.Generated} generated, {s.Repeated} repeated, " +
            $"{s.Empty} empty); presents {s.Presents}, shown {r.Shown}, fillers {r.Fillers}, dropped late {r.DroppedLate}, refused {r.DroppedRefused}; misses {s.TotalMisses} " +
            $"(pipeline {s.PipelineMisses}, display {s.EnvironmentMisses}, rate change {s.RateChangeMisses}; untimed {s.UntimedShows}); skipped back-pressure {r.SkippedBackPressure}, presenter backlog " +
            $"{r.SkippedPresenterBacklog}; max lead {r.MaxOutstanding}; writes {r.Copies} copies, {r.GeneratedWrites} generated, {r.Fallbacks} fallbacks, faults " +
            $"{r.GeneratorFaults}; GC {r.MeasuredGc.Gen0Collections}/{r.MeasuredGc.Gen1Collections}/{r.MeasuredGc.Gen2Collections}; alloc pacing " +
            $"{r.Pacing.AllocatedBytes} generator {r.Writer.AllocatedBytes} present {r.Presenter.AllocatedBytes} control {r.Control.AllocatedBytes}; failures: " +
            string.Join("; ", r.Failures());
    }

    private sealed class LogWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => Log.WriteLine(value ?? "");
    }
}

/// <summary>
/// The decoded and converted frames of a clip, by frame id, decoded through the same source the session uses (so they
/// are exactly what the session's ring writes copy): pixels (every row for small frames, every eighth row for large
/// ones), content times (relative to the first frame) and discontinuity marks.
/// </summary>
internal sealed class ReferenceFrames
{
    private readonly List<uint[]> _pixels = [];
    private readonly List<long> _content = [];
    private readonly List<bool> _discontinuity = [];

    private ReferenceFrames(int width, int height, PixelFormat format, int rowStep)
    {
        Width = width;
        Height = height;
        Format = format;
        RowStep = rowStep;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    /// <summary>1: every row is kept; 8: every eighth row (large frames).</summary>
    public int RowStep { get; }

    public int Count => _pixels.Count;

    public static ReferenceFrames Decode(CudaContext cuda, string path, int count, bool loop)
    {
        using var source = new FileFrameSource(cuda, path, new FileSourceOptions { Loop = loop });
        var info = source.StreamInfo;
        var step = (long)info.Width * info.Height > 1_000_000 ? 8 : 1;
        var reference = new ReferenceFrames(info.Width, info.Height, info.OutputFormat, step);
        using var stream = CudaStream.Create(cuda);
        var full = new uint[info.Width * info.Height];
        source.Start();
        long first = long.MinValue;
        var deadline = Stopwatch.GetTimestamp() + (60 * Stopwatch.Frequency);
        while (reference.Count < count)
        {
            if (!source.TryRead(out var frame))
            {
                if (source.IsEndOfStream)
                {
                    break;
                }

                Assert.Null(source.Failure);
                Assert.True(Stopwatch.GetTimestamp() < deadline, "the reference decode stalled");
                Thread.Sleep(0);
                continue;
            }

            Assert.Equal(reference.Count, frame.FrameId);
            stream.Wait(frame.Frame.Ready);
            stream.CopyFrameToHost(frame.Frame, full);
            reference._pixels.Add(reference.Sample(full));
            first = first == long.MinValue ? frame.ContentTimeNs : first;
            reference._content.Add(frame.ContentTimeNs - first);
            reference._discontinuity.Add((frame.Marks & SourceFrameMarks.Discontinuity) != 0);
            source.Release(frame.FrameId);
        }

        return reference;
    }

    public uint[] Pixels(long id) => _pixels[(int)id];

    public long ContentTime(long id) => _content[(int)id];

    public bool Discontinuity(long id) => _discontinuity[(int)id];

    /// <summary>The kept rows of a full frame (tightly packed).</summary>
    public uint[] Sample(ReadOnlySpan<uint> full)
    {
        if (RowStep == 1)
        {
            return full.ToArray();
        }

        var rows = (Height + RowStep - 1) / RowStep;
        var sampled = new uint[rows * Width];
        for (var r = 0; r < rows; r++)
        {
            full.Slice(r * RowStep * Width, Width).CopyTo(sampled.AsSpan(r * Width, Width));
        }

        return sampled;
    }
}

/// <summary>
/// Checks every present of a virtual-clock session as it happens (present thread): reads the offscreen target back and
/// compares it with what the scheduler decided: a real frame bit-exact with the reference, a blended frame bit-exact with
/// the CPU blend of its two frames at its t, a VFG frame between its two frames, a repeat equal to the image before.
/// </summary>
internal sealed class PresentChecker
{
    private readonly ReferenceFrames _reference;
    private readonly GeneratorKind _generator;
    private readonly uint[] _readback;
    private uint[]? _previous;

    public PresentChecker(ReferenceFrames reference, GeneratorKind generator)
    {
        _reference = reference;
        _generator = generator;
        _readback = new uint[reference.Width * reference.Height];
    }

    public PlaybackSession? Session { get; set; }

    public List<Checked> Results { get; } = [];

    public List<string> Problems { get; } = [];

    /// <summary>The image of every real frame presented, by frame id.</summary>
    public Dictionary<long, uint[]> RealImages { get; } = [];

    /// <summary>Frames above this id are not compared with the reference (after a seek the ids no longer match the file).</summary>
    public long ReferenceValidUpTo { get; set; } = long.MaxValue;

    /// <summary>Optional: the true middle frame of a pair (for VFG), by the pair's first frame id.</summary>
    public Func<long, uint[]?>? Truth { get; init; }

    public void OnPresented(PresentedFrame f)
    {
        var target = Session!.OffscreenTarget!;
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_readback.AsSpan());
        _ = target.ReadLatest(bytes);
        var image = _reference.Sample(_readback);
        var result = new Checked(f, true, 0, 0, 0);
        switch (f.Kind)
        {
            case SlotKind.Real:
                RealImages[f.FrameA] = image;
                if (f.FrameA >= _reference.Count || f.FrameA > ReferenceValidUpTo)
                {
                    break;
                }

                if (!image.AsSpan().SequenceEqual(_reference.Pixels(f.FrameA)))
                {
                    result = result with { Ok = false };
                    Problems.Add($"slot {f.SlotIndex}: real frame {f.FrameA} differs from the reference ({Differences(image, _reference.Pixels(f.FrameA))} values)");
                }

                break;
            case SlotKind.Generated:
                {
                    uint[] a, b;
                    if (f.FrameB < _reference.Count && f.FrameB <= ReferenceValidUpTo)
                    {
                        (a, b) = (_reference.Pixels(f.FrameA), _reference.Pixels(f.FrameB));
                    }
                    else
                    {
                        // Its frames are compared once both have been presented as real frames (CheckDeferred).
                        Deferred.Add((f, image));
                        break;
                    }

                    var weight = Cuda.Kernels.ImageKernels.BlendWeight((float)f.Timestep);
                    var blend = Blend(a, b, weight, _reference.Format);
                    if (_generator == GeneratorKind.Blend)
                    {
                        if (!image.AsSpan().SequenceEqual(blend))
                        {
                            result = result with { Ok = false };
                            Problems.Add($"slot {f.SlotIndex}: blend of {f.FrameA} and {f.FrameB} at t {f.Timestep} differs from the CPU blend ({Differences(image, blend)} values)");
                        }
                    }
                    else if (_generator == GeneratorKind.Vfg)
                    {
                        var toA = Error(image, a, a, b);
                        var toB = Error(image, b, a, b);
                        var truth = Truth?.Invoke(f.FrameA);
                        var toTruth = truth is null ? 0 : Error(image, _reference.Sample(truth), a, b);
                        var blendToTruth = truth is null ? 0 : Error(blend, _reference.Sample(truth), a, b);
                        result = result with { ToTruth = toTruth, BlendToTruth = blendToTruth, ToNeighbour = Math.Min(toA, toB) };
                        if (Math.Min(toA, toB) < 0.01)
                        {
                            result = result with { Ok = false };
                            Problems.Add($"slot {f.SlotIndex}: the VFG frame between {f.FrameA} and {f.FrameB} is a copy of a neighbour");
                        }
                    }

                    break;
                }

            case SlotKind.Repeat:
                if (_previous is not null && !image.AsSpan().SequenceEqual(_previous))
                {
                    result = result with { Ok = false };
                    Problems.Add($"slot {f.SlotIndex}: a repeat differs from the image before it");
                }

                break;
        }

        _previous = image;
        Results.Add(result);
    }

    /// <summary>Generated presents whose frames had no reference: checked against the presented real frames afterwards.</summary>
    public List<(PresentedFrame Frame, uint[] Image)> Deferred { get; } = [];

    /// <summary>Checks the deferred generated presents against the blend of their presented real frames; returns how many.</summary>
    public int CheckDeferred()
    {
        var checkedCount = 0;
        foreach (var (f, image) in Deferred)
        {
            if (_generator != GeneratorKind.Blend || !RealImages.TryGetValue(f.FrameA, out var a) || !RealImages.TryGetValue(f.FrameB, out var b))
            {
                continue;
            }

            var blend = Blend(a, b, Cuda.Kernels.ImageKernels.BlendWeight((float)f.Timestep), _reference.Format);
            checkedCount++;
            if (!image.AsSpan().SequenceEqual(blend))
            {
                Problems.Add($"slot {f.SlotIndex}: blend of presented frames {f.FrameA} and {f.FrameB} at t {f.Timestep} differs from the CPU blend ({Differences(image, blend)} values)");
            }
        }

        return checkedCount;
    }

    public static uint[] Blend(uint[] a, uint[] b, uint weight, PixelFormat format)
    {
        var result = new uint[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            var (ar, ag, ab, aa) = Unpack(a[i], format);
            var (br, bg, bb, ba) = Unpack(b[i], format);
            result[i] = Pack(Mix(ar, br, weight), Mix(ag, bg, weight), Mix(ab, bb, weight), Mix(aa, ba, weight), format);
        }

        return result;
    }

    // From the blend kernel's specification (native/kernels/blend.cu): (1 − w)·a + w·b in 16-bit fixed point, rounded.
    private static uint Mix(uint a, uint b, uint weight) => (uint)((((65536L - weight) * a) + ((long)weight * b) + 32768) >> 16);

    private static (uint R, uint G, uint B, uint A) Unpack(uint p, PixelFormat format) => format == PixelFormat.Rgb10A2
        ? (p & 0x3FF, (p >> 10) & 0x3FF, (p >> 20) & 0x3FF, p >> 30)
        : (p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24);

    private static uint Pack(uint r, uint g, uint b, uint a, PixelFormat format) => format == PixelFormat.Rgb10A2
        ? r | (g << 10) | (b << 20) | (a << 30)
        : r | (g << 8) | (b << 16) | (a << 24);

    private static int Differences(uint[] x, uint[] y)
    {
        var n = 0;
        for (var i = 0; i < x.Length; i++)
        {
            n += x[i] != y[i] ? 1 : 0;
        }

        return n;
    }

    /// <summary>Mean absolute channel error of <paramref name="x"/> against <paramref name="y"/> over the pixels where the pair differs.</summary>
    private double Error(uint[] x, uint[] y, uint[] a, uint[] b)
    {
        double sum = 0;
        long n = 0;
        for (var i = 0; i < x.Length; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            var (xr, xg, xb, _) = Unpack(x[i], _reference.Format);
            var (yr, yg, yb, _) = Unpack(y[i], _reference.Format);
            sum += Math.Abs((int)xr - (int)yr) + Math.Abs((int)xg - (int)yg) + Math.Abs((int)xb - (int)yb);
            n += 3;
        }

        return n == 0 ? 0 : sum / n;
    }

    internal readonly record struct Checked(PresentedFrame Frame, bool Ok, double ToTruth, double BlendToTruth, double ToNeighbour);
}
