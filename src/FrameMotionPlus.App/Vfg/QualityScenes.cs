using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.App.Vfg;

/// <summary>One pair of frames with known motion and its true middle frame (t = 0.5), on the host.</summary>
internal sealed record QualityScene
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required PixelFormat Format { get; init; }

    public required uint[] Prev { get; init; }

    public required uint[] Truth { get; init; }

    public required uint[] Curr { get; init; }

    /// <summary>Columns at the right edge where new content enters (no truth there), excluded from the metrics.</summary>
    public int EdgeColumns { get; init; }

    /// <summary>Rows the metrics cover (the pattern's moving band; all rows otherwise).</summary>
    public (int Y0, int Y1)? Rows { get; init; }

    /// <summary>A window of smooth dark gradient where banding would show: (x0, y0, x1, y1).</summary>
    public (int X0, int Y0, int X1, int Y1)? BandingWindow { get; init; }

    public bool IsPq => Format == PixelFormat.Rgb10A2;
}

/// <summary>
/// The scenes of <c>fmp bench vfg-quality</c>, SDR (RGBA8) and PQ (RGB10A2, full-range ST 2084 codes in a BT.2020
/// container). Every one has an exact truth: the pattern kernel's frame in between, a pan by whole pixels, or analytic
/// rendering at the middle position.
/// </summary>
internal static class QualityScenes
{
    /// <summary>A large picture to pan across for the "photo" scenes, if this machine has one of these (desktop wallpapers).</summary>
    public static readonly string[] PhotoCandidates =
    [
        "/usr/share/wallpapers/Next/contents/images/5120x2880.png",
        "/usr/share/wallpapers/Horos/contents/images/5120x2880.png",
        "/usr/share/wallpapers/Air/contents/images/5120x2880.jpg",
        "/usr/share/backgrounds/gnome/adwaita-l.jpg",
    ];

    public static int Step(int width) => 2 * Math.Max(1, (int)Math.Round(6.0 * width / 1920));

    public static List<QualityScene> Build(int width, int height, CudaContext cuda, ImageKernels kernels, string? photo, TextWriter progress)
    {
        var scenes = new List<QualityScene>();
        var s = Step(width);
        scenes.Add(Pattern(width, height, PixelFormat.Rgba8, cuda, kernels));
        scenes.Add(Pan(width, height, s));
        if (photo is not null)
        {
            progress.WriteLine($"  photo       {photo} (decoded by ffmpeg)");
            var rgb = FfmpegFrames.DecodeRgb48(photo, width + (2 * s), height);
            scenes.Add(Photo(rgb, width, height, s, pq: false));
            scenes.Add(Photo(rgb, width, height, s, pq: true));
        }

        scenes.Add(Pattern(width, height, PixelFormat.Rgb10A2, cuda, kernels));
        scenes.Add(RampPan(width, height, s));
        scenes.Add(Specular(width, height, s));
        scenes.Add(DarkGradient(width, height, s));
        return scenes;
    }

    // ---- PQ (SMPTE ST 2084) ----

    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    /// <summary>Luminance in nits to the PQ signal in [0, 1].</summary>
    public static double PqEncode(double nits)
    {
        var y = Math.Pow(Math.Clamp(nits, 0, 10000) / 10000.0, M1);
        return Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
    }

    /// <summary>The PQ signal in [0, 1] to luminance in nits.</summary>
    public static double PqDecode(double signal)
    {
        var e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
        return 10000 * Math.Pow(Math.Max(e - C1, 0) / (C2 - (C3 * e)), 1 / M1);
    }

    public static uint Code10(double signal) => (uint)Math.Clamp(Math.Round(signal * 1023), 0, 1023);

    public static uint Code8(double value) => (uint)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static uint Pq(double r, double g, double b) =>
        PixelCodes.Pack(Code10(PqEncode(r)), Code10(PqEncode(g)), Code10(PqEncode(b)), PixelFormat.Rgb10A2);

    // ---- Scenes ----

