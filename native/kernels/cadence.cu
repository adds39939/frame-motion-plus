// Cadence metric for a captured frame (design doc, "Cadence detection"): how much the new frame differs from the last
// unique one, as the largest per-tile mean absolute difference of a downsampled luma grid. A PS5 sends a fixed-refresh
// signal, so a 30 fps game arrives at 60 Hz with every picture twice; the CPU half (Core's CadenceTracker) turns this
// metric into unique/repeat decisions.
//
// The grid: cells of 4x4 luma samples, each the plain sum of its 16 code values (no division: exact). Columns and rows
// past the last whole cell are not sampled. Tiles: 8x8 cells (32x32 pixels), one thread block each, one thread per cell.
// Each thread sums its cell, stores the sum into the current grid and takes |sum - reference sum|; the block adds those
// up and turns them into the tile's mean absolute difference per luma sample in 1/65536 code units:
//
//     q = floor(65536 * sum|cur - ref| / (cells * 16))      (cells: the tile's cells inside the grid)
//
// and keeps the maximum over tiles (a small HUD change still counts: one 32x32 tile is enough). It also keeps the largest
// cell sum, so the host can tell a black frame (no signal, a console asleep). Integer arithmetic only, and max and sum
// are order-independent: a CPU reference matches bit for bit. result[0] and result[1] must be zeroed before the launch.
#include "fmp_cuda.h"

#if defined(__clang__) && !defined(__NVCC__)
#define __shared__ __attribute__((shared))
#define FMP_ATOMIC_MAX_U64(p, v) __nvvm_atom_max_gen_ull((p), (v))
#else
#define FMP_ATOMIC_MAX_U64(p, v) atomicMax((p), (v))
#endif

typedef long long fmp_i64;
typedef unsigned short fmp_u16;

#define FMP_CELL 4
#define FMP_TILE 8

// Mirrors FrameMotionPlus.Cuda.Kernels.CadenceKernelParameters: 64-bit fields only.
struct fmp_cadence_params
{
    fmp_i64 cellsX;      // whole cells across: width / 4
    fmp_i64 cellsY;      // whole cells down: height / 4
    fmp_i64 sampleBytes; // bytes from one luma sample to the next: 1 (NV12), 2 (P010, YUYV)
    fmp_i64 sixteen;     // 1: 16-bit samples (P010), 0: 8-bit (NV12, YUYV's Y bytes)
    fmp_i64 shift;       // right shift of a 16-bit sample to its code value (6 for P010)
};

FMP_KERNEL void fmp_cadence_metric(const fmp_u8* luma, fmp_u32 pitch, const fmp_u32* reference, fmp_u32* current,
                                   fmp_u64* result, struct fmp_cadence_params p)
{
    __shared__ fmp_u64 diffs[FMP_TILE * FMP_TILE];
    __shared__ fmp_u32 sums[FMP_TILE * FMP_TILE];

    const int t = (int)(threadIdx.y * FMP_TILE + threadIdx.x);
    const fmp_i64 cx = (fmp_i64)blockIdx.x * FMP_TILE + threadIdx.x;
    const fmp_i64 cy = (fmp_i64)blockIdx.y * FMP_TILE + threadIdx.y;
    fmp_u64 diff = 0;
    fmp_u32 sum = 0;
    if (cx < p.cellsX && cy < p.cellsY)
    {
        for (int dy = 0; dy < FMP_CELL; dy++)
        {
            const fmp_u8* row = luma + (fmp_u64)pitch * (fmp_u64)(cy * FMP_CELL + dy);
            for (int dx = 0; dx < FMP_CELL; dx++)
            {
                const fmp_u8* s = row + (fmp_u64)(cx * FMP_CELL + dx) * (fmp_u64)p.sampleBytes;
                sum += p.sixteen ? (fmp_u32)(*(const fmp_u16*)s >> p.shift) : (fmp_u32)*s;
            }
        }

        const fmp_u64 i = (fmp_u64)cy * (fmp_u64)p.cellsX + (fmp_u64)cx;
        const fmp_u32 r = reference[i];
        current[i] = sum;
        diff = sum > r ? sum - r : r - sum;
    }

    diffs[t] = diff;
    sums[t] = sum;
    __syncthreads();
    for (int s = FMP_TILE * FMP_TILE / 2; s > 0; s >>= 1)
    {
        if (t < s)
        {
            diffs[t] += diffs[t + s];
            sums[t] = sums[t] > sums[t + s] ? sums[t] : sums[t + s];
        }

        __syncthreads();
    }

    if (t == 0)
    {
        const fmp_i64 x0 = (fmp_i64)blockIdx.x * FMP_TILE;
        const fmp_i64 y0 = (fmp_i64)blockIdx.y * FMP_TILE;
        const fmp_i64 w = p.cellsX - x0 < FMP_TILE ? p.cellsX - x0 : FMP_TILE;
        const fmp_i64 h = p.cellsY - y0 < FMP_TILE ? p.cellsY - y0 : FMP_TILE;
        const fmp_u64 cells = (fmp_u64)(w * h);
        FMP_ATOMIC_MAX_U64(&result[0], (diffs[0] << 16) / (cells * FMP_CELL * FMP_CELL));
        FMP_ATOMIC_MAX_U64(&result[1], (fmp_u64)sums[0]);
    }
}
