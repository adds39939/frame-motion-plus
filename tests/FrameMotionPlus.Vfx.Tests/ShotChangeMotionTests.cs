using System.Globalization;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// Why fast motion lost its generated frames (the owner, playing a 30 fps game at 2x and 3x: "really fast scenes/action,
/// and objects up close, especially foliage, seem to disable the frame generation until they go away"): VFG's automatic
/// shot-change detection takes large frame-to-frame change for a cut and then outputs a real frame unchanged instead of
/// an interpolation (docs/benchmarks/phase3-vfg.md, "5. Shot changes"). Pairs of 4K frames with growing motion: a detailed
/// texture panned by 8 to 384 px a frame, and "foliage" (detail of 1 to 6 px moving at different speeds and directions
/// across the frame); for each, how many generated frames are bit-identical copies of an input, with detection on and off.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class ShotChangeMotionTests
{
    private const int Width = 3840;
    private const int Height = 2160;
    private const int FramesPerCase = 5;           // 4 pairs: the first primes the generator, the other 3 are counted
    private static readonly float[] Timesteps = [0.25f, 0.5f, 0.75f];
    private static readonly int[] PanSpeeds = [8, 32, 96, 192, 384];
    private static readonly int[] FoliageSpeeds = [8, 32, 48, 64, 96];
    private const int FoliageMargin = 400;          // > (FramesPerCase - 1) x the fastest foliage speed

    [Fact]
    public void Automatic_detection_takes_fast_motion_for_a_cut_and_off_it_never_copies()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        var cases = new List<(string Name, int Speed, Action<int, uint[]> Draw)>();
        var pan = Texture(Width + ((FramesPerCase - 1) * PanSpeeds[^1]), Height, foliage: false);
        var panWidth = Width + ((FramesPerCase - 1) * PanSpeeds[^1]);
        foreach (var speed in PanSpeeds)
        {
            cases.Add(($"pan {speed} px", speed, (k, pixels) => Crop(pan, panWidth, k * speed, 0, pixels)));
        }

        var leavesWidth = Width + (2 * FoliageMargin);
        var leaves = Texture(leavesWidth, Height + (2 * FoliageMargin), foliage: true);
        var (dx, dy) = DisplacementField();
        foreach (var speed in FoliageSpeeds)
        {
            cases.Add(($"foliage {speed} px", speed, (k, pixels) => Displace(leaves, leavesWidth, dx, dy, k * speed, pixels)));
        }

        var frames = new CudaDeviceBuffer[FramesPerCase];
        using var output = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        using var on = env.Generator(Width, Height, format, FrameGenQuality.Medium, new VfgOptions { AutomaticShotChangeDetection = true, MaxLoadedInstances = 1 });
        using var off = env.Generator(Width, Height, format, FrameGenQuality.Medium, new VfgOptions { AutomaticShotChangeDetection = false, MaxLoadedInstances = 1 });
        var results = new List<(string Name, int Speed, int OnCopies, string OnWhich, int OffCopies, double OffMae)>();
        try
        {
            var host = new uint[Width * Height];
            var inputs = new uint[FramesPerCase][];
            for (var i = 0; i < FramesPerCase; i++)
            {
                frames[i] = env.Image(Width, Height, format);
                inputs[i] = new uint[Width * Height];
            }

            using var stream = CudaStream.Create(env.Context);
            foreach (var (name, speed, draw) in cases)
            {
                for (var k = 0; k < FramesPerCase; k++)
                {
                    draw(k, inputs[k]);
                    stream.CopyFrameFromHost(inputs[k], frames[k].AsFrame());
                }

                stream.Synchronize();
                var (onCopies, onWhich, _) = Count(on, env, frames, inputs, output, done, host, format);
                var (offCopies, _, offMae) = Count(off, env, frames, inputs, output, done, host, format);
                results.Add((name, speed, onCopies, onWhich, offCopies, offMae));
                Log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"4K Medium, {name} a frame: detection on {onCopies} of {3 * Timesteps.Length} generated frames were copies ({onWhich}); " +
                    $"off {offCopies} (mean distance to the nearer input {offMae:0.0} codes)"));
            }
        }
        finally
        {
            foreach (var f in frames)
            {
                f?.Dispose();
            }
        }

        // Detection off never copies: every generated frame is an interpolation, however fast the motion.
        Assert.All(results, r => Assert.Equal(0, r.OffCopies));

        // Detection on (measured 2026-11-20, RTX 5090, driver 620.70): a uniform pan is never taken for a cut, however fast (up
        // to 384 px a frame, a tenth of the width); slow foliage is interpolated; fast foliage, detail moving at different
        // speeds and directions, is taken for a cut at every timestep (previous, previous, current: what the owner saw).
        // Only "it copies in the fastest foliage" is asserted: the exact counts (none for pans and slow foliage, 2, 6 and 9
        // of 9 at 48, 64 and 96 px) are the SDK's and the driver's, logged above, and may move with either.
        Assert.True(results.Single(r => r.Name == $"foliage {FoliageSpeeds[^1]} px").OnCopies > 0, "detection on copied nothing in the fastest foliage");
    }

    /// <summary>
    /// Runs the case's pairs (k, k+1) at each timestep; the first pair primes the generator and is not counted. Returns how
    /// many outputs were bit-identical to an input, which ones (p: the previous frame, c: the current one, -: neither, in
    /// call order), and the mean distance of the non-copies to the nearer input (over the middle eighth of the rows).
    /// </summary>
    private static (int Copies, string Which, double Mae) Count(VfgGenerator generator, VfxEnvironment env, CudaDeviceBuffer[] frames, uint[][] inputs,
        CudaDeviceBuffer output, CudaEvent done, uint[] host, PixelFormat format)
    {
        var copies = 0;
        var which = new System.Text.StringBuilder();
        double mae = 0;
        var interpolations = 0;
        using var stream = CudaStream.Create(env.Context);
        for (var pair = 0; pair < FramesPerCase - 1; pair++)
        {
            foreach (var t in Timesteps)
            {
                // Content times only need to increase from pair to pair (the generator checks it).
                var prev = frames[pair].AsFrame(contentTimeNs: Next());
                var curr = frames[pair + 1].AsFrame(contentTimeNs: Next());
                generator.Enqueue(prev, curr, t, output.AsFrame(), done.Signal);
                done.Synchronize();
                stream.CopyFrameToHost(output.AsFrame(), host);
                if (pair == 0)
                {
                    continue;
                }

                var isPrev = host.AsSpan().SequenceEqual(inputs[pair]);
                var isCurr = host.AsSpan().SequenceEqual(inputs[pair + 1]);
                if (isPrev || isCurr)
                {
                    copies++;
                    _ = which.Append(isPrev ? 'p' : 'c');
                }
                else
                {
                    _ = which.Append('-');
                    // A band of an eighth of the rows is plenty for a mean (the whole frame is compared bit for bit above).
                    var toPrev = Images.Compare(host, inputs[pair], Width, Height * 7 / 16, Height * 9 / 16, format);
                    var toCurr = Images.Compare(host, inputs[pair + 1], Width, Height * 7 / 16, Height * 9 / 16, format);
                    mae += Math.Min(toPrev.Mae, toCurr.Mae);
                    interpolations++;
                }
            }

            _ = which.Append(pair < FramesPerCase - 2 && pair > 0 ? " " : "");
        }

        return (copies, which.ToString(), interpolations == 0 ? 0 : mae / interpolations);
    }

    private static long _contentNs;

    private static long Next() => Interlocked.Add(ref _contentNs, 1_000_000);

    /// <summary>
    /// Value noise in several octaves: a detailed texture (features from 64 px down to 3 px, colour varying slowly), or
    /// foliage (green, features from 32 px down to 1 to 2 px, high contrast).
    /// </summary>
    private static uint[] Texture(int width, int height, bool foliage)
    {
        var pixels = new uint[width * height];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                double r, g, b;
                if (foliage)
                {
                    var l = (0.30 * Noise(x, y, 32, 11)) + (0.40 * Noise(x, y, 6, 12)) + (0.30 * Noise(x, y, 2, 13));
                    var leaf = Math.Clamp((l - 0.25) * 2.0, 0, 1);
                    r = 0.10 + (0.35 * leaf * Noise(x, y, 48, 14));
                    g = 0.12 + (0.80 * leaf);
                    b = 0.05 + (0.20 * leaf);
                }
                else
                {
                    var l = (0.35 * Noise(x, y, 64, 1)) + (0.30 * Noise(x, y, 24, 2)) + (0.20 * Noise(x, y, 8, 3)) + (0.15 * Noise(x, y, 3, 4));
                    r = l * (0.6 + (0.6 * Noise(x, y, 160, 5)));
                    g = l;
                    b = l * (0.5 + (0.7 * Noise(x, y, 200, 6)));
                }

                pixels[(y * width) + x] = Images.Pack(Code(r), Code(g), Code(b), PixelFormat.Rgba8);
            }
        });
        return pixels;
    }

    private static uint Code(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>Smooth value noise in [0, 1] on a lattice of <paramref name="cell"/> px (x, y non-negative).</summary>
    private static double Noise(int x, int y, int cell, uint seed)
    {
        var (cx, cy) = (x / cell, y / cell);
        var fx = Smooth((x % cell) / (double)cell);
        var fy = Smooth((y % cell) / (double)cell);
        var a = Lattice(cx, cy, seed);
        var b = Lattice(cx + 1, cy, seed);
        var c = Lattice(cx, cy + 1, seed);
        var d = Lattice(cx + 1, cy + 1, seed);
        return a + ((b - a) * fx) + ((c - a) * fy) + ((a - b - c + d) * fx * fy);
    }

    private static double Smooth(double f) => f * f * (3 - (2 * f));

    private static double Lattice(int x, int y, uint seed)
    {
        var h = ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u) ^ (seed * 0xCB1AB31Fu);
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        h ^= h >> 15;
        return (h & 0xFFFF) / 65535.0;
    }

    private static void Crop(uint[] texture, int textureWidth, int offsetX, int offsetY, uint[] pixels)
    {
        for (var y = 0; y < Height; y++)
        {
            Array.Copy(texture, ((y + offsetY) * textureWidth) + offsetX, pixels, y * Width, Width);
        }
    }

    /// <summary>
    /// Per pixel, how far (in units of the case's speed) the foliage there moves per frame: 0.2 to 1.0 to the right and
    /// up to ±0.35 up or down, varying over 100 to 250 px, so neighbouring patches of leaves move at different speeds.
    /// </summary>
    private static (float[] Dx, float[] Dy) DisplacementField()
    {
        var dx = new float[Width * Height];
        var dy = new float[Width * Height];
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                dx[(y * Width) + x] = (float)(0.6 + (0.4 * Math.Sin(2 * Math.PI * y / 173.0) * Math.Cos(2 * Math.PI * x / 241.0)));
                dy[(y * Width) + x] = (float)(0.35 * Math.Sin((2 * Math.PI * x / 131.0) + (2 * Math.PI * y / 89.0)));
            }
        });
        return (dx, dy);
    }

    /// <summary>Frame k of a foliage case: each pixel taken from the texture <paramref name="shift"/> × its displacement away.</summary>
    private static void Displace(uint[] texture, int textureWidth, float[] dx, float[] dy, int shift, uint[] pixels)
    {
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width) + x;
                var sx = x + FoliageMargin + (int)MathF.Round(shift * dx[i]);
                var sy = y + FoliageMargin + (int)MathF.Round(shift * dy[i]);
                pixels[i] = texture[(sy * textureWidth) + sx];
            }
        });
    }
}
