// Format conversion (pack) between the ring formats. Plain same-format copies use cuMemcpy2DAsync instead.
// Every conversion rounds to nearest; none of the divisions below can produce an exact tie.
//   8 to 10 bit:  c10 = (c8 * 1023 + 127) / 255     a2 = (a8 * 3 + 127) / 255
//   10 to 8 bit:  c8 = (c10 * 255 + 511) / 1023     a8 = a2 * 85
#include "fmp_cuda.h"

// srcBgra != 0 reads BGRA8 instead of RGBA8.
FMP_KERNEL void fmp_rgba8_to_rgb10a2(const fmp_u8* src, fmp_u32 srcPitch, fmp_u8* dst, fmp_u32 dstPitch,
                                     int width, int height, int srcBgra)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const fmp_u32 p = fmp_row(src, srcPitch, y)[x];
    const fmp_u32 c0 = p & 0xFFu;
    const fmp_u32 g = (p >> 8) & 0xFFu;
    const fmp_u32 c2 = (p >> 16) & 0xFFu;
    const fmp_u32 a = p >> 24;
    const fmp_u32 r = srcBgra ? c2 : c0;
    const fmp_u32 b = srcBgra ? c0 : c2;
    fmp_row_mut(dst, dstPitch, y)[x] = fmp_pack10(
        (r * 1023u + 127u) / 255u, (g * 1023u + 127u) / 255u, (b * 1023u + 127u) / 255u, (a * 3u + 127u) / 255u);
}

// dstBgra != 0 writes BGRA8 instead of RGBA8.
FMP_KERNEL void fmp_rgb10a2_to_rgba8(const fmp_u8* src, fmp_u32 srcPitch, fmp_u8* dst, fmp_u32 dstPitch,
                                     int width, int height, int dstBgra)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const fmp_u32 p = fmp_row(src, srcPitch, y)[x];
    const fmp_u32 r = ((p & 0x3FFu) * 255u + 511u) / 1023u;
    const fmp_u32 g = (((p >> 10) & 0x3FFu) * 255u + 511u) / 1023u;
    const fmp_u32 b = (((p >> 20) & 0x3FFu) * 255u + 511u) / 1023u;
    const fmp_u32 a = (p >> 30) * 85u;
    fmp_row_mut(dst, dstPitch, y)[x] = dstBgra ? fmp_pack8(b, g, r, a) : fmp_pack8(r, g, b, a);
}

// RGBA8 <-> BGRA8.
FMP_KERNEL void fmp_swap_red_blue8(const fmp_u8* src, fmp_u32 srcPitch, fmp_u8* dst, fmp_u32 dstPitch,
                                   int width, int height)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const fmp_u32 p = fmp_row(src, srcPitch, y)[x];
    fmp_row_mut(dst, dstPitch, y)[x] = (p & 0xFF00FF00u) | ((p & 0xFFu) << 16) | ((p >> 16) & 0xFFu);
}
