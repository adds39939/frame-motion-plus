using System.Runtime;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Platform.Tests;

/// <summary>
/// As in FrameMotionPlus.Core.Tests: allocation measurements (and tests that change process-wide GC state) run on
/// their own, after every parallel test, so nothing else allocates while they read the thread's allocation counter.
/// With non-concurrent GC in this test project the counter is exact, so hot paths must allocate exactly zero bytes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementDefinition
{
    public const string Name = "Allocation measurement (serial)";
}

[Collection(AllocationMeasurementDefinition.Name)]
public class AllocationTests
{
    private const int Windows = 3;
    private static long _sink;

    /// <summary>Bytes this thread allocates over <see cref="Windows"/> calls of <paramref name="window"/> after a warm-up call.</summary>
    private static long Measure(Action window)
    {
        window();
        long total = 0;
        for (var k = 0; k < Windows; k++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            window();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }

    [Fact]
    public void Test_host_uses_workstation_gc_without_background_collections()
    {
        Assert.False(GCSettings.IsServerGC);
        Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);
        Assert.False(GcConfiguration.IsConcurrent);
    }

    [Fact]
    public void Clock_reads_allocate_nothing()
    {
        var allocated = Measure(static () =>
        {
            long x = 0;
            for (var i = 0; i < 100_000; i++)
            {
                x ^= Monotonic.NowNs;
            }

            _sink += x;
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Calls through IClock are what is under test.")]
    public void Clock_reads_through_the_IClock_interface_allocate_nothing()
    {
        IClock clock = new MonotonicClock();
        var allocated = Measure(() =>
        {
            long x = 0;
            for (var i = 0; i < 100_000; i++)
            {
                x ^= clock.NowNs;
            }

            _sink += x;
        });

        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(WaitStrategy.Sleep, 0)]
    [InlineData(WaitStrategy.Hybrid, 50_000)]
    [InlineData(WaitStrategy.Hybrid, DeadlineWaiter.DefaultSpinWindowNs)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Calls through IClock are what is under test.")]
    public void Waits_allocate_nothing(WaitStrategy strategy, long spinNs)
    {
        var waiter = new DeadlineWaiter(strategy, spinNs);
        IClock clock = new MonotonicClock(waiter);
        var allocated = Measure(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                _sink += waiter.WaitUntil(Monotonic.NowNs + 300_000);
                clock.SleepUntil(Monotonic.NowNs + 100_000);
                _sink += waiter.WaitUntil(Monotonic.NowNs - Nanoseconds.PerSecond);
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Gc_snapshots_allocate_nothing()
    {
        var allocated = Measure(static () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var start = GcSnapshot.Take();
                var delta = GcSnapshot.Take().Since(start);
                _sink += delta.Collections + delta.TotalAllocatedBytes + GcSnapshot.CurrentThreadAllocatedBytes;
            }
        });

        Assert.Equal(0, allocated);
    }
}
