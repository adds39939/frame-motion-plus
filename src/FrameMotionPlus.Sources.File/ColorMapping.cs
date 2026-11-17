using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File;

/// <summary>
/// The colour description of a stream or frame as FFmpeg reports it (H.273 code points, FFmpeg numbering), before
/// defaults are applied.
/// </summary>
public readonly record struct RawColorDescription(int Primaries, int Transfer, int Matrix, int Range, int ChromaLocation)
{
    public override string ToString() =>
        $"primaries {ColorMapping.PrimariesName(Primaries)}, transfer {ColorMapping.TransferName(Transfer)}, matrix {ColorMapping.MatrixName(Matrix)}, " +
        $"range {ColorMapping.RangeName(Range)}, chroma {ColorMapping.ChromaLocationName(ChromaLocation)}";
}

/// <summary>
/// FFmpeg's colour fields to Core's <see cref="ColorMetadata"/> and the convert kernel's settings, with the defaults for
/// what a stream leaves unspecified. Transfer characteristics are carried, never converted (HLG to PQ and tone mapping
/// are Phase 7). Allocation-free.
/// </summary>
public static class ColorMapping
{
    /// <summary>
    /// The matrix to convert with: the stream's, or for an unspecified one BT.2020 non-constant luminance when the
    /// primaries are BT.2020, BT.601 for standard definition (height ≤ 576) and BT.709 otherwise (HD SDR). Null for a
    /// matrix the converter does not implement (BT.2020 constant luminance, RGB, YCgCo, SMPTE 240M, ...).
    /// </summary>
    public static MatrixCoefficients? Matrix(in RawColorDescription raw, int height) => raw.Matrix switch
    {
        Av.ColorSpaceBt709 => MatrixCoefficients.Bt709,
        Av.ColorSpaceBt470Bg or Av.ColorSpaceSmpte170M => MatrixCoefficients.Bt601,
        Av.ColorSpaceBt2020Ncl => MatrixCoefficients.Bt2020NonConstant,
        Av.ColorSpaceUnspecified or Av.ColorSpaceReserved => raw.Primaries == Av.ColorPrimariesBt2020 ? MatrixCoefficients.Bt2020NonConstant
            : height <= 576 ? MatrixCoefficients.Bt601 : MatrixCoefficients.Bt709,
        _ => null,
    };

    /// <summary>Limited unless the stream says full (JPEG) range.</summary>
    public static ColorRange Range(in RawColorDescription raw) => raw.Range == Av.ColorRangeJpeg ? ColorRange.Full : ColorRange.Limited;

    /// <summary>
    /// The stream's primaries in Core's terms; for unspecified ones, those its matrix implies for HD (BT.709) and UHD
    /// (BT.2020). Unspecified standard-definition primaries stay <see cref="ColorPrimaries.Unspecified"/>: BT.601 has two
    /// sets (525 and 625 lines) and the matrix does not say which.
    /// </summary>
    public static ColorPrimaries Primaries(in RawColorDescription raw, MatrixCoefficients matrix) => raw.Primaries switch
    {
        Av.ColorPrimariesBt709 => ColorPrimaries.Bt709,
        Av.ColorPrimariesBt2020 => ColorPrimaries.Bt2020,
        Av.ColorPrimariesSmpte431 => ColorPrimaries.DciP3,
        Av.ColorPrimariesSmpte432 => ColorPrimaries.DisplayP3,
        Av.ColorPrimariesSmpte170M or Av.ColorPrimariesSmpte240M => ColorPrimaries.Bt601Line525,
        Av.ColorPrimariesBt470Bg => ColorPrimaries.Bt601Line625,
        Av.ColorPrimariesUnspecified => matrix switch
        {
            MatrixCoefficients.Bt709 => ColorPrimaries.Bt709,
            MatrixCoefficients.Bt2020NonConstant => ColorPrimaries.Bt2020,
            _ => ColorPrimaries.Unspecified,
        },
        _ => ColorPrimaries.Unspecified,
    };

    /// <summary>The transfer characteristics; unspecified SDR is taken as BT.709 (the SDR curves share it).</summary>
    public static TransferFunction Transfer(in RawColorDescription raw) => raw.Transfer switch
    {
        Av.TransferSmpte2084 => TransferFunction.Pq,
        Av.TransferAribStdB67 => TransferFunction.Hlg,
        Av.TransferIec61966_2_1 => TransferFunction.Srgb,
        Av.TransferLinear => TransferFunction.Linear,
        _ => TransferFunction.Bt709,
    };

