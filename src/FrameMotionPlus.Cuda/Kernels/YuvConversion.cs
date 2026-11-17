using System.Numerics;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda.Kernels;

/// <summary>How the samples of a 4:2:0 YCbCr image are stored: a luma plane and one interleaved CbCr plane.</summary>
public enum YuvSampleFormat
{
    Unknown = 0,

    /// <summary>8-bit samples (NV12).</summary>
    Nv12 = 1,

    /// <summary>16-bit little-endian samples with 10 significant bits in the high bits (P010).</summary>
    P010 = 2,
}

/// <summary>
/// Where 4:2:0 chroma samples sit relative to luma: ITU-T H.273 <c>chroma_sample_loc_type</c> 0 to 5 (FFmpeg's
/// <c>AVCHROMA_LOC_*</c> is one more).
/// </summary>
public enum ChromaSiting
{
    /// <summary>Type 0: co-sited with even luma columns, halfway between rows (MPEG-2, H.264 and HEVC's default).</summary>
    Left = 0,

    /// <summary>Type 1: halfway between columns and rows (MPEG-1, JPEG).</summary>
    Center = 1,

    /// <summary>Type 2: co-sited with the even luma column and row (BT.2020/BT.2100 UHD and most HDR10).</summary>
    TopLeft = 2,

    /// <summary>Type 3: halfway between columns, co-sited with even rows.</summary>
    Top = 3,

    /// <summary>Type 4: co-sited with even columns and odd rows.</summary>
    BottomLeft = 4,

    /// <summary>Type 5: halfway between columns, co-sited with odd rows.</summary>
    Bottom = 5,
}

/// <summary>A 4:2:0 YCbCr image in CUDA device memory: a luma plane and an interleaved CbCr plane, each pitch-linear.</summary>
public readonly record struct YuvPlanes
{
    public ulong Luma { get; init; }

    public int LumaPitch { get; init; }

    public ulong Chroma { get; init; }

    public int ChromaPitch { get; init; }

    /// <summary>Visible width in luma samples (the chroma plane holds ⌈width / 2⌉ CbCr pairs per row).</summary>
    public int Width { get; init; }

    /// <summary>Visible height in luma rows (the chroma plane holds ⌈height / 2⌉ rows).</summary>
    public int Height { get; init; }

    public YuvSampleFormat Format { get; init; }

    public int BytesPerSample => Format == YuvSampleFormat.P010 ? 2 : 1;
}

