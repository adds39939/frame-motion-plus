using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>How the HDR decision is made.</summary>
public enum HdrMode
{
    /// <summary>From the V4L2 format's colourspace and transfer function (BT.2020 / SMPTE 2084 means HDR10).</summary>
    Auto = 0,

    /// <summary>HDR10 whatever the device says: PQ, BT.2020 primaries and (unless given) the BT.2020 matrix. For cards
    /// that do not signal it: the Elgato 4K X reports Rec. 709 for a PQ signal.</summary>
    On = 1,

    /// <summary>SDR whatever the device says.</summary>
    Off = 2,

    /// <summary>HDR10 when the device signals it or the capture is 10-bit (P010); otherwise as <see cref="Auto"/>: fixed for
    /// the run.</summary>
    TenBit = 3,

    /// <summary>
    /// A 10-bit (P010) capture follows the signal: HDR10 while its samples carry 10 significant bits, SDR while they carry
    /// only 8 (<see cref="SampleDepthProbe"/>). A PS5 is assumed to send SDR in 8 bits and HDR10 in 10 or more, and the
    /// Elgato 4K X to put an 8-bit signal into P010 with the two low bits zero (assumed; no real SDR signal seen yet), so
    /// this would tell the console's switches apart without the card signalling them. Starts as HDR10. Other captures: as <see cref="Auto"/>. <c>fmp capture</c>'s default.
    /// </summary>
    Follow = 4,
}

/// <summary>Manual colour overrides (null: from the V4L2 format fields).</summary>
public sealed record CaptureColorOverrides
{
    public HdrMode Hdr { get; init; } = HdrMode.TenBit;

    public ColorRange? Range { get; init; }

    public MatrixCoefficients? Matrix { get; init; }

    public ChromaSiting? Siting { get; init; }

    /// <summary>The HDR10 mastering peak sent when the frames are HDR10 (the card carries no metadata), in nits.</summary>
    public int PeakNits { get; init; } = ColorMetadata.DefaultPeakNits;

    /// <summary>MaxCLL sent with HDR10 frames (null: the peak).</summary>
    public int? MaxCll { get; init; }

    /// <summary>MaxFALL sent with HDR10 frames (0: not stated).</summary>
    public int MaxFall { get; init; }
}

/// <summary>The colour a capture resolved to, and where each part came from.</summary>
public sealed record CaptureColor
{
    /// <summary>The YCbCr description of the captured signal.</summary>
    public required MatrixCoefficients Matrix { get; init; }

    public required ColorRange Range { get; init; }

    public required ChromaSiting Siting { get; init; }

    /// <summary>What the frames' RGB pixels are (identity matrix, full range, the content's primaries and transfer).</summary>
    public required ColorMetadata FrameColor { get; init; }

    /// <summary>The V4L2 fields as the device reported them, by name ("sRGB / Rec. 709 / Rec. 709 / default").</summary>
    public required string Reported { get; init; }

    /// <summary>Where each choice came from, for the start-up lines.</summary>
    public required string Origin { get; init; }

    public bool IsHdr => FrameColor.IsHdr;
}