    private static QualityScene Pattern(int width, int height, PixelFormat format, CudaContext cuda, ImageKernels kernels)
    {
        const uint k = 40;
        using var stream = CudaStream.Create(cuda);
        using var buffer = CudaDeviceBuffer.AllocateImage(cuda, width, height, format);
        uint[] Draw(uint index)
        {
            kernels.EnqueueTestPattern(stream, buffer.AsFrame(), index);
            var pixels = new uint[width * height];
            stream.CopyFrameToHost(buffer.AsFrame(), pixels);
            return pixels;
        }

        var pq = format == PixelFormat.Rgb10A2;
        return new QualityScene
        {
            Name = pq ? "pq-pattern" : "sdr-pattern",
            Description = pq
                ? "The project's test pattern in PQ: 203-nit bars, a 1000-nit box moving over 10 nits, the PQ code ramp and the 0.1-10000 nit step wedge (static). Metrics: the box band."
                : "The project's test pattern in 8-bit SDR: 75 % bars, a white box moving over dark grey, a grey ramp (static). Metrics: the box band.",
            Format = format,
            Prev = Draw(2 * k),
            Truth = Draw((2 * k) + 1),
            Curr = Draw((2 * k) + 2),
            Rows = (height * 7 / 12, height * 9 / 12),
        };
    }

    private static QualityScene Pan(int width, int height, int s)
    {
        var texture = new SyntheticTexture(width + (2 * s), height);
        uint[] Crop(int offset)
        {
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = texture.At(x + offset, y);
                    pixels[(y * width) + x] = PixelCodes.Pack(Code8(r), Code8(g), Code8(b), PixelFormat.Rgba8);
                }
            }