/// <summary>
/// One YCbCr-to-RGB conversion (native/kernels/yuv.cu): matrix, range, source bit depth, chroma siting and output
/// format, with the kernel's integer coefficients derived once from the standards here. Each output channel times the
/// output maximum M is an exact fraction of the code values: with A = Y − yOffset and Q = 16·C − cOffset16 (C the
/// interpolated chroma, kept in 16ths),
/// <code>
/// R·den = cy·A + crR·Qr      G·den = cy·A + crG·Qr + cbG·Qb      B·den = cy·A + cbB·Qb
/// </code>
/// from H.273's inverse equations R' = Y' + 2(1 − Kr)·Cr, B' = Y' + 2(1 − Kb)·Cb, G' = (Y' − Kr·R' − Kb·B') / Kg, with
/// Y' = A / yDen and C = Q / (16·cDen) (limited range: yDen = 219·2^(n−8), cDen = 224·2^(n−8), offsets 16 and 128 scaled
/// the same; full range: yDen = cDen = 2^n − 1, offsets 0 and 2^(n−1)). The kernel rounds half up and clamps. Kr and Kb
/// are the standards' exact decimals: BT.601 0.299 / 0.114, BT.709 0.2126 / 0.0722, BT.2020 0.2627 / 0.0593.
/// Allocation-free to use; create it once per stream configuration.
/// </summary>
public readonly struct YuvConversion
{
    private YuvConversion(YuvKernelParameters parameters, MatrixCoefficients matrix, ColorRange range, int sourceBits, ChromaSiting siting, PixelFormat output)
    {
        Parameters = parameters;
        Matrix = matrix;
        Range = range;
        SourceBits = sourceBits;
        Siting = siting;
        Output = output;
    }

    public MatrixCoefficients Matrix { get; }

    public ColorRange Range { get; }

    /// <summary>8 (NV12) or 10 (P010).</summary>
    public int SourceBits { get; }

    public ChromaSiting Siting { get; }

    /// <summary><see cref="PixelFormat.Rgba8"/> or <see cref="PixelFormat.Rgb10A2"/>.</summary>
    public PixelFormat Output { get; }

    public bool IsValid => Output != PixelFormat.Unknown;

    /// <summary>The kernel's parameter block (tests compare it with an independent derivation).</summary>
    internal YuvKernelParameters Parameters { get; }

    /// <summary>
    /// The conversion for <paramref name="matrix"/> (BT.601, BT.709 or BT.2020 non-constant luminance) and
    /// <paramref name="range"/> (limited or full) of <paramref name="sourceBits"/>-bit samples (8 or 10) sited at
    /// <paramref name="siting"/>, into <paramref name="output"/> (RGBA8 or RGB10A2; code values, full range, no transfer
    /// conversion).
    /// </summary>
    public static YuvConversion Create(MatrixCoefficients matrix, ColorRange range, int sourceBits, ChromaSiting siting, PixelFormat output)
    {
        var (kr, kb) = matrix switch
        {
            MatrixCoefficients.Bt601 => (2990, 1140),
            MatrixCoefficients.Bt709 => (2126, 722),
            MatrixCoefficients.Bt2020NonConstant => (2627, 593),
            _ => throw new ArgumentException($"No YCbCr conversion for matrix {matrix}: BT.601, BT.709 and BT.2020 non-constant luminance are supported.", nameof(matrix)),
        };
        if (range is not (ColorRange.Limited or ColorRange.Full))
        {
            throw new ArgumentException($"The range must be limited or full, not {range}.", nameof(range));
        }

        if (sourceBits is not (8 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBits), sourceBits, "8-bit (NV12) and 10-bit (P010) sources are supported.");
        }

        if (!Enum.IsDefined(siting))
        {
            throw new ArgumentOutOfRangeException(nameof(siting), siting, "Unknown chroma siting.");
        }

        if (output is not (PixelFormat.Rgba8 or PixelFormat.Rgb10A2))
        {
            throw new ArgumentException($"The output must be RGBA8 or RGB10A2, not {output}.", nameof(output));
        }

        const int unit = 10_000;
        var kg = unit - kr - kb;
        Int128 maxOut = output == PixelFormat.Rgb10A2 ? 1023 : 255;
        var scale = 1 << (sourceBits - 8);
        Int128 yDen, yOffset, cDen16, cOffset16;
        if (range == ColorRange.Limited)
        {
            (yDen, yOffset) = (219 * scale, 16 * scale);
            (cDen16, cOffset16) = (16 * 224 * scale, 16 * 128 * scale);
        }
        else
        {
            var max = (1 << sourceBits) - 1;
            (yDen, yOffset) = (max, 0);
            (cDen16, cOffset16) = (16 * max, 16 * (1 << (sourceBits - 1)));
        }

        // Over the common denominator yDen·cDen16·10000·Kg (see the remarks), times M.
        Int128[] c =
        [
            maxOut * cDen16 * unit * kg,                 // cy
            maxOut * yDen * 2 * (unit - kr) * kg,        // crR
            maxOut * yDen * 2 * (unit - kb) * kg,        // cbB
            -maxOut * yDen * 2 * kr * (unit - kr),       // crG
            -maxOut * yDen * 2 * kb * (unit - kb),       // cbG
            yDen * cDen16 * unit * kg,                   // den
        ];
        var divisor = BigInteger.Zero;
        foreach (var v in c)
        {
            divisor = BigInteger.GreatestCommonDivisor(divisor, (BigInteger)v);
        }

        var g = (Int128)divisor;
        for (var i = 0; i < c.Length; i++)
        {
            c[i] /= g;
        }

        // The kernel computes 2n + den in signed 64-bit integers: bound |n| over every code value.
        Int128 maxA = (1 << sourceBits) - 1;
        var maxQ = Int128.Max(16 * maxA - cOffset16, cOffset16);
        var worst = (Int128.Abs(c[0]) * maxA) + (maxQ * Int128.Max(Int128.Abs(c[1]), Int128.Max(Int128.Abs(c[2]), Int128.Abs(c[3]) + Int128.Abs(c[4]))));
        if ((2 * worst) + c[5] >= long.MaxValue)
        {
            throw new InvalidOperationException("The conversion's integer coefficients would overflow the kernel's 64-bit arithmetic.");
        }

        var (chromaX, chromaY) = siting switch
        {
            ChromaSiting.Left => (0, 1),
            ChromaSiting.Center => (1, 1),
            ChromaSiting.TopLeft => (0, 0),
            ChromaSiting.Top => (1, 0),
            ChromaSiting.BottomLeft => (0, 2),
            _ => (1, 2),
        };
        var parameters = new YuvKernelParameters
        {
            Cy = (long)c[0],
            CrR = (long)c[1],
            CbB = (long)c[2],
            CrG = (long)c[3],
            CbG = (long)c[4],
            Den = (long)c[5],
            YOffset = (long)yOffset,
            COffset16 = (long)cOffset16,
            MaxOut = (long)maxOut,
            ChromaX = chromaX,
            ChromaY = chromaY,
            Sixteen = sourceBits > 8 ? 1 : 0,
            Shift = sourceBits > 8 ? 16 - sourceBits : 0,
            Out10 = output == PixelFormat.Rgb10A2 ? 1 : 0,
        };
        return new YuvConversion(parameters, matrix, range, sourceBits, siting, output);
    }

    public override string ToString() => $"{Matrix} {Range} {SourceBits}-bit, chroma {Siting}, to {Output}";
}

/// <summary>The parameter block of <c>fmp_yuv420_to_rgb</c> (native/kernels/yuv.cu, <c>struct fmp_yuv_params</c>): 14 64-bit fields.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct YuvKernelParameters
{
    public long Cy { get; init; }

    public long CrR { get; init; }

    public long CbB { get; init; }

    public long CrG { get; init; }

    public long CbG { get; init; }

    public long Den { get; init; }

    public long YOffset { get; init; }

    public long COffset16 { get; init; }

    public long MaxOut { get; init; }

    public long ChromaX { get; init; }

    public long ChromaY { get; init; }

    public long Sixteen { get; init; }

    public long Shift { get; init; }

    public long Out10 { get; init; }
}
