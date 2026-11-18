using System.Globalization;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>The uncompressed pixel formats this source captures.</summary>
public enum CapturePixelFormat
{
    /// <summary>Not asked for: the source picks (10-bit first).</summary>
    Unknown = 0,

    /// <summary>8-bit 4:2:0, a luma plane then interleaved CbCr (V4L2 'NV12').</summary>
    Nv12 = 1,

    /// <summary>10-bit 4:2:0 in 16-bit samples, the code in the top bits (V4L2 'P010'): the HDR format.</summary>
    P010 = 2,

    /// <summary>8-bit packed 4:2:2 (V4L2 'YUYV').</summary>
    Yuyv = 3,
}

/// <summary>What to capture: each item left at its default is chosen from what the device offers.</summary>
public sealed record CaptureRequest
{
    /// <summary>Frame width and height; 0: the largest the format offers.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Frames per second (59.94 and 60 are different rates); 0: the highest offered at the chosen size.</summary>
    public double Fps { get; init; }

    /// <summary><see cref="CapturePixelFormat.Unknown"/>: P010 when offered (10-bit), else NV12, else YUYV.</summary>
    public CapturePixelFormat Format { get; init; }

    public override string ToString()
    {
        var size = Width > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}") : "largest size";
        var rate = Fps > 0 ? Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : "highest rate";
        var format = Format == CapturePixelFormat.Unknown ? "10-bit first" : CaptureFormats.Name(Format);
        return $"{size}, {rate}, {format}";
    }
}

/// <summary>One mode a device offers: a format, a size and a frame interval.</summary>
public readonly record struct CaptureMode(CapturePixelFormat Format, int Width, int Height, V4l2Interval Interval)
{
    public double Fps => Interval.Fps;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height} {CaptureFormats.Name(Format)} @ {Interval}");
}

/// <summary>What the driver actually set (it may change what was asked for without failing).</summary>
public sealed record NegotiatedFormat
{
    public required CaptureMode Requested { get; init; }

    public required CapturePixelFormat Format { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int BytesPerLine { get; init; }

    public required int SizeImage { get; init; }

    public required V4l2Interval Interval { get; init; }

    /// <summary>False when the driver reports no frame interval (the rate is then the requested one, unconfirmed).</summary>
    public bool IntervalConfirmed { get; init; }

    public uint Colorspace { get; init; }

    public uint XferFunc { get; init; }

    public uint YcbcrEnc { get; init; }

    public uint Quantization { get; init; }

    public uint Field { get; init; }

    /// <summary>Where the driver departed from the request, one line each (empty: as asked).</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    public FrameRate Rate => Interval.Numerator == 0 || Interval.Denominator == 0
        ? default
        : new FrameRate(Interval.Denominator, Interval.Numerator);

    public int BitDepth => Format == CapturePixelFormat.P010 ? 10 : 8;

    /// <summary>The bytes one frame needs: the luma plane plus the chroma (4:2:0: half as many rows; YUYV: none).</summary>
    public long FrameBytes => Format == CapturePixelFormat.Yuyv
        ? (long)BytesPerLine * Height
        : (long)BytesPerLine * Height + ((long)BytesPerLine * ((Height + 1) / 2));

    /// <summary>The RGB format of the frames: RGB10A2 for 10-bit captures, RGBA8 otherwise (code values passed through).</summary>
    public PixelFormat OutputFormat => BitDepth > 8 ? PixelFormat.Rgb10A2 : PixelFormat.Rgba8;

    public CadenceSampling Sampling => Format switch
    {
        CapturePixelFormat.P010 => CadenceSampling.P010,
        CapturePixelFormat.Yuyv => CadenceSampling.Yuyv,
        _ => CadenceSampling.Nv12,
    };

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Width}x{Height} {CaptureFormats.Name(Format)} @ {Interval} fps{(IntervalConfirmed ? "" : " (unconfirmed)")}, {BytesPerLine} B per line, {SizeImage} B per frame");
}

/// <summary>Pixel format names, codes, and the mode choice (pure: tested without a device).</summary>
public static class CaptureFormats
{
    /// <summary>The formats this build converts.</summary>
    public static readonly IReadOnlyList<CapturePixelFormat> Supported = [CapturePixelFormat.P010, CapturePixelFormat.Nv12, CapturePixelFormat.Yuyv];

    public static string Name(CapturePixelFormat format) => format switch
    {
        CapturePixelFormat.Nv12 => "NV12",
        CapturePixelFormat.P010 => "P010",
        CapturePixelFormat.Yuyv => "YUYV",
        _ => "any",
    };

