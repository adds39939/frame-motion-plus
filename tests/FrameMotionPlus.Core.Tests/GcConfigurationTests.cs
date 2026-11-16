using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace FrameMotionPlus.Core.Tests;

/// <summary>
/// The zero-allocation tests in <see cref="AllocationTests"/> are only exact if the thread allocation counter is.
/// It is not under concurrent (background) GC: a background gen2 collection triggered by another thread can move a
/// thread's <see cref="GC.GetAllocatedBytesForCurrentThread"/> by up to ~8 KB although the thread allocated nothing,
/// which made those tests fail intermittently on 2 to 4 cores while other test classes allocated large lists.
/// </summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class GcConfigurationTests
{
    private static long _sink;

    [Fact]
    public void Test_host_uses_workstation_gc_without_background_collections()
    {
        Assert.False(GCSettings.IsServerGC);

        // Batch is what the runtime reports when concurrent GC is off; Interactive means it is on.
        Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);
    }

    [Fact]
    public void Allocation_measurements_run_in_a_serial_collection()
    {
        var definition = typeof(AllocationMeasurementDefinition).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
        Assert.Equal(AllocationMeasurementDefinition.Name, typeof(AllocationTests).GetCustomAttribute<CollectionAttribute>()?.Name);
        Assert.Equal(AllocationMeasurementDefinition.Name, typeof(GcConfigurationTests).GetCustomAttribute<CollectionAttribute>()?.Name);
    }

    /// <summary>
    /// The review's reproduction: one thread churns the large object heap (as the simulation tests' 11 MB record
    /// lists do), forcing gen2 collections, while this thread runs pure arithmetic between counter reads. With
    /// concurrent GC roughly 1 in 10 of those collections moved this thread's counter; without it, none may.
    /// <para>
    /// Bounded by time and by collections, not by steps, so a slow or busy machine only makes it run longer. No moved
    /// counter is asserted on whatever ran; if the machine produced too few collections in the time to show the
    /// difference, the test reports itself skipped (inconclusive) rather than failed.
    /// </para>
    /// </summary>
    [Fact]
    public void Thread_allocation_counter_stays_exact_while_another_thread_forces_gen2_collections()
    {
        const int TargetGen2 = 150;
        const int ConclusiveGen2 = 50; // concurrent GC moved the counter in over 1 of 10 collections: 50 shows it
        var budget = TimeSpan.FromSeconds(15);
        var stop = 0;
        var noise = new Thread(() =>
        {
            var keep = new List<byte[]>(8);
            while (Volatile.Read(ref stop) == 0)
            {
                keep.Add(new byte[12 * 1024 * 1024]);
                if (keep.Count > 6)
                {
                    keep.Clear();
                }
            }
        }) { IsBackground = true, Name = "loh-churn" };

        Watch(int.MaxValue, 0, TimeSpan.FromMilliseconds(1), out _, out _); // JIT everything the measured loop uses
        var gen2Before = GC.CollectionCount(2);
        noise.Start();
        var steps = Watch(TargetGen2, gen2Before, budget, out var moved, out var first);
        Volatile.Write(ref stop, 1);
        Assert.True(noise.Join(TimeSpan.FromSeconds(30)), "noise thread did not stop");
        var gen2 = GC.CollectionCount(2) - gen2Before;

        Assert.True(moved == 0, $"the counter of a thread that allocated nothing moved in {moved} of {steps} steps (first by {first} B) across {gen2} gen2 collections");
        if (gen2 < ConclusiveGen2)
        {
            Assert.Skip($"inconclusive: only {gen2} gen2 collections in {budget.TotalSeconds:F0} s ({steps} steps, none moved the counter)");
        }
    }

    /// <summary>
    /// Reads this thread's allocated bytes around a pure arithmetic step until <paramref name="targetGen2"/> gen2
    /// collections have happened or <paramref name="budget"/> has passed. Counts steps whose delta was not zero.
    /// Allocates only outside the measured windows.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static long Watch(int targetGen2, int gen2Before, TimeSpan budget, out long moved, out long first)
    {
        var started = Stopwatch.GetTimestamp();
        moved = 0;
        first = 0;
        long steps = 0;
        while (GC.CollectionCount(2) - gen2Before < targetGen2 && Stopwatch.GetElapsedTime(started) < budget)
        {
            // Keep a partly used allocation context on this thread, as any real test body has, outside the window.
            GC.KeepAlive(new byte[100]);
            var before = GC.GetAllocatedBytesForCurrentThread();
            _sink += Spin(100_000);
            var delta = GC.GetAllocatedBytesForCurrentThread() - before;
            if (delta != 0)
            {
                moved++;
                first = first == 0 ? delta : first;
            }

            steps++;
        }

        return steps;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static long Spin(int n)
    {
        long x = 1;
        for (var i = 0; i < n; i++)
        {
            x = (x * 6364136223846793005L) + 1442695040888963407L;
        }

        return x;
    }
}
