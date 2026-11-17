using System.Globalization;
using System.Text;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.App.Vfg;

/// <summary>VFG against the truth for one scene in one quality mode (code values: 0-255 SDR, 0-1023 PQ).</summary>
internal sealed record QualityResult
{
    public required QualityScene Scene { get; init; }

    public required FrameGenQuality Mode { get; init; }

    public double PsnrVfg { get; init; }

    public double PsnrBlend { get; init; }

    /// <summary>The nearest real frame (prev) against the truth: what showing a repeat would give.</summary>
    public double PsnrRepeat { get; init; }

    public double MaeVfg { get; init; }

    public double MaeBlend { get; init; }

    public uint MaxVfg { get; init; }

    /// <summary>99.9th percentile of the absolute channel error of VFG.</summary>
    public uint P999Vfg { get; init; }

    /// <summary>PQ: mean absolute code error where the truth is at least 400 nits in some channel (highlights).</summary>
    public double HighlightMaeVfg { get; init; }

    public double HighlightMaeBlend { get; init; }

    public long HighlightPixels { get; init; }

    /// <summary>PQ: mean absolute code error where the truth is at most 1 nit in every channel (shadows).</summary>
    public double DarkMaeVfg { get; init; }

    public double DarkMaeBlend { get; init; }

    /// <summary>PQ: the share of shadow pixels with some channel off by more than 4 codes (VFG, and the blend).</summary>
    public double DarkOff4Vfg { get; init; }

    public double DarkOff4Blend { get; init; }

    public long DarkPixels { get; init; }

    /// <summary>Banding window: distinct green codes in the truth and in VFG's output.</summary>
    public int DistinctTruth { get; init; }

    public int DistinctVfg { get; init; }

    /// <summary>Banding window: share of green codes that are multiples of 4 (0.25 for full 10-bit precision, 1 for 8-bit).</summary>
    public double Multiple4Truth { get; init; }

    public double Multiple4Vfg { get; init; }

    public long GpuNs { get; init; }
}

