using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.App.Pattern;

/// <summary>The HDR test patterns of <c>fmp hdr-pattern</c>.</summary>
internal enum HdrPatternKind
{
    /// <summary>Every PQ code from 0 to 1023 left to right in grey, with ticks and nit labels; R, G and B ramps below.</summary>
    Ramp = 0,

    /// <summary>Patches at 100, 203, 400, 600, 1,000, 2,000, 4,000 and 10,000 nits, each with an inner square 25% brighter.</summary>
    Peak = 1,

    /// <summary>BT.2020 colour bars (white, yellow, cyan, green, magenta, red, blue) at 203 nits, and at 1,000 nits below.</summary>
    Bars = 2,

    /// <summary>Near-black steps: PQ codes 0 to 128 on code 0, labelled in nits.</summary>
    Black = 3,
}

/// <summary>
/// The HDR patterns as full-range R'G'B' PQ codes (what an HDR10 swapchain shows as they are), and the PQ arithmetic and
/// YCbCr encoding the numeric check uses. Features sit on even pixel coordinates, so 4:2:0 chroma is exact inside them.
/// Labels are drawn at <see cref="LabelNits"/> (moderate, so a label is never the brightest thing on screen). Pure.
/// </summary>
internal static class HdrPatterns
{
    public static readonly int[] PeakNits = [100, 203, 400, 600, 1000, 2000, 4000, 10000];

    public static readonly double[] RampTicks = [0.01, 0.1, 1, 10, 100, 203, 400, 1000, 4000, 10000];

    public static readonly int[] BlackCodes = [0, 1, 2, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128];

    public const double LabelNits = 100;

    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    /// <summary>SMPTE ST 2084 inverse EOTF: nits to the PQ signal, 0 to 1.</summary>
    public static double Pq(double nits)
    {
        var y = Math.Pow(Math.Clamp(nits, 0, 10000) / 10000, M1);
        return Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
    }

    /// <summary>SMPTE ST 2084 EOTF: the PQ signal (0 to 1) to nits.</summary>
    public static double Nits(double signal)
    {
        var p = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
        return Math.Pow(Math.Max(p - C1, 0) / (C2 - (C3 * p)), 1 / M1) * 10000;
    }

    /// <summary>The 10-bit full-range PQ code of <paramref name="nits"/>.</summary>
    public static int Code(double nits) => (int)Math.Round(Pq(nits) * 1023);

    public static string Name(HdrPatternKind kind) => kind.ToString().ToLowerInvariant();

    public static HdrPatternKind Parse(string text) => text.ToLowerInvariant() switch
    {
        "ramp" => HdrPatternKind.Ramp,
        "peak" or "clip" or "clipping" => HdrPatternKind.Peak,
        "bars" => HdrPatternKind.Bars,
        "black" or "near-black" => HdrPatternKind.Black,
        _ => throw new Cli.UsageException($"fmp hdr-pattern: the pattern is ramp, peak, bars or black, got '{text}'."),
    };

    /// <summary>What a correct HDR display shows, for the start-up lines and the owner's check by eye.</summary>
    public static string Describe(HdrPatternKind kind) => kind switch
    {
        HdrPatternKind.Ramp => "every PQ code 0..1023 left to right (grey), ticks labelled in nits; R, G and B ramps below. Correct: smooth, no bands " +
            "or steps, black at the left, the ramp brightening up to the display's peak and flat beyond it (where the display clips or KWin compresses)",
        HdrPatternKind.Peak => "patches at 100, 203, 400, 600, 1000, 2000, 4000 and 10000 nits, each with an inner square 25% brighter. Correct: each " +
            "patch brighter than the one before up to the display's peak; where the inner square disappears, the display (or KWin) has reached its limit",
        HdrPatternKind.Bars => "BT.2020 colour bars at 203 nits (HDR reference white) and at 1000 nits below: white, yellow, cyan, green, magenta, red, " +
            "blue. Correct: saturated, the top row at the brightness of SDR white on a calibrated HDR display, no colour shift between rows",
        _ => "near-black steps: PQ codes 0, 1, 2, 4 ... 128 on code 0, labelled in nits. Correct: the background pure black (an OLED off); the " +
            "lowest steps may vanish into black, the rest visible as separate steps",
    };

