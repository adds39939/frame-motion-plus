// Linear blend out = (1 - t) * prev + t * curr on every channel, alpha included. The host passes
// weight = round(t * 65536) in [0, 65536]; each channel is then rounded half up:
//   out = ((65536 - weight) * prev + weight * curr + 32768) >> 16
// which is exactly round-half-up of the real blend for the quantised t, and returns prev at weight 0 and curr at
// weight 65536. Channel order does not matter, so the 8-bit kernel serves RGBA8 and BGRA8 alike.
#include "fmp_cuda.h"

FMP_INLINE fmp_u32 fmp_mix(fmp_u32 a, fmp_u32 b, fmp_u32 weight)
{
    return ((65536u - weight) * a + weight * b + 32768u) >> 16;
}

FMP_KERNEL void fmp_blend_rgba8(const fmp_u8* prev, fmp_u32 prevPitch, const fmp_u8* curr, fmp_u32 currPitch,
                                fmp_u8* dst, fmp_u32 dstPitch, int width, int height, fmp_u32 weight)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const fmp_u32 a = fmp_row(prev, prevPitch, y)[x];
    const fmp_u32 b = fmp_row(curr, currPitch, y)[x];
    fmp_row_mut(dst, dstPitch, y)[x] = fmp_pack8(
        fmp_mix(a & 0xFFu, b & 0xFFu, weight),
        fmp_mix((a >> 8) & 0xFFu, (b >> 8) & 0xFFu, weight),
        fmp_mix((a >> 16) & 0xFFu, (b >> 16) & 0xFFu, weight),
        fmp_mix(a >> 24, b >> 24, weight));
}

FMP_KERNEL void fmp_blend_rgb10a2(const fmp_u8* prev, fmp_u32 prevPitch, const fmp_u8* curr, fmp_u32 currPitch,
                                  fmp_u8* dst, fmp_u32 dstPitch, int width, int height, fmp_u32 weight)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    const fmp_u32 a = fmp_row(prev, prevPitch, y)[x];
    const fmp_u32 b = fmp_row(curr, currPitch, y)[x];
    fmp_row_mut(dst, dstPitch, y)[x] = fmp_pack10(
        fmp_mix(a & 0x3FFu, b & 0x3FFu, weight),
        fmp_mix((a >> 10) & 0x3FFu, (b >> 10) & 0x3FFu, weight),
        fmp_mix((a >> 20) & 0x3FFu, (b >> 20) & 0x3FFu, weight),
        fmp_mix(a >> 30, b >> 30, weight));
}