/// <summary>
/// <c>fmp bench vfg-quality</c>: runs VFG at t = 0.5 on SDR and PQ scenes with known motion in each quality mode,
/// scores the output against the true middle frame (and the blend and repeat baselines), and writes before / truth /
/// after / blend / VFG / error images as 16-bit PNGs (PQ ones tagged BT.2020 + PQ with a cICP chunk) and an index for
/// inspection by eye.
/// </summary>
internal sealed class VfgQuality
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    private readonly CudaContext _cuda;
    private readonly int _width;
    private readonly int _height;
    private readonly IReadOnlyList<FrameGenQuality> _modes;
    private readonly string? _outDirectory;

    public VfgQuality(CudaContext cuda, int width, int height, IReadOnlyList<FrameGenQuality> modes, string? outDirectory)
    {
        _cuda = cuda;
        _width = width;
        _height = height;
        _modes = modes;
        _outDirectory = outDirectory;
    }

    public List<QualityResult> Run(IReadOnlyList<QualityScene> scenes, TextWriter progress)
    {
        var results = new List<QualityResult>();
        using var generator = new VfgGenerator(_cuda, new VfgOptions { MaxLoadedInstances = 2 });
        using var prev = CudaDeviceBuffer.AllocateImage(_cuda, _width, _height, PixelFormat.Rgba8);
        using var curr = CudaDeviceBuffer.AllocateImage(_cuda, _width, _height, PixelFormat.Rgba8);
        using var output = CudaDeviceBuffer.AllocateImage(_cuda, _width, _height, PixelFormat.Rgba8);
        using var stream = CudaStream.Create(_cuda);
        using var done = CudaEvent.Create(_cuda);
        if (_outDirectory is not null)
        {
            foreach (var scene in scenes)
            {
                var dir = Directory.CreateDirectory(Path.Combine(_outDirectory, scene.Name)).FullName;
                Save(Path.Combine(dir, "a-prev.png"), scene, scene.Prev);
                Save(Path.Combine(dir, "b-truth.png"), scene, scene.Truth);
                Save(Path.Combine(dir, "c-curr.png"), scene, scene.Curr);
                Save(Path.Combine(dir, "d-blend.png"), scene, Blend(scene));
            }
        }

        foreach (var mode in _modes)
        {
            foreach (var scene in scenes)
            {
                // The buffers are 32-bit whatever the format; only the frame descriptions differ.
                var format = scene.Format;
                generator.Configure(new FrameGenConfig { Width = _width, Height = _height, Format = format, Quality = mode });
                var fp = prev.AsFrame(_width, _height, format, prev.PitchBytes);
                var fc = curr.AsFrame(_width, _height, format, curr.PitchBytes);
                var fo = output.AsFrame(_width, _height, format, output.PitchBytes);
                stream.CopyFrameFromHost(scene.Prev, fp);
                stream.CopyFrameFromHost(scene.Curr, fc);
                stream.Synchronize();
                while (generator.TryReadGpuTime(out _))
                {
                }

                generator.Enqueue(fp, fc, 0.5f, fo, done.Signal);
                done.Synchronize();
                _ = generator.TryReadGpuTime(out var gpuNs);
                var pixels = new uint[_width * _height];
                stream.CopyFrameToHost(fo, pixels);
                var result = Score(scene, mode, pixels, gpuNs);
                results.Add(result);
                progress.WriteLine(Row(result));
                progress.Flush();
                if (_outDirectory is not null)
                {
                    var dir = Path.Combine(_outDirectory, scene.Name);
                    var name = VfgReport.ModeName(mode);
                    Save(Path.Combine(dir, $"e-vfg-{name}.png"), scene, pixels);
                    SaveError(Path.Combine(dir, $"f-error-{name}.png"), scene, pixels);
                }
            }
        }

        return results;
    }

    public static string Header =>
        "  scene              mode     PSNR dB: VFG  blend repeat   MAE VFG blend   max  p99.9   highlights VFG/blend   shadows VFG/blend (>4 codes)     banding window";

    public static string Row(QualityResult r)
    {
        var s = r.Scene;
        var highlights = r.HighlightPixels > 0 ? string.Create(Ic, $"{r.HighlightMaeVfg,6:0.00}/{r.HighlightMaeBlend,-6:0.00} ({r.HighlightPixels} px)") : "-";
        var shadows = r.DarkPixels > 0 ? string.Create(Ic, $"{r.DarkMaeVfg,5:0.00}/{r.DarkMaeBlend,-5:0.00} >4: {r.DarkOff4Vfg * 100:0.0}%/{r.DarkOff4Blend * 100:0.0}%") : "-";
        var banding = s.BandingWindow is null
            ? "-"
            : string.Create(Ic, $"{r.DistinctVfg}/{r.DistinctTruth} codes, x4 share {r.Multiple4Vfg:0.00}/{r.Multiple4Truth:0.00}");
        return string.Create(Ic,
            $"  {s.Name,-18} {VfgReport.ModeName(r.Mode),-7} {r.PsnrVfg,10:0.0} {r.PsnrBlend,6:0.0} {r.PsnrRepeat,6:0.0} {r.MaeVfg,9:0.00} {r.MaeBlend,5:0.00} {r.MaxVfg,5} {r.P999Vfg,6}   {highlights,-22} {shadows,-32} {banding}");
    }

    public static void WriteIndex(string path, IReadOnlyList<QualityScene> scenes, IReadOnlyList<QualityResult> results, int width, int height, string header)
    {
        var b = new StringBuilder();
        b.AppendLine("# VFG quality check: SDR and PQ frames for inspection");
        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"Written by `fmp bench vfg-quality` ({header}). Every scene is one pair of frames with known motion at {width}x{height}; VFG generated the middle frame (t = 0.5) in each quality mode.");
        b.AppendLine();
        b.AppendLine("Per scene directory:");
        b.AppendLine();
        b.AppendLine("| File | Shows |");
        b.AppendLine("| --- | --- |");
        b.AppendLine("| `a-prev.png` | the previous real frame (t = 0) |");
        b.AppendLine("| `b-truth.png` | the true middle frame (t = 0.5): what a perfect interpolator would output |");
        b.AppendLine("| `c-curr.png` | the current real frame (t = 1) |");
        b.AppendLine("| `d-blend.png` | the linear blend of the two (the baseline VFG must beat): ghosting where things move |");
        b.AppendLine("| `e-vfg-<mode>.png` | VFG's output in that quality mode (low, medium, high) |");
        b.AppendLine("| `f-error-<mode>.png` | |VFG − truth| per pixel (largest channel), amplified: × 8 for SDR codes, × 2 for PQ codes; black = exact |");
        b.AppendLine();
        b.AppendLine("Images are 16-bit RGB PNGs. SDR scenes carry an sRGB chunk. **PQ scenes carry a cICP chunk (BT.2020 primaries, SMPTE ST 2084, full range)**: an HDR-aware viewer on an HDR display (e.g. Chromium-based browsers with HDR on) shows them as HDR10; a viewer that ignores cICP shows the raw PQ code values, which look flat and dark but still show banding, smear and ghosting. The 10-bit codes are stored as `code << 6 | code >> 4`.");
        b.AppendLine();
        b.AppendLine("## What to look at first");
        b.AppendLine();
        b.AppendLine("- **`pq-specular/e-vfg-high.png` against `pq-specular/b-truth.png` and `e-vfg-medium.png`**: High mode disturbs the dark background widely, not only around the highlights as Medium does (see the shadow columns below); compare `f-error-high.png` with `f-error-medium.png`. High is not simply better than Medium.");
        b.AppendLine("- `pq-specular/e-vfg-medium.png`: small highlights smeared or doubled, faint striped halos around them.");
        b.AppendLine("- `pq-dark-gradient/` and `pq-ramp-pan/`: banding (none was found by the numbers: the code statistics match the truth).");
        b.AppendLine("- `pq-photo/`: the HDR-graded picture's highlights.");
        b.AppendLine();
        b.AppendLine("## Scenes");
        b.AppendLine();
        foreach (var s in scenes)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"- **`{s.Name}/`** ({(s.IsPq ? "PQ RGB10A2" : "SDR RGBA8")}): {s.Description}");
        }

        b.AppendLine();
        b.AppendLine("## Numbers");
        b.AppendLine();
        b.AppendLine("Errors in code values (0-255 SDR, 0-1023 PQ) against the truth, away from the right edge where new content enters (no truth there); the pattern scenes over their moving box band only. PSNR peak = the format's maximum code. *Repeat* is showing the previous real frame. *Highlights*: pixels whose truth is at least 400 nits in some channel; *shadows*: at most 1 nit in every channel (PQ only), with the share of them off by more than 4 codes in some channel. *Banding window*: distinct green codes in a window of smooth gradient (VFG / truth), and the share of them that are multiples of 4 (about 0.25 with full 10-bit precision, 1.0 if the output had been quantised to 8 bits).");
        b.AppendLine();
        b.AppendLine("| Scene | Mode | PSNR VFG | PSNR blend | PSNR repeat | MAE VFG | MAE blend | max | p99.9 | highlight MAE VFG / blend | shadow MAE VFG / blend | shadow pixels > 4 codes VFG / blend | banding window | d (ms) |");
        b.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var r in results)
        {
            var hl = r.HighlightPixels > 0 ? string.Create(Ic, $"{r.HighlightMaeVfg:0.00} / {r.HighlightMaeBlend:0.00}") : "–";
            var dk = r.DarkPixels > 0 ? string.Create(Ic, $"{r.DarkMaeVfg:0.00} / {r.DarkMaeBlend:0.00}") : "–";
            var dk4 = r.DarkPixels > 0 ? string.Create(Ic, $"{r.DarkOff4Vfg * 100:0.0} % / {r.DarkOff4Blend * 100:0.0} %") : "–";
            var bw = r.Scene.BandingWindow is null ? "–" : string.Create(Ic, $"{r.DistinctVfg} / {r.DistinctTruth} codes, ×4 share {r.Multiple4Vfg:0.00} / {r.Multiple4Truth:0.00}");
            b.AppendLine(string.Create(Ic,
                $"| {r.Scene.Name} | {VfgReport.ModeName(r.Mode)} | {r.PsnrVfg:0.0} | {r.PsnrBlend:0.0} | {r.PsnrRepeat:0.0} | {r.MaeVfg:0.00} | {r.MaeBlend:0.00} | {r.MaxVfg} | {r.P999Vfg} | {hl} | {dk} | {dk4} | {bw} | {r.GpuNs / 1e6:0.00} |"));
        }

        File.WriteAllText(path, b.ToString());
    }

    private QualityResult Score(QualityScene scene, FrameGenQuality mode, uint[] output, long gpuNs)
    {
        var format = scene.Format;
        var (y0, y1) = scene.Rows ?? (0, _height);
        var xEnd = _width - scene.EdgeColumns;
        var blend = Blend(scene);
        double sqV = 0, sqB = 0, sqR = 0, absV = 0, absB = 0;
        double hlV = 0, hlB = 0, dkV = 0, dkB = 0;
        long n = 0, hl = 0, dk = 0, dkOffV = 0, dkOffB = 0;
        uint max = 0;
        var histogram = new long[1024];
        var highlightCode = QualityScenes.Code10(QualityScenes.PqEncode(400));
        var darkCode = QualityScenes.Code10(QualityScenes.PqEncode(1));
        for (var y = y0; y < y1; y++)
        {
            for (var x = 0; x < xEnd; x++)
            {
                var i = (y * _width) + x;
                var (tr, tg, tb) = PixelCodes.Unpack(scene.Truth[i], format);
                var (vr, vg, vb) = PixelCodes.Unpack(output[i], format);
                var (br, bg, bb) = PixelCodes.Unpack(blend[i], format);
                var (pr, pg, pb) = PixelCodes.Unpack(scene.Prev[i], format);
                var er = Math.Abs((int)vr - (int)tr);
                var eg = Math.Abs((int)vg - (int)tg);
                var eb = Math.Abs((int)vb - (int)tb);
                max = Math.Max(max, (uint)Math.Max(Math.Max(er, eg), eb));
                histogram[Math.Min(1023, er)]++;
                histogram[Math.Min(1023, eg)]++;
                histogram[Math.Min(1023, eb)]++;
                double pixelV = er + eg + eb;
                double pixelB = Math.Abs((int)br - (int)tr) + Math.Abs((int)bg - (int)tg) + Math.Abs((int)bb - (int)tb);
                sqV += Sq(er) + Sq(eg) + Sq(eb);
                sqB += Sq((int)br - (int)tr) + Sq((int)bg - (int)tg) + Sq((int)bb - (int)tb);
                sqR += Sq((int)pr - (int)tr) + Sq((int)pg - (int)tg) + Sq((int)pb - (int)tb);
                absV += pixelV;
                absB += pixelB;
                n += 3;
                if (scene.IsPq)
                {
                    if (Math.Max(Math.Max(tr, tg), tb) >= highlightCode)
                    {
                        hl++;
                        hlV += pixelV / 3;
                        hlB += pixelB / 3;
                    }
                    else if (Math.Max(Math.Max(tr, tg), tb) <= darkCode)
                    {
                        dk++;
                        dkV += pixelV / 3;
                        dkB += pixelB / 3;
                        dkOffV += Math.Max(Math.Max(er, eg), eb) > 4 ? 1 : 0;
                        dkOffB += Math.Max(Math.Max(Math.Abs((int)br - (int)tr), Math.Abs((int)bg - (int)tg)), Math.Abs((int)bb - (int)tb)) > 4 ? 1 : 0;
                    }
                }
            }
        }

        var peak = (double)PixelCodes.MaxCode(format);
        double Psnr(double sq) => sq == 0 ? 99 : 10 * Math.Log10(peak * peak / (sq / n));
        var p999 = Percentile(histogram, n, 0.999);
        var (distinctT, m4T) = CodeStats(scene, scene.Truth);
        var (distinctV, m4V) = CodeStats(scene, output);
        return new QualityResult
        {
            Scene = scene,
            Mode = mode,
            PsnrVfg = Psnr(sqV),
            PsnrBlend = Psnr(sqB),
            PsnrRepeat = Psnr(sqR),
            MaeVfg = absV / n,
            MaeBlend = absB / n,
            MaxVfg = max,
            P999Vfg = p999,
            HighlightPixels = hl,
            HighlightMaeVfg = hl == 0 ? 0 : hlV / hl,
            HighlightMaeBlend = hl == 0 ? 0 : hlB / hl,
            DarkPixels = dk,
            DarkMaeVfg = dk == 0 ? 0 : dkV / dk,
            DarkMaeBlend = dk == 0 ? 0 : dkB / dk,
            DarkOff4Vfg = dk == 0 ? 0 : (double)dkOffV / dk,
            DarkOff4Blend = dk == 0 ? 0 : (double)dkOffB / dk,
            DistinctTruth = distinctT,
            DistinctVfg = distinctV,
            Multiple4Truth = m4T,
            Multiple4Vfg = m4V,
            GpuNs = gpuNs,
        };
    }

    private (int Distinct, double Multiple4) CodeStats(QualityScene scene, uint[] pixels)
    {
        if (scene.BandingWindow is not { } w)
        {
            return (0, 0);
        }

        var seen = new HashSet<uint>();
        for (var y = w.Y0; y < w.Y1; y++)
        {
            for (var x = w.X0; x < w.X1; x++)
            {
                var (_, g, _) = PixelCodes.Unpack(pixels[(y * _width) + x], scene.Format);
                seen.Add(g);
            }
        }

        // The share among distinct codes, so a large flat area does not dominate.
        var distinctMultiple4 = seen.Count(static c => (c & 3) == 0);
        return (seen.Count, seen.Count == 0 ? 0 : (double)distinctMultiple4 / seen.Count);
    }

    private static double Sq(int d) => (double)d * d;

    private static uint Percentile(long[] histogram, long n, double fraction)
    {
        var target = (long)Math.Ceiling(fraction * n);
        long seen = 0;
        for (var e = 0; e < histogram.Length; e++)
        {
            seen += histogram[e];
            if (seen >= target)
            {
                return (uint)e;
            }
        }

        return (uint)(histogram.Length - 1);
    }

    private static uint[] Blend(QualityScene scene)
    {
        var blend = new uint[scene.Prev.Length];
        for (var i = 0; i < blend.Length; i++)
        {
            var (ar, ag, ab) = PixelCodes.Unpack(scene.Prev[i], scene.Format);
            var (br, bg, bb) = PixelCodes.Unpack(scene.Curr[i], scene.Format);
            blend[i] = PixelCodes.Pack((ar + br + 1) / 2, (ag + bg + 1) / 2, (ab + bb + 1) / 2, scene.Format);
        }

        return blend;
    }

    private void Save(string path, QualityScene scene, uint[] pixels)
    {
        var rgb = new ushort[_width * _height * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            var (r, g, b) = PixelCodes.Unpack(pixels[i], scene.Format);
            rgb[3 * i] = Widen(r, scene.IsPq);
            rgb[(3 * i) + 1] = Widen(g, scene.IsPq);
            rgb[(3 * i) + 2] = Widen(b, scene.IsPq);
        }

        PngWriter.WriteRgb16(path, _width, _height, rgb, scene.IsPq ? PngColor.Pq : PngColor.Srgb);
    }

    private static ushort Widen(uint code, bool tenBit) => tenBit ? (ushort)((code << 6) | (code >> 4)) : (ushort)(code * 257);

    private void SaveError(string path, QualityScene scene, uint[] output)
    {
        var grey = new byte[_width * _height];
        var gain = scene.IsPq ? 2 : 8;
        for (var i = 0; i < grey.Length; i++)
        {
            var (tr, tg, tb) = PixelCodes.Unpack(scene.Truth[i], scene.Format);
            var (vr, vg, vb) = PixelCodes.Unpack(output[i], scene.Format);
            var e = Math.Max(Math.Max(Math.Abs((int)vr - (int)tr), Math.Abs((int)vg - (int)tg)), Math.Abs((int)vb - (int)tb));
            grey[i] = (byte)Math.Min(255, e * gain);
        }

        PngWriter.WriteGrey8(path, _width, _height, grey);
    }
}