/// <summary>
/// Maps a V4L2 format's colour fields (colorspace, xfer_func, ycbcr_enc, quantization) to Core's description, resolving
/// the "default" values the way videodev2.h's V4L2_MAP_*_DEFAULT macros do, then applies the manual overrides. Pure.
/// </summary>
public static class CaptureColorMapping
{
    public static CaptureColor Resolve(uint colorspace, uint xferFunc, uint ycbcrEnc, uint quantization, int height, int bitDepth, CaptureColorOverrides? overrides = null)
    {
        overrides ??= new CaptureColorOverrides();
        if (overrides.Hdr == HdrMode.Follow)
        {
            var resolved = Resolve(colorspace, xferFunc, ycbcrEnc, quantization, height, bitDepth, overrides with { Hdr = bitDepth > 8 ? HdrMode.On : HdrMode.Auto });
            return bitDepth > 8 && xferFunc != V4l2Defs.XferFuncSmpte2084
                ? resolved with { Origin = resolved.Origin.Replace("HDR10 (forced: the device reports SDR)", "HDR10 to start, then following the signal: 10 significant bits are HDR10, 8 are SDR (the device reports SDR; --hdr on|off fixes it)", StringComparison.Ordinal) }
                : resolved;
        }

        if (overrides.Hdr == HdrMode.TenBit)
        {
            overrides = overrides with { Hdr = bitDepth > 8 ? HdrMode.On : HdrMode.Auto };
            var resolved = Resolve(colorspace, xferFunc, ycbcrEnc, quantization, height, bitDepth, overrides);
            return bitDepth > 8 && xferFunc != V4l2Defs.XferFuncSmpte2084
                ? resolved with { Origin = resolved.Origin.Replace("HDR10 (forced: the device reports SDR)", "HDR10 (a 10-bit capture is taken as HDR10: the device reports SDR; --hdr off overrides)", StringComparison.Ordinal) }
                : resolved;
        }

        var xfer = xferFunc == V4l2Defs.XferFuncDefault ? DefaultXfer(colorspace) : xferFunc;
        var enc = ycbcrEnc == V4l2Defs.YcbcrEncDefault ? DefaultEncoding(colorspace) : ycbcrEnc;

        // A YCbCr format (everything this source captures): default quantization is limited unless the colourspace is JPEG.
        var quant = quantization == V4l2Defs.QuantizationDefault
            ? (colorspace == V4l2Defs.ColorspaceJpeg ? V4l2Defs.QuantizationFullRange : V4l2Defs.QuantizationLimRange)
            : quantization;
        var reported = $"{ColorspaceName(colorspace)} / {XferName(xferFunc)} / {EncodingName(ycbcrEnc)} / {QuantizationName(quantization)}";

        var signalHdr = xfer == V4l2Defs.XferFuncSmpte2084;
        var hdr = overrides.Hdr switch
        {
            HdrMode.On => true,
            HdrMode.Off => false,
            _ => signalHdr,
        };
        var hdrOrigin = overrides.Hdr switch
        {
            HdrMode.On => signalHdr ? "HDR10 (forced; the device says so too)" : "HDR10 (forced: the device reports SDR)",
            HdrMode.Off => signalHdr ? "SDR (forced: the device reports PQ)" : "SDR (forced)",
            _ => signalHdr ? "HDR10 (the device reports SMPTE 2084)" : "SDR (the device reports no PQ; --hdr on overrides)",
        };

        var deviceMatrix = enc switch
        {
            V4l2Defs.YcbcrEnc709 or V4l2Defs.YcbcrEncXv709 => MatrixCoefficients.Bt709,
            V4l2Defs.YcbcrEncBt2020 or V4l2Defs.YcbcrEncBt2020ConstLum => MatrixCoefficients.Bt2020NonConstant,
            _ => MatrixCoefficients.Bt601,
        };
        MatrixCoefficients matrix;
        string matrixOrigin;
        if (overrides.Matrix is { } m)
        {
            matrix = m;
            matrixOrigin = "forced";
        }
        else if (overrides.Hdr == HdrMode.On && deviceMatrix != MatrixCoefficients.Bt2020NonConstant)
        {
            // HDR10 over HDMI is BT.2020 YCbCr; a device that does not signal HDR does not signal its matrix either.
            matrix = MatrixCoefficients.Bt2020NonConstant;
            matrixOrigin = "HDR10's (the device reports " + MatrixName(deviceMatrix) + ")";
        }
        else
        {
            matrix = deviceMatrix;
            matrixOrigin = "the device's";
        }

        var deviceRange = quant == V4l2Defs.QuantizationFullRange ? ColorRange.Full : ColorRange.Limited;
        var range = overrides.Range ?? deviceRange;
        var siting = overrides.Siting ?? ChromaSiting.Left;

        ColorMetadata color;
        if (hdr)
        {
            color = ColorMetadata.Hdr10WithPeak(overrides.PeakNits, overrides.MaxCll ?? overrides.PeakNits, overrides.MaxFall);
        }
        else
        {
            var primaries = colorspace switch
            {
                V4l2Defs.ColorspaceBt2020 => ColorPrimaries.Bt2020,
                V4l2Defs.ColorspaceSmpte170M or V4l2Defs.ColorspaceSmpte240M or V4l2Defs.Colorspace470SystemM => ColorPrimaries.Bt601Line525,
                V4l2Defs.Colorspace470SystemBg => ColorPrimaries.Bt601Line625,
                V4l2Defs.ColorspaceDciP3 => ColorPrimaries.DciP3,
                V4l2Defs.ColorspaceDefault => height >= 720 ? ColorPrimaries.Bt709 : ColorPrimaries.Bt601Line525,
                _ => ColorPrimaries.Bt709,
            };
            var transfer = xfer switch
            {
                V4l2Defs.XferFuncSrgb => TransferFunction.Srgb,
                V4l2Defs.XferFuncNone => TransferFunction.Linear,
                _ => TransferFunction.Bt709,
            };
            if (overrides.Hdr == HdrMode.Off && primaries == ColorPrimaries.Bt2020)
            {
                primaries = ColorPrimaries.Bt709;
            }

            color = new ColorMetadata { Primaries = primaries, Transfer = transfer, Matrix = MatrixCoefficients.Identity, Range = ColorRange.Full };
        }

        var origin = $"{hdrOrigin}; matrix {MatrixName(matrix)} ({matrixOrigin}); range {range} ({(overrides.Range is null ? "the device's" : "forced")}); " +
            $"chroma {siting} ({(overrides.Siting is null ? "assumed: V4L2 does not report it" : "forced")})";
        return new CaptureColor
        {
            Matrix = matrix,
            Range = range,
            Siting = siting,
            FrameColor = color,
            Reported = reported,
            Origin = origin,
        };
    }

