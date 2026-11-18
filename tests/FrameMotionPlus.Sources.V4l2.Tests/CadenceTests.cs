using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The cadence kernel bit for bit against its CPU reference, and synthetic capture sequences through the real kernel and
/// Core's tracker with the capture thresholds (<see cref="CaptureSourceOptions.CaptureCadence"/>): 30-in-60, 40-in-120,
/// dropped frames, a HUD-only change, noise below the threshold, in PQ (P010) and SDR (NV12, YUYV).
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class CadenceTests
{
    private static readonly FrameRate Signal60 = FrameRate.FromHz(60);
    private static readonly FrameRate Signal120 = FrameRate.FromHz(120);

    public static TheoryData<string, int, int, int> KernelCases => new()
    {
        { "nv12", 67, 37, 0 },        // partial tiles, columns and rows past the last whole cell
        { "nv12", 640, 360, 24 },     // pitch padding that would change the result if read
        { "p010", 1366, 766, 32 },
        { "p010", 3840, 2160, 0 },
        { "yuyv", 322, 182, 8 },      // the Y bytes of the packed pairs
        { "p010", 4, 4, 0 },          // one cell
    };

    [Theory]
    [MemberData(nameof(KernelCases))]
    public void The_kernel_is_bit_exact_against_the_cpu_reference(string format, int width, int height, int padding)
    {
        var context = CaptureHardware.RequireCuda();
        var sampling = format switch
        {
            "p010" => CadenceSampling.P010,
            "yuyv" => CadenceSampling.Yuyv,
            _ => CadenceSampling.Nv12,
        };
        var pitch = (width * sampling.SampleBytes) + padding;
        var random = new Random(width * 31 + height);
        var plane = new byte[pitch * (height + 2)]; // two more rows nothing may read
        random.NextBytes(plane);
        if (sampling.Sixteen)
        {
            for (var i = 0; i < plane.Length; i += 2)
            {
                plane[i] &= 0xc0; // P010 keeps its 10 bits on top; the reference and kernel shift them down
            }
        }

        var grid = new CadenceGrid(width, height, sampling);
        var reference = new uint[grid.Cells];
        for (var i = 0; i < reference.Length; i++)
        {
            reference[i] = (uint)random.Next(16 * (sampling.MaxCode + 1));
        }

        using var stream = CudaStream.Create(context);
        using var luma = CudaDeviceBuffer.Allocate(context, plane.Length);
        using var refGrid = CudaDeviceBuffer.Allocate(context, grid.Bytes);
        using var curGrid = CudaDeviceBuffer.Allocate(context, grid.Bytes);
        using var result = CudaDeviceBuffer.Allocate(context, 16);
        stream.CopyFromHost<byte>(plane, luma.Address);
        stream.CopyFromHost<uint>(reference, refGrid.Address);
        stream.MemsetAsync(result.Address, 0xff, 16); // the kernel's own memset must clear it
        CaptureHardware.Kernels.EnqueueCadenceMetric(stream, luma.Address, pitch, grid, refGrid.Address, curGrid.Address, result.Address);
        var words = new ulong[2];
        var current = new uint[grid.Cells];
        stream.CopyToHost<ulong>(result.Address, words);
        stream.CopyToHost<uint>(curGrid.Address, current);

        var expected = CadenceReference.Run(plane, pitch, width, height, sampling, reference);
        Assert.Equal(expected.Grid, current);
        Assert.Equal(expected.TileQ, words[0]);
        Assert.Equal(expected.MaxCell, words[1]);
        Log.WriteLine($"{format} {width}x{height}: tile {words[0]} ({grid.Normalise(words[0]):0.000000}), max cell {words[1]}");
    }

    [Fact]
    public void The_parameter_block_is_cadence_cus_struct()
    {
        // Five 64-bit fields in the order of struct fmp_cadence_params.
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<CadenceKernelParameters>());
        var names = typeof(CadenceKernelParameters).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(["CellsX", "CellsY", "SampleBytes", "Sixteen", "Shift"], names);
    }

    /// <summary>
    /// A 30 fps game in a 60 Hz signal (every picture captured twice, with the card's noise on every capture): exactly the
    /// first copy of each picture is new, at its own capture time, and the tracker reads 30 fps. PQ (P010) and SDR (NV12).
    /// </summary>
    [Theory]
    [InlineData("p010", 0)]
    [InlineData("p010", 1)]
    [InlineData("nv12", 0)]
    [InlineData("nv12", 1)]
    public void Thirty_in_60_keeps_the_first_copy_of_each_picture(string format, int phase)
    {
        var (rig, scene, pq) = Rig(format, 320, 180);
        using var _ = rig;
        var unique = new List<int>();
        for (var i = 0; i < 240; i++)
        {
            var picture = (i + phase) / 2;
            var r = rig.Capture(scene.Plane(rig.Sampling, picture, noise: 1, noiseSeed: i, noiseDensity: Density(format)), SyntheticLuma.Tick(Signal60, i), pq);
            if (r.IsUnique)
            {
                unique.Add(i);
                Assert.Equal(SyntheticLuma.Tick(Signal60, i), r.ContentTimeNs);
            }
        }

        var expected = Enumerable.Range(0, 240).Where(i => i == 0 || (i + phase) % 2 == 0).ToList();
        Assert.Equal(expected, unique);
        Assert.InRange(rig.Tracker.EstimatedFrameRateHz, 29.99, 30.01);
    }

    /// <summary>40 fps in a 120 Hz signal: a new picture every third capture.</summary>
    [Theory]
    [InlineData("nv12")]
    [InlineData("yuyv")]
    [InlineData("p010")]
    public void Forty_in_120_keeps_every_third_capture(string format)
    {
        var (rig, scene, pq) = Rig(format, 320, 180);
        using var _ = rig;
        var unique = 0;
        for (var i = 0; i < 360; i++)
        {
            var r = rig.Capture(scene.Plane(rig.Sampling, i / 3, noise: 1, noiseSeed: i, noiseDensity: Density(format)), SyntheticLuma.Tick(Signal120, i), pq);
            Assert.True(r.IsUnique == (i % 3 == 0), $"capture {i}: unique {r.IsUnique}");
            unique += r.IsUnique ? 1 : 0;
        }

        Assert.Equal(120, unique);
        Assert.InRange(rig.Tracker.EstimatedFrameRateHz, 39.99, 40.01);
    }

    /// <summary>A 60 fps game that drops frames now and then (the previous picture repeats): those captures are repeats, the
    /// gap is a double interval, and the rate stays near 60.</summary>
    [Theory]
    [InlineData("p010")]
    [InlineData("nv12")]
    public void Dropped_game_frames_are_repeats(string format)
    {
        var (rig, scene, pq) = Rig(format, 320, 180);
        using var _ = rig;
        var random = new Random(5);
        var picture = 0;
        var decisions = new List<(bool New, bool Unique)>();
        for (var i = 0; i < 300; i++)
        {
            var isNew = i == 0 || random.NextDouble() > 0.1; // about one frame in ten dropped by the game
            picture += isNew && i > 0 ? 1 : 0;
            var r = rig.Capture(scene.Plane(rig.Sampling, picture, noise: 1, noiseSeed: i, noiseDensity: Density(format)), SyntheticLuma.Tick(Signal60, i), pq);
            decisions.Add((isNew, r.IsUnique));
        }

        Assert.All(decisions.Select((d, i) => (d, i)), x => Assert.True(x.d.New == x.d.Unique, $"capture {x.i}: new {x.d.New}, judged {x.d.Unique}"));
        Assert.InRange(rig.Tracker.EstimatedFrameRateHz, 50, 60.01);
    }

    /// <summary>A static picture where only an 8x8-pixel HUD element changes, by a few tens of codes: each change counts.</summary>
    [Theory]
    [InlineData("p010", 100)]
    [InlineData("nv12", 40)]
    public void A_small_hud_change_counts(string format, int step)
    {
        var (rig, scene, pq) = Rig(format, 640, 360);
        using var _ = rig;
        var black = 16 << (scene.Bits - 8);
        for (var i = 0; i < 120; i++)
        {
            var hud = black + ((i / 4) % 2 == 0 ? 0 : step) + 1; // changes every fourth capture
            var r = rig.Capture(scene.Plane(rig.Sampling, 0, hud: hud, noise: 1, noiseSeed: i, noiseDensity: Density(format)), SyntheticLuma.Tick(Signal60, i), pq);
            Assert.True(r.IsUnique == (i % 4 == 0), $"capture {i}: unique {r.IsUnique} (HUD {hud})");
        }

        // The metric of one such change, for the record: one tile of 32x32 with an 8x8 patch moved by `step` codes.
        Log.WriteLine($"{format}: HUD step {step} codes over 1/16 of a tile");
    }

    /// <summary>
    /// A static picture with noise at about the card's level for duplicates, and at twice it: nothing after the first capture
    /// is new, and the largest metric stays below the low threshold. (Noisier repeats fall between the thresholds and are
    /// left to the cadence: the next test.)
    /// </summary>
    [Theory]
    [InlineData("p010", 1)]
    [InlineData("p010", 2)]
    [InlineData("nv12", 1)]
    [InlineData("nv12", 2)]
    [InlineData("yuyv", 1)]
    public void Noise_below_the_threshold_is_never_new(string format, int scale)
    {
        var (rig, scene, pq) = Rig(format, 640, 360);
        using var _ = rig;
        const int noise = 1;
        var density = Density(format) * scale; // the card's level, and twice it
        var first = scene.Plane(rig.Sampling, 0, noise: noise, noiseSeed: 0, noiseDensity: density);
        Assert.True(rig.Capture(first, SyntheticLuma.Tick(Signal60, 0), pq).IsUnique);
        var metrics = new List<double>();
        for (var i = 1; i < 120; i++)
        {
            var plane = scene.Plane(rig.Sampling, 0, noise: noise, noiseSeed: i, noiseDensity: density);
            metrics.Add(rig.Grid.Normalise(rig.Measure(plane).TileQ));
            var r = rig.Capture(plane, SyntheticLuma.Tick(Signal60, i), pq);
            Assert.True(!r.IsUnique, $"capture {i}: new (metric {metrics[^1]:0.000000})");
        }

        var low = pq ? CaptureSourceOptions.CaptureCadence.Pq.Low : CaptureSourceOptions.CaptureCadence.Sdr.Low;
        Log.WriteLine($"{format} ±{noise} on {density:P0} of the samples: metric p50 {metrics.Order().ElementAt(metrics.Count / 2):0.000000}, max {metrics.Max():0.000000}, low threshold {low}");
        Assert.True(metrics.Max() < low, $"noise metric {metrics.Max()} reached the low threshold {low}");
    }

    /// <summary>A 30-in-60 source whose repeats are noisy enough to fall between the thresholds: once the cadence is
    /// learned, the cadence keeps them repeats (they are not due) and the rate stays 30.</summary>
    [Theory]
    [InlineData("nv12", 6)]
    [InlineData("p010", 8)]
    public void In_band_repeats_are_left_to_the_cadence(string format, int noise)
    {
        var (rig, scene, pq) = Rig(format, 320, 180);
        using var _ = rig;
        var inBand = 0;
        var wrong = 0;
        for (var i = 0; i < 400; i++)
        {
            var quiet = i < 100; // learn the cadence on quiet repeats first
            var plane = scene.Plane(rig.Sampling, i / 2, noise: quiet ? 1 : noise, noiseSeed: i, noiseDensity: Density(format));
            var r = rig.Capture(plane, SyntheticLuma.Tick(Signal60, i), pq);
            inBand += r.Ambiguous ? 1 : 0;
            wrong += !quiet && r.IsUnique != (i % 2 == 0) ? 1 : 0;
        }

        Log.WriteLine($"{format} ±{noise}: {inBand} in-band decisions");
        Assert.True(inBand > 20, $"only {inBand} in-band decisions: the noise did not reach the band");
        Assert.Equal(0, wrong);
        Assert.InRange(rig.Tracker.EstimatedFrameRateHz, 29.99, 30.01);
    }

    /// <summary>
    /// Noise at about the card's level for true duplicates (measured: P010 p50 0.00014, at most 0.00021 at 1440p60; NV12 p50
    /// 0.00048, at most 0.00084): ±1 code on a share of the samples (8-bit codes are coarser in full-scale terms).
    /// </summary>
    private static double Density(string format) => format == "p010" ? 0.15 : 0.25;

    private static (CadenceRig Rig, SyntheticLuma Scene, bool Pq) Rig(string format, int width, int height)
    {
        var context = CaptureHardware.RequireCuda();
        var sampling = format switch
        {
            "p010" => CadenceSampling.P010,
            "yuyv" => CadenceSampling.Yuyv,
            _ => CadenceSampling.Nv12,
        };
        var pq = format == "p010";
        return (new CadenceRig(context, width, height, sampling), new SyntheticLuma(width, height, pq ? 10 : 8), pq);
    }
}
