// Shared by every kernel source. One source compiles with nvcc (which force-includes cuda_runtime.h) and with
// clang's NVPTX backend run with -nocudainc -nocudalib, which needs no NVIDIA headers: the few CUDA keywords the
// kernels use are defined here, and threadIdx/blockIdx/blockDim come from clang's own resource header.
//
// Kernels use integer arithmetic only, so the C# reference implementations in the tests are bit-exact and the
// result cannot depend on the compiler's floating-point contraction choices.
#pragma once

#if defined(__clang__) && !defined(__NVCC__)
#define __global__ __attribute__((global))
#define __device__ __attribute__((device))
#define __forceinline__ __inline__ __attribute__((always_inline))
#include <__clang_cuda_builtin_vars.h>
#endif

#define FMP_KERNEL extern "C" __global__
#define FMP_INLINE static __device__ __forceinline__

typedef unsigned char fmp_u8;
typedef unsigned int fmp_u32;
typedef unsigned long long fmp_u64;

// Pixel coordinates of this thread in a 2D launch; false outside the image.
FMP_INLINE bool fmp_pixel(int width, int height, int* x, int* y)
{
    *x = (int)(blockIdx.x * blockDim.x + threadIdx.x);
    *y = (int)(blockIdx.y * blockDim.y + threadIdx.y);
    return *x < width && *y < height;
}

// Pitch-linear 32-bit pixel addressing. Pitches are in bytes.
FMP_INLINE const fmp_u32* fmp_row(const fmp_u8* base, fmp_u32 pitch, int y)
{
    return (const fmp_u32*)(base + (fmp_u64)pitch * (fmp_u64)y);
}

FMP_INLINE fmp_u32* fmp_row_mut(fmp_u8* base, fmp_u32 pitch, int y)
{
    return (fmp_u32*)(base + (fmp_u64)pitch * (fmp_u64)y);
}

// RGBA8 in memory is R, G, B, A bytes: little-endian word R | G << 8 | B << 16 | A << 24.
FMP_INLINE fmp_u32 fmp_pack8(fmp_u32 r, fmp_u32 g, fmp_u32 b, fmp_u32 a)
{
    return r | (g << 8) | (b << 16) | (a << 24);
}

// RGB10A2: R in bits 0-9, G 10-19, B 20-29, A 30-31 (VK_FORMAT_A2B10G10R10_UNORM_PACK32, NVCV_RGB10A2).
FMP_INLINE fmp_u32 fmp_pack10(fmp_u32 r, fmp_u32 g, fmp_u32 b, fmp_u32 a)
{
    return r | (g << 10) | (b << 20) | (a << 30);
}