    /// <summary>Renders <paramref name="kind"/> as (R, G, B) PQ codes, row-major.</summary>
    public static (int R, int G, int B)[] Render(HdrPatternKind kind, int width, int height)
    {
        var px = new (int R, int G, int B)[width * height];
        var label = Code(LabelNits);
        var u = Math.Max(2, (height / 54) & ~1); // the grid unit, even
        switch (kind)
        {
            case HdrPatternKind.Ramp:
                RenderRamp(px, width, height, u, label);
                break;
            case HdrPatternKind.Peak:
                RenderPeak(px, width, height, u, label);
                break;
            case HdrPatternKind.Bars:
                RenderBars(px, width, height, u, label);
                break;
            default:
                RenderBlack(px, width, height, u, label);
                break;
        }

        return px;
    }

    /// <summary>The rectangles a numeric check should judge (inside features, away from edges and labels), as (x, y, w, h).</summary>
    public static List<(int X, int Y, int W, int H)> Interiors(HdrPatternKind kind, int width, int height)
    {
        var u = Math.Max(2, (height / 54) & ~1);
        var list = new List<(int, int, int, int)>();
        switch (kind)
        {
            case HdrPatternKind.Ramp:
                list.Add((0, Even(height * 22 / 100) + 2, width, Even(height * 20 / 100) - 4));
                break;
            case HdrPatternKind.Peak:
                foreach (var r in PeakRects(width, height, u))
                {
                    // The patch's corners: clear of its edges and of the brighter inner square in the middle.
                    var c = Even(r.W * 3 / 8) - 4;
                    list.Add((r.X + 2, r.Y + 2, c, c));
                    list.Add((r.X + r.W - 2 - c, r.Y + r.H - 2 - c, c, c));
                }

                break;
            case HdrPatternKind.Bars:
                foreach (var r in BarRects(width, height))
                {
                    list.Add((r.X + 2, r.Y + 2, r.W - 4, r.H - 4));
                }

                break;
            default:
                foreach (var r in BlackRects(width, height, u))
                {
                    list.Add((r.X + 2, r.Y + 2, r.W - 4, r.H - 4));
                }

                break;
        }

        return list;
    }

    private static void RenderRamp((int R, int G, int B)[] px, int w, int h, int u, int label)
    {
        // The grey ramp: column x shows code floor(x * 1024 / w), in pairs of columns so chroma is exact.
        var top = Even(h * 22 / 100);
        var bottom = top + Even(h * 20 / 100);
        for (var y = top; y < bottom; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = (x & ~1) * 1024 / w;
                px[(y * w) + x] = (c, c, c);
            }
        }

        // Ticks above the ramp at RampTicks, labelled in nits.
        var scale = Math.Max(2, u / 2) & ~1;
        foreach (var nits in RampTicks)
        {
            var x = Math.Clamp(Code(nits) * w / 1024, 0, w - 2) & ~1;
            Fill(px, w, h, x, top - (2 * u), 2, (2 * u) - 2, label, label, label);
            var text = Text(nits);
            var tw = TextWidth(text, scale);
            DrawText(px, w, h, Math.Clamp(x - (tw / 2), 0, w - tw) & ~1, top - (2 * u) - (6 * scale), text, scale, label);
        }