    public static uint FourCc(CapturePixelFormat format) => format switch
    {
        CapturePixelFormat.Nv12 => V4l2Defs.PixFmtNv12,
        CapturePixelFormat.P010 => V4l2Defs.PixFmtP010,
        CapturePixelFormat.Yuyv => V4l2Defs.PixFmtYuyv,
        _ => 0,
    };

    public static CapturePixelFormat FromFourCc(uint fourCc) => fourCc switch
    {
        V4l2Defs.PixFmtNv12 => CapturePixelFormat.Nv12,
        V4l2Defs.PixFmtP010 => CapturePixelFormat.P010,
        V4l2Defs.PixFmtYuyv => CapturePixelFormat.Yuyv,
        _ => CapturePixelFormat.Unknown,
    };

    public static bool IsCompressed(uint fourCc) => fourCc is V4l2Defs.PixFmtMjpeg or V4l2Defs.PixFmtJpeg;

    /// <summary>
    /// Chooses the mode for <paramref name="request"/> from <paramref name="offered"/> (every uncompressed mode a device
    /// offers in a supported format). With nothing asked for: 10-bit first, then the largest size, then the highest rate.
    /// A size asked for must be offered exactly; a rate must be within 0.02% of one offered (so 59.94 and 60 stay apart).
    /// Throws <see cref="CaptureFormatException"/> saying what is offered instead.
    /// </summary>
    public static CaptureMode Choose(IReadOnlyList<CaptureMode> offered, CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(request);
        IEnumerable<CaptureMode> candidates = offered;
        if (request.Format != CapturePixelFormat.Unknown)
        {
            candidates = candidates.Where(m => m.Format == request.Format);
            if (!candidates.Any())
            {
                throw new CaptureFormatException($"the device does not offer {Name(request.Format)}; it offers {Formats(offered)}");
            }
        }

        if (request.Width > 0)
        {
            var sized = candidates.Where(m => m.Width == request.Width && m.Height == request.Height).ToList();
            if (sized.Count == 0)
            {
                throw new CaptureFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"the device does not offer {request.Width}x{request.Height}{(request.Format == CapturePixelFormat.Unknown ? "" : " in " + Name(request.Format))}; " +
                    $"sizes offered: {Sizes(candidates)}"));
            }

            candidates = sized;
        }

        if (request.Fps > 0)
        {
            // 0.02%: a typed 59.94 matches 60000/1001, and 59.94 and 60 (0.1% apart) stay apart.
            var timed = candidates.Where(m => Math.Abs(m.Fps - request.Fps) <= request.Fps * 0.0002).ToList();
            if (timed.Count == 0)
            {
                throw new CaptureFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"the device does not offer {request.Fps:0.###} fps{(request.Width > 0 ? $" at {request.Width}x{request.Height}" : "")}" +
                    $"{(request.Format == CapturePixelFormat.Unknown ? "" : " in " + Name(request.Format))}; rates offered: {Rates(candidates)}"));
            }

            candidates = timed;
        }

        return candidates
            .OrderByDescending(m => request.Format == CapturePixelFormat.Unknown ? Preference(m.Format) : 0)
            .ThenByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ThenBy(m => Math.Abs(m.Fps - request.Fps))
            .First();
    }

    /// <summary>The supported uncompressed modes of an enumerated device (MJPEG and other formats left out).</summary>
    public static List<CaptureMode> Modes(IEnumerable<(V4l2FormatDescription Format, IReadOnlyList<V4l2FrameSize> Sizes)> formats)
    {
        var modes = new List<CaptureMode>();
        foreach (var (format, sizes) in formats)
        {
            var kind = FromFourCc(format.FourCc);
            if (kind == CapturePixelFormat.Unknown || format.Compressed || format.Emulated)
            {
                continue;
            }

            foreach (var size in sizes)
            {
                foreach (var interval in size.Intervals)
                {
                    if (interval.Numerator > 0 && interval.Denominator > 0)
                    {
                        modes.Add(new CaptureMode(kind, size.Width, size.Height, interval));
                    }
                }
            }
        }

        return modes;
    }

    private static int Preference(CapturePixelFormat format) => format switch
    {
        CapturePixelFormat.P010 => 3,
        CapturePixelFormat.Nv12 => 2,
        _ => 1,
    };

    private static string Formats(IReadOnlyList<CaptureMode> offered) =>
        offered.Count == 0 ? "no uncompressed NV12, P010 or YUYV mode" : string.Join(", ", offered.Select(m => Name(m.Format)).Distinct());

    private static string Sizes(IEnumerable<CaptureMode> modes) =>
        string.Join(", ", modes.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Width}x{m.Height}")).Distinct());

    private static string Rates(IEnumerable<CaptureMode> modes) =>
        string.Join(", ", modes.Select(m => m.Interval.ToString()).Distinct());
}