    /// <summary>The chroma siting; unspecified is H.264/HEVC's default, left (type 0).</summary>
    public static ChromaSiting Siting(in RawColorDescription raw) => raw.ChromaLocation switch
    {
        Av.ChromaLocationCenter => ChromaSiting.Center,
        Av.ChromaLocationTopLeft => ChromaSiting.TopLeft,
        Av.ChromaLocationTop => ChromaSiting.Top,
        Av.ChromaLocationBottomLeft => ChromaSiting.BottomLeft,
        Av.ChromaLocationBottom => ChromaSiting.Bottom,
        _ => ChromaSiting.Left,
    };

    /// <summary>
    /// Core's description of the RGB frames converted from a stream described by <paramref name="raw"/> (whose YCbCr
    /// matrix is <paramref name="matrix"/>): the pixels are RGB at full range (<see cref="MatrixCoefficients.Identity"/>,
    /// <see cref="ColorRange.Full"/>, the conversion's output), with the stream's primaries, transfer function and HDR
    /// metadata. The matrix and range converted from stay in the stream's description (<see cref="FileStreamInfo.Conversion"/>).
    /// </summary>
    public static ColorMetadata ToColorMetadata(in RawColorDescription raw, MatrixCoefficients matrix, in MasteringDisplay mastering, in ContentLightLevel lightLevel) => new()
    {
        Primaries = Primaries(raw, matrix),
        Transfer = Transfer(raw),
        Matrix = MatrixCoefficients.Identity,
        Range = ColorRange.Full,
        Mastering = mastering,
        LightLevel = lightLevel,
    };

    internal static unsafe MasteringDisplay Mastering(AVMasteringDisplayMetadata* m)
    {
        if (m == null)
        {
            return default;
        }

        static float Q(int num, int den) => den == 0 ? 0f : (float)num / den;
        var result = default(MasteringDisplay);
        if (m->HasPrimaries != 0)
        {
            var p = m->DisplayPrimaries;
            var w = m->WhitePoint;
            result = result with
            {
                Red = new Chromaticity(Q(p[0], p[1]), Q(p[2], p[3])),
                Green = new Chromaticity(Q(p[4], p[5]), Q(p[6], p[7])),
                Blue = new Chromaticity(Q(p[8], p[9]), Q(p[10], p[11])),
                WhitePoint = new Chromaticity(Q(w[0], w[1]), Q(w[2], w[3])),
            };
        }

        if (m->HasLuminance != 0)
        {
            result = result with
            {
                MaxLuminanceNits = Q(m->MaxLuminance.Num, m->MaxLuminance.Den),
                MinLuminanceNits = Q(m->MinLuminance.Num, m->MinLuminance.Den),
            };
        }

        return result;
    }

    internal static unsafe ContentLightLevel LightLevel(AVContentLightMetadata* c) =>
        c == null ? default : new ContentLightLevel((ushort)Math.Min(c->MaxCll, ushort.MaxValue), (ushort)Math.Min(c->MaxFall, ushort.MaxValue));

    public static string PrimariesName(int value) => value switch
    {
        1 => "bt709", 2 => "unspecified", 4 => "bt470m", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m", 8 => "film",
        9 => "bt2020", 10 => "smpte428", 11 => "smpte431 (DCI-P3)", 12 => "smpte432 (Display P3)", 22 => "ebu3213", _ => $"reserved ({value})",
    };

    public static string TransferName(int value) => value switch
    {
        1 => "bt709", 2 => "unspecified", 4 => "gamma22", 5 => "gamma28", 6 => "smpte170m", 7 => "smpte240m", 8 => "linear",
        9 => "log100", 10 => "log316", 11 => "iec61966-2-4", 12 => "bt1361e", 13 => "iec61966-2-1 (sRGB)", 14 => "bt2020-10",
        15 => "bt2020-12", 16 => "smpte2084 (PQ)", 17 => "smpte428", 18 => "arib-std-b67 (HLG)", _ => $"reserved ({value})",
    };

    public static string MatrixName(int value) => value switch
    {
        0 => "rgb", 1 => "bt709", 2 => "unspecified", 4 => "fcc", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m", 8 => "ycgco",
        9 => "bt2020nc", 10 => "bt2020c", 11 => "smpte2085", 14 => "ictcp", _ => $"reserved ({value})",
    };

    public static string RangeName(int value) => value switch
    {
        1 => "limited (tv)", 2 => "full (pc)", _ => "unspecified",
    };

    public static string ChromaLocationName(int value) => value switch
    {
        1 => "left", 2 => "center", 3 => "topleft", 4 => "top", 5 => "bottomleft", 6 => "bottom", _ => "unspecified",
    };
}