        // R, G and B ramps below.
        var band = Even(h * 8 / 100);
        var y0 = Even(h * 52 / 100);
        for (var c = 0; c < 3; c++)
        {
            for (var y = y0 + (c * band); y < y0 + ((c + 1) * band) - 2; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var v = (x & ~1) * 1024 / w;
                    px[(y * w) + x] = c == 0 ? (v, 0, 0) : c == 1 ? (0, v, 0) : (0, 0, v);
                }
            }
        }
    }

    private static List<(int X, int Y, int W, int H)> PeakRects(int w, int h, int u)
    {
        var list = new List<(int, int, int, int)>();
        var size = Even(Math.Min(w / 10, h * 3 / 10));
        var gap = Even((w - (PeakNits.Length * size)) / (PeakNits.Length + 1));
        var y = Even((h - size) / 2) - u;
        for (var i = 0; i < PeakNits.Length; i++)
        {
            list.Add((gap + (i * (size + gap)), y, size, size));
        }

        return list;
    }

    private static void RenderPeak((int R, int G, int B)[] px, int w, int h, int u, int label)
    {
        var rects = PeakRects(w, h, u);
        var scale = Math.Max(2, u / 2) & ~1;
        for (var i = 0; i < rects.Count; i++)
        {
            var (x, y, s, _) = rects[i];
            var v = Code(PeakNits[i]);
            var inner = Code(Math.Min(10000, PeakNits[i] * 1.25));
            Fill(px, w, h, x, y, s, s, v, v, v);
            Fill(px, w, h, x + Even(s * 3 / 8), y + Even(s * 3 / 8), Even(s / 4), Even(s / 4), inner, inner, inner);
            var text = Text(PeakNits[i]);
            var tw = TextWidth(text, scale);
            DrawText(px, w, h, (x + ((s - tw) / 2)) & ~1, y + s + u, text, scale, label);
        }
    }

    private static List<(int X, int Y, int W, int H)> BarRects(int w, int h)
    {
        var list = new List<(int, int, int, int)>();
        var bw = Even(w / 7);
        var bh = Even(h * 35 / 100);
        for (var row = 0; row < 2; row++)
        {
            for (var i = 0; i < 7; i++)
            {
                list.Add((i * bw, Even(h * 10 / 100) + (row * (bh + Even(h * 10 / 100))), bw, bh));
            }
        }

        return list;
    }

    private static void RenderBars((int R, int G, int B)[] px, int w, int h, int u, int label)
    {
        // White, yellow, cyan, green, magenta, red, blue: each channel at the row's level or 0 (BT.2020 primaries and
        // secondaries in the HDR10 container).
        (int R, int G, int B)[] bars = [(1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1)];
        var rects = BarRects(w, h);
        double[] levels = [203, 1000];
        for (var i = 0; i < rects.Count; i++)
        {
            var c = Code(levels[i / 7]);
            var b = bars[i % 7];
            Fill(px, w, h, rects[i].X, rects[i].Y, rects[i].W, rects[i].H, b.R * c, b.G * c, b.B * c);
        }

        var scale = Math.Max(2, u / 2) & ~1;
        DrawText(px, w, h, u, rects[0].Y - (7 * scale), "203", scale, label);
        DrawText(px, w, h, u, rects[7].Y - (7 * scale), "1000", scale, label);
    }

    private static List<(int X, int Y, int W, int H)> BlackRects(int w, int h, int u)
    {
        var list = new List<(int, int, int, int)>();
        var n = BlackCodes.Length;
        var size = Even(Math.Min(w / (n + 2), h / 4));
        var gap = Even((w - (n * size)) / (n + 1));
        for (var i = 0; i < n; i++)
        {
            list.Add((gap + (i * (size + gap)), Even((h - size) / 2) - u, size, size));
        }

        return list;
    }

    private static void RenderBlack((int R, int G, int B)[] px, int w, int h, int u, int label)
    {
        var rects = BlackRects(w, h, u);
        var scale = Math.Max(2, u / 3) & ~1;
        for (var i = 0; i < rects.Count; i++)
        {
            var c = BlackCodes[i];
            Fill(px, w, h, rects[i].X, rects[i].Y, rects[i].W, rects[i].H, c, c, c);
            var text = Text(Nits(c / 1023.0));
            var tw = TextWidth(text, scale);
            DrawText(px, w, h, Math.Max(0, rects[i].X + ((rects[i].W - tw) / 2)) & ~1, rects[i].Y + rects[i].H + u, text, scale, label);
        }
    }

    /// <summary>"0.01", "0.1", "1", "203", "10000"; small values to three significant digits ("0.00038").</summary>
    public static string Text(double nits)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (nits <= 0)
        {
            return "0";
        }

        if (nits >= 10)
        {
            return Math.Round(nits).ToString("0", ic);
        }

        var digits = Math.Max(0, 2 - (int)Math.Floor(Math.Log10(nits)));
        return Math.Round(nits, digits).ToString("0." + new string('#', digits), ic);
    }

    private static int Even(int v) => v & ~1;

    private static void Fill((int R, int G, int B)[] px, int w, int h, int x, int y, int fw, int fh, int r, int g, int b)
    {
        for (var j = Math.Max(0, y); j < Math.Min(h, y + fh); j++)
        {
            for (var i = Math.Max(0, x); i < Math.Min(w, x + fw); i++)
            {
                px[(j * w) + i] = (r, g, b);
            }
        }
    }

    // 3x5 digits and the point, one row of 3 bits per entry (top first).
    private static readonly int[][] Glyphs =
    [
        [7, 5, 5, 5, 7], [2, 6, 2, 2, 7], [7, 1, 7, 4, 7], [7, 1, 7, 1, 7], [5, 5, 7, 1, 1],
        [7, 4, 7, 1, 7], [7, 4, 7, 5, 7], [7, 1, 1, 1, 1], [7, 5, 7, 5, 7], [7, 5, 7, 1, 7],
    ];

    private static int TextWidth(string text, int scale) => text.Length * 4 * scale;

    private static void DrawText((int R, int G, int B)[] px, int w, int h, int x, int y, string text, int scale, int level)
    {
        foreach (var ch in text)
        {
            if (ch == '.')
            {
                Fill(px, w, h, x + scale, y + (4 * scale), scale, scale, level, level, level);
            }
            else if (ch is >= '0' and <= '9')
            {
                var glyph = Glyphs[ch - '0'];
                for (var row = 0; row < 5; row++)
                {
                    for (var col = 0; col < 3; col++)
                    {
                        if ((glyph[row] & (4 >> col)) != 0)
                        {
                            Fill(px, w, h, x + (col * scale), y + (row * scale), scale, scale, level, level, level);
                        }
                    }
                }
            }

            x += 4 * scale;
        }
    }

    /// <summary>
    /// R'G'B' codes (10-bit, full range) to P010 planes, limited range, with the YCbCr matrix of <paramref name="matrix"/>
    /// (BT.2020 non-constant luminance for HDR10, BT.709 for SDR): luma per pixel, chroma per 2x2 block from its mean.
    /// Returns 10-bit code values (not yet shifted into P010's high bits). The inverse of what the capture's conversion does.
    /// </summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr) ToYcbcr((int R, int G, int B)[] px, int w, int h, MatrixCoefficients matrix, int bits)
    {
        var (kr, kb) = matrix == MatrixCoefficients.Bt709 ? (0.2126, 0.0722) : (0.2627, 0.0593);
        var kg = 1 - kr - kb;
        var max = (1 << bits) - 1;
        var scale = 1 << (bits - 8);
        var y = new ushort[w * h];
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        double Luma((int R, int G, int B) p, int full) => (kr * p.R / full) + (kg * p.G / full) + (kb * p.B / full);
        var fullMax = 1023.0;
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                y[(j * w) + i] = (ushort)Math.Clamp(Math.Round((16 * scale) + (219 * scale * Luma(px[(j * w) + i], 1023))), 0, max);
            }
        }

        for (var j = 0; j < ch; j++)
        {
            for (var i = 0; i < cw; i++)
            {
                double sb = 0, sr = 0;
                var n = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var xx = Math.Min(w - 1, (2 * i) + dx);
                        var yy = Math.Min(h - 1, (2 * j) + dy);
                        var p = px[(yy * w) + xx];
                        var l = Luma(p, 1023);
                        sb += ((p.B / fullMax) - l) / (2 * (1 - kb));
                        sr += ((p.R / fullMax) - l) / (2 * (1 - kr));
                        n++;
                    }
                }

                cb[(j * cw) + i] = (ushort)Math.Clamp(Math.Round((128 * scale) + (224 * scale * sb / n)), 0, max);
                cr[(j * cw) + i] = (ushort)Math.Clamp(Math.Round((128 * scale) + (224 * scale * sr / n)), 0, max);
            }
        }

        return (y, cb, cr);
    }
}