            return pixels;
        }

        return new QualityScene
        {
            Name = "sdr-pan",
            Description = $"A detailed synthetic texture (sinusoids at several scales) panned {2 * s} px left between the frames, 8-bit SDR.",
            Format = PixelFormat.Rgba8,
            Prev = Crop(0),
            Truth = Crop(s),
            Curr = Crop(2 * s),
            EdgeColumns = (2 * s) + 8,
        };
    }

    private static QualityScene Photo(ushort[] rgb, int width, int height, int s, bool pq)
    {
        var wide = width + (2 * s);
        uint[] Crop(int offset)
        {
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y * wide) + x + offset) * 3;
                    var (r, g, b) = (rgb[i] / 65535.0, rgb[i + 1] / 65535.0, rgb[i + 2] / 65535.0);
                    if (!pq)
                    {
                        pixels[(y * width) + x] = PixelCodes.Pack(Code8(r), Code8(g), Code8(b), PixelFormat.Rgba8);
                        continue;
                    }

                    // An HDR-like grade of the sRGB picture: linear light at 203 nits diffuse white, highlights expanded
                    // smoothly to about 4000 nits; BT.709 primaries carried in the BT.2020/PQ container unmapped.
                    var (lr, lg, lb) = (SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
                    var luma = (0.2126 * lr) + (0.7152 * lg) + (0.0722 * lb);
                    var boost = 1 + (18.7 * Math.Pow(Math.Max(0, (luma - 0.6) / 0.4), 3));
                    pixels[(y * width) + x] = Pq(203 * lr * boost, 203 * lg * boost, 203 * lb * boost);
                }
            }

            return pixels;
        }

        return new QualityScene
        {
            Name = pq ? "pq-photo" : "sdr-photo",
            Description = pq
                ? $"The same picture graded to HDR (203-nit white, highlights expanded to ~4000 nits), PQ RGB10A2, panned {2 * s} px left."
                : $"A real picture (a desktop wallpaper, decoded by ffmpeg) panned {2 * s} px left, 8-bit SDR.",
            Format = pq ? PixelFormat.Rgb10A2 : PixelFormat.Rgba8,
            Prev = Crop(0),
            Truth = Crop(s),
            Curr = Crop(2 * s),
            EdgeColumns = (2 * s) + 8,
        };
    }

    private static QualityScene RampPan(int width, int height, int s)
    {
        var wide = width + (2 * s);
        double[] steps = [0, 0.1, 1, 10, 100, 203, 400, 1000, 4000, 10000];
        uint[] Crop(int offset)
        {
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var u = x + offset;
                    uint code;
                    if (y < height / 2)
                    {
                        code = (uint)Math.Round(1023.0 * u / (wide - 1)); // every PQ code, left to right
                    }
                    else
                    {
                        code = Code10(PqEncode(steps[Math.Min(9, u * 10 / wide)]));
                    }

                    pixels[(y * width) + x] = PixelCodes.Pack(code, code, code, PixelFormat.Rgb10A2);
                }
            }

            return pixels;
        }

        return new QualityScene
        {
            Name = "pq-ramp-pan",
            Description = $"The PQ code ramp (every code 0-1023, top half) and the 0/0.1/1/10/100/203/400/1000/4000/10000-nit step wedge (bottom half) panned {2 * s} px left: banding in the ramp, ringing at the steps.",
            Format = PixelFormat.Rgb10A2,
            Prev = Crop(0),
            Truth = Crop(s),
            Curr = Crop(2 * s),
            EdgeColumns = (2 * s) + 8,
            BandingWindow = (width / 8, height / 8, width * 7 / 8, height * 3 / 8),
        };
    }

    private static QualityScene Specular(int width, int height, int s)
    {
        var texture = new SyntheticTexture(width + (2 * s), height);
        var random = new Random(7);
        var scale = width / 1920.0;
        var lights = new (double X, double Y, double Vx, double Vy, double Sigma, double Peak)[60];
        for (var i = 0; i < lights.Length; i++)
        {
            lights[i] = (
                random.NextDouble() * width,
                random.NextDouble() * height,
                ((random.NextDouble() * 2) - 1) * 16 * scale,
                ((random.NextDouble() * 2) - 1) * 10 * scale,
                (1 + (random.NextDouble() * 3)) * scale,
                600 * Math.Pow(10000.0 / 600, random.NextDouble()));
        }

        uint[] Render(double phase)
        {
            var offset = (int)Math.Round(phase * 2 * s);
            var nits = new double[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // A dark textured background, 0.05 to about 2 nits, panning with the camera.
                    var (_, g, _) = texture.At(x + offset, y);
                    nits[(y * width) + x] = 0.05 + (2.0 * g * g);
                }
            }

            foreach (var (lx, ly, vx, vy, sigma, peak) in lights)
            {
                var cx = lx + (vx * phase * 2);
                var cy = ly + (vy * phase * 2);
                var reach = (int)Math.Ceiling(4 * sigma);
                for (var y = Math.Max(0, (int)cy - reach); y <= Math.Min(height - 1, (int)cy + reach); y++)
                {
                    for (var x = Math.Max(0, (int)cx - reach); x <= Math.Min(width - 1, (int)cx + reach); x++)
                    {
                        var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                        nits[(y * width) + x] += peak * Math.Exp(-d2 / (2 * sigma * sigma));
                    }
                }
            }

            var pixels = new uint[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Pq(nits[i], nits[i] * 0.95, nits[i] * 0.85); // slightly warm highlights
            }

            return pixels;
        }

        return new QualityScene
        {
            Name = "pq-specular",
            Description = $"60 small specular highlights (600 to 10000 nits, Gaussian, 1-4 px) each moving its own way (up to {16 * scale:0} px) over a dark textured background (0.05-2 nits) that pans {2 * s} px: highlight smear, ghosting and halos.",
            Format = PixelFormat.Rgb10A2,
            Prev = Render(0),
            Truth = Render(0.5),
            Curr = Render(1),
            EdgeColumns = (2 * s) + 8,
        };
    }

    private static QualityScene DarkGradient(int width, int height, int s)
    {
        var wide = width + (2 * s);
        var radius = height / 8.0;
        uint[] Render(double phase)
        {
            var offset = (int)Math.Round(phase * 2 * s);
            var cx = (width * 0.6) + (phase * 6 * s);
            var cy = (height * 0.7) + (phase * 2 * s);
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // 0.002 to about 3 nits along the diagonal: PQ codes from about 10 to 250, each code several px wide.
                    var u = (double)(x + offset) / wide;
                    var v = (double)y / height;
                    var nits = 0.002 + (3.0 * Math.Pow((0.7 * u) + (0.3 * v), 2.2));

                    // A 20-nit disc with a soft 3-px edge moving across.
                    var d = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                    var cover = Math.Clamp((radius - d) / 3 + 0.5, 0, 1);
                    nits = (nits * (1 - cover)) + (20 * cover);
                    pixels[(y * width) + x] = Pq(nits, nits, nits * 1.05);
                }
            }

            return pixels;
        }

        return new QualityScene
        {
            Name = "pq-dark-gradient",
            Description = $"A smooth dark gradient (0.002 to 3 nits, PQ codes ~10 to ~250) panned {2 * s} px with a 20-nit disc moving across: banding, and whether 10-bit precision survives (the window's code statistics).",
            Format = PixelFormat.Rgb10A2,
            Prev = Render(0),
            Truth = Render(0.5),
            Curr = Render(1),
            EdgeColumns = (2 * s) + 8,
            BandingWindow = (width / 16, height / 16, width / 2, height / 3),
        };
    }

    private static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}
