using FrameMotionPlus.Cuda;
using FrameMotionPlus.Pipeline.Tests.Support;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// GPU times on <c>CLOCK_MONOTONIC</c>: the device's event clock runs 10 to 12 µs/s off it here, so a clock that chained
/// its anchors by elapsed time drifted (7 ms in ten minutes, enough to make generated frames appear to finish after the
/// refresh they were shown at). Re-anchored on host time at every renewal, it stays within tens of microseconds.
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class GpuClockTests
{
    /// <summary>
    /// For 12 s (six renewals), marks recorded on an idle stream: each mark's time must lie between when it was recorded
    /// and when the host saw it complete, within 60 µs (a chained clock is about 130 µs off by the end).
    /// </summary>
    [Fact]
    public void Gpu_times_stay_on_the_host_clock()
    {
        var cuda = Hardware.RequireGpu();
        cuda.MakeCurrent();
        using var clock = new GpuClock(cuda);
        using var stream = CudaStream.Create(cuda);
        Assert.True(clock.Start(stream));
        var end = Monotonic.NowNs + (12 * 1_000_000_000L);
        var worstLate = 0L;
        var index = 0L;
        while (Monotonic.NowNs < end)
        {
            var before = Monotonic.NowNs;
            clock.Mark(stream, index, before);
            long t;
            var giveUp = Monotonic.NowNs + 1_000_000_000;
            while (!clock.TryRead(index, out t))
            {
                Assert.True(Monotonic.NowNs < giveUp, "a mark on an idle stream did not complete within 1 s");
            }

            var after = Monotonic.NowNs;
            var error = t < before ? before - t : t > after ? t - after : 0;
            if (end - after < 2_000_000_000)
            {
                worstLate = Math.Max(worstLate, error);
            }

            index++;
            Thread.Sleep(20);
        }

        Log.WriteLine($"{index} marks; worst distance outside [recorded, seen complete] over the last 2 s: {worstLate / 1e3:0.0} µs");
        Assert.True(worstLate < 60_000, $"a GPU time was {worstLate / 1e3:0.0} µs off the host clock");
    }
}
