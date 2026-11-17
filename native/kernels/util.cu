// Diagnostics. fmp_delay_ns keeps a stream busy for a set GPU time: tests use it to make a producer stream slow
// on purpose, so a missing cross-stream dependency shows up as a wrong result rather than passing by luck.
#include "fmp_cuda.h"

FMP_INLINE fmp_u64 fmp_global_timer_ns()
{
    fmp_u64 t;
    asm volatile("mov.u64 %0, %%globaltimer;" : "=l"(t));
    return t;
}

// Launch with one thread.
FMP_KERNEL void fmp_delay_ns(fmp_u64 nanoseconds)
{
    const fmp_u64 start = fmp_global_timer_ns();
    while (fmp_global_timer_ns() - start < nanoseconds)
    {
    }
}