/// <summary>Applies a chosen mode to a device and reads back what it set.</summary>
[SupportedOSPlatform("linux")]
internal static class CaptureNegotiation
{
    public static NegotiatedFormat Apply(V4l2Device device, CaptureMode mode)
    {
        var fourCc = CaptureFormats.FourCc(mode.Format);
        V4l2PixFormat set;
        try
        {
            set = device.SetFormat(new V4l2PixFormat
            {
                Width = (uint)mode.Width,
                Height = (uint)mode.Height,
                PixelFormat = fourCc,
                Field = V4l2Defs.FieldNone,
            });
        }
        catch (V4l2Exception e) when (e.Errno == LibC.Ebusy)
        {
            throw new CaptureUnavailableException($"{device.Path} is busy (another application is capturing from it)", e);
        }

        var changes = new List<string>();
        var actual = CaptureFormats.FromFourCc(set.PixelFormat);
        if (set.PixelFormat != fourCc)
        {
            var name = V4l2FormatDescription.FourCcText(set.PixelFormat);
            throw new CaptureFormatException(CaptureFormats.IsCompressed(set.PixelFormat)
                ? $"asked for {CaptureFormats.Name(mode.Format)} but the driver switched to {name} (compressed: it needs a decode step and adds latency, so it is never used)"
                : $"asked for {CaptureFormats.Name(mode.Format)} but the driver set {name}, which this source does not convert");
        }

        if (set.Width != mode.Width || set.Height != mode.Height)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"size {mode.Width}x{mode.Height} asked, {set.Width}x{set.Height} set"));
        }

        if (set.Field is not (V4l2Defs.FieldNone or V4l2Defs.FieldAny))
        {
            throw new CaptureFormatException(string.Create(CultureInfo.InvariantCulture, $"the driver set an interlaced field order ({set.Field}); only progressive capture is supported"));
        }

        var bytesPerSample = actual == CapturePixelFormat.P010 || actual == CapturePixelFormat.Yuyv ? 2 : 1;
        if (set.BytesPerLine < set.Width * bytesPerSample || set.Width % 2 != 0 || set.Height % 2 != 0)
        {
            throw new CaptureFormatException(string.Create(CultureInfo.InvariantCulture,
                $"unusable layout from the driver: {set.Width}x{set.Height}, {set.BytesPerLine} B per line"));
        }

        var interval = mode.Interval;
        var confirmed = false;
        _ = device.GetFrameInterval(out var settable);
        if (settable)
        {
            try
            {
                interval = device.SetFrameInterval(mode.Interval);
                confirmed = interval.Numerator > 0 && interval.Denominator > 0;
            }
            catch (V4l2Exception e) when (e.Errno == LibC.Ebusy)
            {
                throw new CaptureUnavailableException($"{device.Path} is busy (another application is capturing from it)", e);
            }
        }
        else
        {
            changes.Add("the driver cannot set a frame interval: the rate is not confirmed");
        }

        if (confirmed && Math.Abs(interval.Fps - mode.Fps) > mode.Fps * 0.001)
        {
            changes.Add($"rate {mode.Interval} fps asked, {interval} fps set");
        }

        // Read back once more: the rate can change the format on some drivers.
        var final = device.GetFormat();
        var layout = new NegotiatedFormat
        {
            Requested = mode,
            Format = actual,
            Width = (int)final.Width,
            Height = (int)final.Height,
            BytesPerLine = (int)final.BytesPerLine,
            SizeImage = (int)final.SizeImage,
            Interval = interval,
            IntervalConfirmed = confirmed,
            Colorspace = final.Colorspace,
            XferFunc = final.XferFunc,
            YcbcrEnc = final.YcbcrEnc,
            Quantization = final.Quantization,
            Field = final.Field,
            Changes = changes,
        };
        if (final.PixelFormat != fourCc || final.Width != set.Width || final.Height != set.Height)
        {
            throw new CaptureFormatException($"the driver changed the format again after the rate was set ({V4l2FormatDescription.FourCcText(final.PixelFormat)} {final.Width}x{final.Height})");
        }

        if (layout.SizeImage < layout.FrameBytes)
        {
            throw new CaptureFormatException(string.Create(CultureInfo.InvariantCulture,
                $"the driver's frame size {layout.SizeImage} B is smaller than {layout.FrameBytes} B for {layout.Width}x{layout.Height} {CaptureFormats.Name(actual)}"));
        }

        return layout;
    }
}
