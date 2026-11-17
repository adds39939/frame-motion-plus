// 4:2:0 YCbCr (NV12, or P010 with 10 significant bits in the high bits of each 16-bit sample) to pitch-linear RGB
// (RGBA8 or RGB10A2), as the decoder delivers it and the generators take it. One pass per output pixel:
//
// Chroma upsampling, once, bilinear, honouring the chroma siting (ITU-T H.273 chroma_sample_loc_type). A luma pixel
// at column x sits at chroma column (x - ox) / 2, where ox = 0 when chroma is co-sited with even luma columns (Left,
// TopLeft, BottomLeft) and 1/2 when it lies between them (Center, Top, Bottom); vertically (y - oy) / 2 with oy = 0
// (TopLeft, Top), 1/2 (Left, Center) or 1 (BottomLeft, Bottom). In quarter chroma samples that is 2x - 2ox, so each
// axis interpolates between two neighbours with weights (4 - f, f) / 4, f = 0..3; edges repeat the outermost sample.
// The interpolated chroma is kept exact as a sum over 16ths.
//
// Matrix and range: H.273 / BT.601, BT.709, BT.2020 non-constant luminance, limited or full range. With
// A = Y - yOffset, Q = 16 C - cOffset16 (C interpolated) the inverse matrix gives each of R', G', B' times the output
// maximum M as an exact fraction n / den with integer coefficients (YuvConversion derives them on the host from Kr and
// Kb); the kernel computes n in 64-bit integers and rounds half up, round(n / den) = floor((2n + den) / 2den), clamped
// to [0, M]. No floating point and no approximation, so a CPU reference that computes the same exact values from the
// standards' equations matches bit for bit. Transfer characteristics are not touched: SDR and PQ code values pass
// through as they are.
#include "fmp_cuda.h"

typedef long long fmp_i64;
typedef unsigned short fmp_u16;

// Mirrors FrameMotionPlus.Cuda.Kernels.YuvConversion's parameter block: 64-bit fields only, so the layout is plain.
struct fmp_yuv_params
{
    fmp_i64 cy;        // on A, for every channel
    fmp_i64 crR;       // on Qr, for R
    fmp_i64 cbB;       // on Qb, for B
    fmp_i64 crG;       // on Qr, for G
    fmp_i64 cbG;       // on Qb, for G
    fmp_i64 den;       // the common denominator (> 0)
    fmp_i64 yOffset;   // luma code of black: 16 << (n - 8) limited, 0 full
    fmp_i64 cOffset16; // 16 x the chroma code of zero: 16 x 128 << (n - 8) limited, 16 x 2^(n - 1) full
    fmp_i64 maxOut;    // 255 or 1023
    fmp_i64 chromaX;   // 2 ox: 0 or 1
    fmp_i64 chromaY;   // 2 oy: 0, 1 or 2
    fmp_i64 sixteen;   // 1: 16-bit samples (P010), 0: 8-bit (NV12)
    fmp_i64 shift;     // right shift of a 16-bit sample to its code value (6 for P010)
    fmp_i64 out10;     // 1: RGB10A2, 0: RGBA8
};

FMP_INLINE int fmp_floor4(int v)
{
    return v >= 0 ? v / 4 : -((3 - v) / 4);
}

FMP_INLINE int fmp_clamp(int v, int high)
{
    return v < 0 ? 0 : (v > high ? high : v);
}

FMP_INLINE fmp_i64 fmp_luma(const fmp_u8* row, int x, int sixteen, int shift)
{
    return sixteen ? (fmp_i64)(((const fmp_u16*)row)[x] >> shift) : (fmp_i64)row[x];
}

// Component c (0 = Cb, 1 = Cr) of interleaved chroma sample i.
FMP_INLINE fmp_i64 fmp_chroma(const fmp_u8* row, int i, int c, int sixteen, int shift)
{
    return sixteen ? (fmp_i64)(((const fmp_u16*)row)[2 * i + c] >> shift) : (fmp_i64)row[2 * i + c];
}

FMP_INLINE fmp_u32 fmp_round(fmp_i64 n, fmp_i64 den, fmp_i64 maxOut)
{
    const fmp_i64 twice = 2 * n + den;
    if (twice < 0)
    {
        return 0;
    }

    const fmp_i64 q = twice / (2 * den);
    return (fmp_u32)(q > maxOut ? maxOut : q);
}

FMP_KERNEL void fmp_yuv420_to_rgb(const fmp_u8* luma, fmp_u32 lumaPitch, const fmp_u8* chroma, fmp_u32 chromaPitch,
                                  fmp_u8* dst, fmp_u32 dstPitch, int width, int height, struct fmp_yuv_params p)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const int sixteen = (int)p.sixteen;
    const int shift = (int)p.shift;
    const fmp_i64 a = fmp_luma(luma + (fmp_u64)lumaPitch * (fmp_u64)y, x, sixteen, shift) - p.yOffset;

    const int lastColumn = ((width + 1) >> 1) - 1;
    const int lastRow = ((height + 1) >> 1) - 1;
    const int cx4 = 2 * x - (int)p.chromaX;
    const int cy4 = 2 * y - (int)p.chromaY;
    const int i0 = fmp_floor4(cx4);
    const int j0 = fmp_floor4(cy4);
    const fmp_i64 fx = cx4 - 4 * i0;
    const fmp_i64 fy = cy4 - 4 * j0;
    const int ia = fmp_clamp(i0, lastColumn);
    const int ib = fmp_clamp(i0 + 1, lastColumn);
    const fmp_u8* rowA = chroma + (fmp_u64)chromaPitch * (fmp_u64)fmp_clamp(j0, lastRow);
    const fmp_u8* rowB = chroma + (fmp_u64)chromaPitch * (fmp_u64)fmp_clamp(j0 + 1, lastRow);
    const fmp_i64 w00 = (4 - fx) * (4 - fy);
    const fmp_i64 w10 = fx * (4 - fy);
    const fmp_i64 w01 = (4 - fx) * fy;
    const fmp_i64 w11 = fx * fy;

    const fmp_i64 cb = w00 * fmp_chroma(rowA, ia, 0, sixteen, shift) + w10 * fmp_chroma(rowA, ib, 0, sixteen, shift) +
                       w01 * fmp_chroma(rowB, ia, 0, sixteen, shift) + w11 * fmp_chroma(rowB, ib, 0, sixteen, shift);
    const fmp_i64 cr = w00 * fmp_chroma(rowA, ia, 1, sixteen, shift) + w10 * fmp_chroma(rowA, ib, 1, sixteen, shift) +
                       w01 * fmp_chroma(rowB, ia, 1, sixteen, shift) + w11 * fmp_chroma(rowB, ib, 1, sixteen, shift);
    const fmp_i64 qb = cb - p.cOffset16;
    const fmp_i64 qr = cr - p.cOffset16;

    const fmp_i64 luminance = p.cy * a;
    const fmp_u32 r = fmp_round(luminance + p.crR * qr, p.den, p.maxOut);
    const fmp_u32 g = fmp_round(luminance + p.crG * qr + p.cbG * qb, p.den, p.maxOut);
    const fmp_u32 b = fmp_round(luminance + p.cbB * qb, p.den, p.maxOut);
    fmp_row_mut(dst, dstPitch, y)[x] = p.out10 ? fmp_pack10(r, g, b, 3u) : fmp_pack8(r, g, b, 255u);
}
