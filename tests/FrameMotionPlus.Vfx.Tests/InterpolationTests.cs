using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// VFG output against the true intermediate frame of known motion. VFG is not bit-exact against anything, so the
/// checks are relative: the generated frame must be closer to the truth than the linear blend of its neighbours is,
/// and must differ from both neighbours (it is not a copy of either, so shot-change handling did not bypass the model).
/// <para>
/// Two kinds of motion with an exact truth: the test pattern's box (pattern frames 2k and 2k + 2 as the pair, frame
/// 2k + 1 as the truth; only the box band is compared, the counter band changes by design), and a pan across a
/// detailed texture by whole pixels (the truth is the texture shifted by half the motion).
/// </para>
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class InterpolationTests
{
    private const int Width = 1280;
    private const int Height = 720;

    public static TheoryData<PixelFormat> Formats => [PixelFormat.Rgba8, PixelFormat.Rgb10A2];

    [Theory]
    [MemberData(nameof(Formats))]
    public void The_moving_box_lands_halfway_closer_than_the_blend_does(PixelFormat format)
    {
        var env = VfxHardware.Require();
        const uint k = 40;
        using var prev = env.Pattern(Width, Height, format, 2 * k);
        using var curr = env.Pattern(Width, Height, format, (2 * k) + 2);
        using var truth = env.Pattern(Width, Height, format, (2 * k) + 1);
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);

        generator.Enqueue(prev.AsFrame(), curr.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();

        var (y0, y1) = BoxBand();
        var o = env.Read(output);
        var p = env.Read(prev);
        var c = env.Read(curr);
        var t = env.Read(truth);
        var blend = Images.Blend(p, c, 0.5, format);
        var vfg = Images.Compare(o, t, Width, y0, y1, format);
        var linear = Images.Compare(blend, t, Width, y0, y1, format);
        var fromPrev = Images.Compare(o, p, Width, y0, y1, format);
        var fromCurr = Images.Compare(o, c, Width, y0, y1, format);
        var bars = Images.Compare(o, t, Width, 0, Height * 7 / 12, format);
        Log.WriteLine($"{format} box band: VFG vs truth {vfg}; blend vs truth {linear}; VFG vs prev {fromPrev}; VFG vs curr {fromCurr}; static bars {bars}");

        Assert.True(vfg.Mae < linear.Mae / 2, $"VFG {vfg} is not clearly better than the blend {linear}");
        Assert.True(vfg.Psnr > linear.Psnr + 3, $"VFG {vfg} vs blend {linear}");
        Assert.True(fromPrev.Mae > vfg.Mae * 2 && fromCurr.Mae > vfg.Mae * 2, $"the output is too close to a neighbour: prev {fromPrev}, curr {fromCurr}");

        // The box sits halfway: its centre of mass is within a pixel of the truth's.
        var threshold = format == PixelFormat.Rgb10A2 ? 540u : 150u;
        var x = Images.BrightCentroidX(o, Width, y0, y1, format, threshold);
        var xt = Images.BrightCentroidX(t, Width, y0, y1, format, threshold);
        Log.WriteLine($"box centre: VFG {x:0.00}, truth {xt:0.00}");
        Assert.InRange(x, xt - 1, xt + 1);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_textured_pan_is_reconstructed_closer_than_the_blend(PixelFormat format)
    {
        var env = VfxHardware.Require();
        const int motion = 12; // px between prev and curr
        using var prev = env.Upload(Width, Height, format, Images.Texture(Width, Height, 0, format));
        using var curr = env.Upload(Width, Height, format, Images.Texture(Width, Height, motion, format));
        var truth = Images.Texture(Width, Height, motion / 2, format);
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);

        generator.Enqueue(prev.AsFrame(), curr.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();

        var o = env.Read(output);
        var blend = Images.Blend(env.Read(prev), env.Read(curr), 0.5, format);

        // Away from the right edge, where content enters the frame and no frame shows the truth.
        var vfg = CompareInterior(o, truth, format);
        var linear = CompareInterior(blend, truth, format);
        Log.WriteLine($"{format} pan of {motion} px: VFG vs truth {vfg}; blend vs truth {linear}");
        Assert.True(vfg.Psnr > linear.Psnr + 3, $"VFG {vfg} vs blend {linear}");
    }

    /// <summary>
    /// The output moves from prev towards curr as t grows: the box's centre of mass never moves backwards over t = 1/8
    /// .. 7/8 between pattern frames 8k and 8k + 8 (40 px of motion at this size), and for t = 1/4 .. 3/4 it is within
    /// 2 px of the true intermediate. Nearer the ends VFG does not follow t this closely for motion this large (measured
    /// in <see cref="CharacterizationTests"/>), so there the test only logs.
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void The_output_moves_monotonically_from_prev_to_curr_as_t_grows(PixelFormat format)
    {
        var env = VfxHardware.Require();
        const uint k = 4;
        using var prev = env.Pattern(Width, Height, format, 8 * k);
        using var curr = env.Pattern(Width, Height, format, (8 * k) + 8);
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        var (y0, y1) = BoxBand();
        var threshold = format == PixelFormat.Rgb10A2 ? 540u : 150u;

        var centres = new List<double>();
        var truths = new List<double>();
        for (var i = 1; i <= 7; i++)
        {
            generator.Enqueue(prev.AsFrame(), curr.AsFrame(), i / 8f, output.AsFrame(), done.Signal);
            done.Synchronize();
            using var truth = env.Pattern(Width, Height, format, (8 * k) + (uint)i);
            var o = env.Read(output);
            var t = env.Read(truth);
            centres.Add(Images.BrightCentroidX(o, Width, y0, y1, format, threshold));
            truths.Add(Images.BrightCentroidX(t, Width, y0, y1, format, threshold));
        }

        Log.WriteLine($"{format} centres for t = 1/8..7/8: {string.Join(", ", centres.Select(c => c.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
        Log.WriteLine($"truth centres: {string.Join(", ", truths.Select(c => c.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
        for (var i = 1; i < centres.Count; i++)
        {
            Assert.True(centres[i] >= centres[i - 1] - 0.25, $"moved backwards at t = {i + 1}/8: {centres[i - 1]:0.00} then {centres[i]:0.00}");
        }

        Assert.True(centres[^1] - centres[0] > 15, "the output hardly moves with t");
        for (var i = 1; i <= 5; i++)
        {
            Assert.InRange(centres[i], truths[i] - 2, truths[i] + 2);
        }
    }

    private static (int Y0, int Y1) BoxBand() => (Height * 7 / 12, Height * 9 / 12);

    private static ImageError CompareInterior(uint[] a, uint[] b, PixelFormat format)
    {
        // Columns [16, Width - 32) of every row: copy them out so Compare sees a narrower image.
        const int x0 = 16;
        var w = Width - 48;
        var ca = new uint[w * Height];
        var cb = new uint[w * Height];
        for (var y = 0; y < Height; y++)
        {
            Array.Copy(a, (y * Width) + x0, ca, y * w, w);
            Array.Copy(b, (y * Width) + x0, cb, y * w, w);
        }

        return Images.Compare(ca, cb, w, 0, Height, format);
    }
}
