// Test pattern. Every value is integer arithmetic on (x, y, width, height, frame), so the CPU reference in
// tests/FrameMotionPlus.Cuda.Tests reproduces it bit for bit. Bands, top to bottom (H = height, W = width):
//
//   [0, 7H/12)        eight colour bars, bar = 8x/W: white, yellow, cyan, green, magenta, red, blue, black
//   [7H/12, 9H/12)    a box max(W/16, 1) wide over a dark background, moving max(W/240, 1) px per frame and
//                     wrapping after W - boxWidth px
//   [9H/12, 10H/12)   8-bit: grey ramp 0..255 across the width.
//                     10-bit: upper half PQ code ramp 0..1023; lower half ten luminance steps, step = 10x/W
//   [10H/12, H)       frame counter: 32 cells W/32 wide, most significant bit first; a set bit is full scale
//
// 8-bit levels: bars 191 (75 %), box 255, background 48. 10-bit levels are full-range SMPTE ST 2084 (PQ) codes:
// bars 594 (203 nits, BT.2408 reference white), box 769 (1000 nits), background 307 (10 nits).
// Alpha is opaque in both formats.
#include "fmp_cuda.h"

#define FMP_BAR8 191u
#define FMP_BOX8 255u
#define FMP_BACKGROUND8 48u

#define FMP_BAR10 594u
#define FMP_BOX10 769u
#define FMP_BACKGROUND10 307u

// PQ codes of 0, 0.1, 1, 10, 100, 203, 400, 1000, 4000 and 10000 nits.
FMP_INLINE fmp_u32 fmp_pq_step(int index)
{
    switch (index)
    {
    case 0: return 0u;
    case 1: return 64u;
    case 2: return 153u;
    case 3: return 307u;
    case 4: return 520u;
    case 5: return 594u;
    case 6: return 668u;
    case 7: return 769u;
    case 8: return 923u;
    default: return 1023u;
    }
}

// Writes the colour of pixel (x, y) at scale maxCode (255 or 1023) into r, g, b.
FMP_INLINE void fmp_pattern_pixel(int x, int y, int width, int height, fmp_u32 frame, bool tenBit,
                                  fmp_u32* r, fmp_u32* g, fmp_u32* b)
{
    const fmp_u32 maxCode = tenBit ? 1023u : 255u;
    const int barsEnd = height * 7 / 12;
    const int boxEnd = height * 9 / 12;
    const int rampEnd = height * 10 / 12;

    if (y < barsEnd)
    {
        // Nibble k is bar k's channel mask (4 = red, 2 = green, 1 = blue): 7 6 3 2 5 4 1 0 from bar 0.
        const fmp_u32 masks = 0x01452367u;
        const int bar = x * 8 / width;
        const fmp_u32 mask = (masks >> (bar * 4)) & 0x7u;
        const fmp_u32 level = tenBit ? FMP_BAR10 : FMP_BAR8;
        *r = (mask & 4u) ? level : 0u;
        *g = (mask & 2u) ? level : 0u;
        *b = (mask & 1u) ? level : 0u;
        return;
    }

    if (y < boxEnd)
    {
        int boxWidth = width / 16;
        boxWidth = boxWidth < 1 ? 1 : boxWidth;
        int step = width / 240;
        step = step < 1 ? 1 : step;
        const fmp_u32 travel = (fmp_u32)(width - boxWidth);
        const int boxX = travel == 0u ? 0 : (int)(((frame % travel) * (fmp_u32)step) % travel);
        const bool inBox = x >= boxX && x < boxX + boxWidth;
        const fmp_u32 v = inBox ? (tenBit ? FMP_BOX10 : FMP_BOX8) : (tenBit ? FMP_BACKGROUND10 : FMP_BACKGROUND8);
        *r = v;
        *g = v;
        *b = v;
        return;
    }

    if (y < rampEnd)
    {
        fmp_u32 v;
        if (tenBit && y >= (boxEnd + rampEnd) / 2)
        {
            v = fmp_pq_step(x * 10 / width);
        }
        else
        {
            v = width > 1 ? (fmp_u32)x * maxCode / (fmp_u32)(width - 1) : 0u;
        }

        *r = v;
        *g = v;
        *b = v;
        return;
    }

    const int cellWidth = width / 32;
    const int cell = cellWidth > 0 ? x / cellWidth : 32;
    const bool on = cell < 32 && ((frame >> (31 - cell)) & 1u) != 0u;
    const fmp_u32 v = on ? maxCode : 0u;
    *r = v;
    *g = v;
    *b = v;
}

// swapRedBlue != 0 writes BGRA8 instead of RGBA8.
FMP_KERNEL void fmp_pattern_rgba8(fmp_u8* dst, fmp_u32 pitch, int width, int height, fmp_u32 frame, int swapRedBlue)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    fmp_u32 r, g, b;
    fmp_pattern_pixel(x, y, width, height, frame, false, &r, &g, &b);
    fmp_row_mut(dst, pitch, y)[x] = swapRedBlue ? fmp_pack8(b, g, r, 255u) : fmp_pack8(r, g, b, 255u);
}

FMP_KERNEL void fmp_pattern_rgb10a2(fmp_u8* dst, fmp_u32 pitch, int width, int height, fmp_u32 frame)
{
    int x, y;
    if (!fmp_pixel(width, height, &x, &y))
    {
        return;
    }

    fmp_u32 r, g, b;
    fmp_pattern_pixel(x, y, width, height, frame, true, &r, &g, &b);
    fmp_row_mut(dst, pitch, y)[x] = fmp_pack10(r, g, b, 3u);
}