    /// <summary>
    /// The two colours a capture can switch between while it runs (<see cref="HdrMode.Follow"/>, or by hand): the HDR10 one
    /// (PQ, BT.2020, the BT.2020 matrix unless forced) and the SDR one (as the device reports it, BT.709 for this card),
    /// with the same range, siting and metadata overrides.
    /// </summary>
    public static (CaptureColor Hdr, CaptureColor Sdr) ResolveBoth(uint colorspace, uint xferFunc, uint ycbcrEnc, uint quantization, int height, int bitDepth, CaptureColorOverrides? overrides = null)
    {
        overrides ??= new CaptureColorOverrides();
        return (
            Resolve(colorspace, xferFunc, ycbcrEnc, quantization, height, bitDepth, overrides with { Hdr = HdrMode.On }),
            Resolve(colorspace, xferFunc, ycbcrEnc, quantization, height, bitDepth, overrides with { Hdr = HdrMode.Off }));
    }

    /// <summary>"PQ (SMPTE ST 2084), BT.2020 primaries, limited range, 10-bit, BT.2020 matrix": what a capture's frames are taken to be.</summary>
    public static string SignalDescription(CaptureColor color, int bitDepth, bool subsampled420 = true) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{(color.FrameColor.IsPq ? "PQ (SMPTE ST 2084)" : "SDR gamma (BT.709)")}, {PrimariesName(color.FrameColor.Primaries)} primaries, " +
        $"{(color.Range == ColorRange.Full ? "full" : "limited")} range, {bitDepth}-bit YCbCr {(subsampled420 ? "4:2:0" : "4:2:2")} with the {MatrixName(color.Matrix)} matrix");

    private static string PrimariesName(ColorPrimaries p) => p switch
    {
        ColorPrimaries.Bt2020 => "BT.2020",
        ColorPrimaries.Bt709 => "BT.709",
        ColorPrimaries.Bt601Line525 => "BT.601 525-line",
        ColorPrimaries.Bt601Line625 => "BT.601 625-line",
        ColorPrimaries.DciP3 => "DCI-P3",
        _ => p.ToString(),
    };

    /// <summary>V4L2_MAP_XFER_FUNC_DEFAULT.</summary>
    internal static uint DefaultXfer(uint colorspace) => colorspace switch
    {
        V4l2Defs.ColorspaceOpRgb => V4l2Defs.XferFuncOpRgb,
        V4l2Defs.ColorspaceSmpte240M => V4l2Defs.XferFuncSmpte240M,
        V4l2Defs.ColorspaceDciP3 => V4l2Defs.XferFuncDciP3,
        V4l2Defs.ColorspaceRaw => V4l2Defs.XferFuncNone,
        V4l2Defs.ColorspaceSrgb or V4l2Defs.ColorspaceJpeg => V4l2Defs.XferFuncSrgb,
        _ => V4l2Defs.XferFunc709,
    };

    /// <summary>V4L2_MAP_YCBCR_ENC_DEFAULT.</summary>
    internal static uint DefaultEncoding(uint colorspace) => colorspace switch
    {
        V4l2Defs.ColorspaceRec709 or V4l2Defs.ColorspaceDciP3 => V4l2Defs.YcbcrEnc709,
        V4l2Defs.ColorspaceBt2020 => V4l2Defs.YcbcrEncBt2020,
        V4l2Defs.ColorspaceSmpte240M => V4l2Defs.YcbcrEncSmpte240M,
        _ => V4l2Defs.YcbcrEnc601,
    };

    public static string ColorspaceName(uint v) => v switch
    {
        V4l2Defs.ColorspaceDefault => "default",
        V4l2Defs.ColorspaceSmpte170M => "SMPTE 170M",
        V4l2Defs.ColorspaceSmpte240M => "SMPTE 240M",
        V4l2Defs.ColorspaceRec709 => "Rec. 709",
        V4l2Defs.Colorspace470SystemM => "470 System M",
        V4l2Defs.Colorspace470SystemBg => "470 System BG",
        V4l2Defs.ColorspaceJpeg => "JPEG",
        V4l2Defs.ColorspaceSrgb => "sRGB",
        V4l2Defs.ColorspaceOpRgb => "opRGB",
        V4l2Defs.ColorspaceBt2020 => "BT.2020",
        V4l2Defs.ColorspaceRaw => "raw",
        V4l2Defs.ColorspaceDciP3 => "DCI-P3",
        _ => $"colorspace {v}",
    };

    public static string XferName(uint v) => v switch
    {
        V4l2Defs.XferFuncDefault => "default",
        V4l2Defs.XferFunc709 => "Rec. 709",
        V4l2Defs.XferFuncSrgb => "sRGB",
        V4l2Defs.XferFuncOpRgb => "opRGB",
        V4l2Defs.XferFuncSmpte240M => "SMPTE 240M",
        V4l2Defs.XferFuncNone => "none",
        V4l2Defs.XferFuncDciP3 => "DCI-P3",
        V4l2Defs.XferFuncSmpte2084 => "SMPTE 2084",
        _ => $"xfer {v}",
    };

    public static string EncodingName(uint v) => v switch
    {
        V4l2Defs.YcbcrEncDefault => "default",
        V4l2Defs.YcbcrEnc601 => "BT.601",
        V4l2Defs.YcbcrEnc709 => "Rec. 709",
        V4l2Defs.YcbcrEncXv601 => "xvYCC 601",
        V4l2Defs.YcbcrEncXv709 => "xvYCC 709",
        V4l2Defs.YcbcrEncSycc => "sYCC",
        V4l2Defs.YcbcrEncBt2020 => "BT.2020",
        V4l2Defs.YcbcrEncBt2020ConstLum => "BT.2020 constant luminance",
        V4l2Defs.YcbcrEncSmpte240M => "SMPTE 240M",
        _ => $"encoding {v}",
    };

    public static string QuantizationName(uint v) => v switch
    {
        V4l2Defs.QuantizationDefault => "default quantization",
        V4l2Defs.QuantizationFullRange => "full range",
        V4l2Defs.QuantizationLimRange => "limited range",
        _ => $"quantization {v}",
    };

    private static string MatrixName(MatrixCoefficients m) => m switch
    {
        MatrixCoefficients.Bt601 => "BT.601",
        MatrixCoefficients.Bt709 => "BT.709",
        MatrixCoefficients.Bt2020NonConstant => "BT.2020",
        _ => m.ToString(),
    };
}
