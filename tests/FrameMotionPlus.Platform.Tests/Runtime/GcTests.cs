using System.Runtime;
using FrameMotionPlus.Platform.Runtime;

namespace FrameMotionPlus.Platform.Tests.Runtime;

/// <summary>The latency mode is process-wide, so these run in the serial collection.</summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class GcTests
{
    [Fact]
    public void Scope_requests_SustainedLowLatency_reports_what_applies_and_restores()
    {
        var before = GCSettings.LatencyMode;
        GCLatencyMode inside;
        GcLatencyScope captured;
        using (var scope = GcLatencyScope.Enter())
        {
            inside = GCSettings.LatencyMode;
            captured = scope;
            Assert.Equal(before, scope.Previous);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, scope.Requested);
            Assert.Equal(inside, scope.Obtained);
            Assert.Equal(scope.Obtained == GCLatencyMode.SustainedLowLatency, scope.Applied);
        }

        // SustainedLowLatency needs concurrent GC; this host (like fmp) runs without it and stays in Batch.
        Assert.Equal(GcConfiguration.IsConcurrent ? GCLatencyMode.SustainedLowLatency : GCLatencyMode.Batch, inside);
        Assert.Equal(before, GCSettings.LatencyMode);
        captured.Dispose();
        Assert.Equal(before, GCSettings.LatencyMode);
    }

    [Fact]
    public void LowLatency_applies_to_workstation_gc_and_is_restored()
    {
        var before = GCSettings.LatencyMode;
        using (var scope = GcLatencyScope.Enter(GCLatencyMode.LowLatency))
        {
            Assert.True(scope.Applied);
            Assert.Equal(GCLatencyMode.LowLatency, GCSettings.LatencyMode);
        }

        Assert.Equal(before, GCSettings.LatencyMode);
    }

    [Fact]
    public void A_no_gc_region_is_not_a_latency_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GcLatencyScope.Enter(GCLatencyMode.NoGCRegion));
        Assert.Throws<ArgumentOutOfRangeException>(() => GcLatencyScope.Enter((GCLatencyMode)99));
    }

    [Fact]
    public void Snapshots_count_collections_and_allocations()
    {
        var start = GcSnapshot.Take();
        var threadBefore = GcSnapshot.CurrentThreadAllocatedBytes;
        GC.KeepAlive(new byte[100_000]);
        var allocated = GcSnapshot.Take().Since(start);

        // The process-wide figure is the cheap estimate (the precise one suspends the runtime): it can drop by the
        // unused part of every thread's allocation context when a collection flushes them, so it is checked here
        // without one in between. The per-thread counter is exact.
        Assert.True(allocated.TotalAllocatedBytes >= 100_000, $"total allocated grew by {allocated.TotalAllocatedBytes} B");
        Assert.InRange(GcSnapshot.CurrentThreadAllocatedBytes - threadBefore, 100_000, 101_000);

        GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        var collected = GcSnapshot.Take().Since(start);
        Assert.True(collected.Gen0Collections >= 1);
        Assert.True(collected.Gen1Collections >= 1);
        Assert.Equal(collected.Gen0Collections, collected.Collections);
        Assert.True(collected.TotalPauseDuration >= TimeSpan.Zero);
    }

    /// <summary>Mode-agnostic: also passes with DOTNET_gcConcurrent=1, which the runtime configuration does not show.</summary>
    [Fact]
    public void Configuration_describes_this_process_as_the_runtime_sees_it()
    {
        var mode = GCSettings.LatencyMode;
        var concurrent = GcConfiguration.IsConcurrent;

        Assert.Equal(mode, GCSettings.LatencyMode); // the probe restores the mode
        Assert.False(GcConfiguration.IsServer);
        Assert.Equal(mode == GCLatencyMode.Interactive, concurrent); // the startup mode: Interactive with, Batch without
        Assert.StartsWith(concurrent ? "workstation, concurrent, " : "workstation, non-concurrent, ", GcConfiguration.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe briefly changes the process-wide latency mode. It takes the lock scopes take, so a scope entered or
    /// disposed on another thread at the same moment still obtains its mode and restores the right one afterwards.
    /// After the first probe the answer is cached and the mode is not touched at all.
    /// </summary>
    [Fact]
    public void Probing_races_no_scope_and_is_cached()
    {
        var before = GCSettings.LatencyMode;
        var expected = GcConfiguration.IsConcurrent;
        var stop = 0;
        var wrongObtained = 0;
        var scopes = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                using var scope = GcLatencyScope.Enter(GCLatencyMode.LowLatency);
                if (scope.Obtained != GCLatencyMode.LowLatency || scope.Previous != before)
                {
                    Interlocked.Increment(ref wrongObtained);
                }
            }
        }) { IsBackground = true, Name = "gc-scopes" };
        scopes.Start();
        for (var i = 0; i < 20_000; i++)
        {
            GcConfiguration.ForgetProbe();
            Assert.Equal(expected, GcConfiguration.IsConcurrent);
        }

        Volatile.Write(ref stop, 1);
        Assert.True(scopes.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, wrongObtained);
        Assert.Equal(before, GCSettings.LatencyMode);

        // Cached: with a scope holding another mode, asking again neither changes it nor re-probes.
        using var low = GcLatencyScope.Enter(GCLatencyMode.LowLatency);
        Assert.Equal(expected, GcConfiguration.IsConcurrent);
        Assert.Equal(GCLatencyMode.LowLatency, GCSettings.LatencyMode);
    }

    [Fact]
    public void Probing_for_concurrent_gc_keeps_a_mode_set_by_a_scope()
    {
        using var scope = GcLatencyScope.Enter(GCLatencyMode.LowLatency);
        var concurrent = GcConfiguration.IsConcurrent;

        Assert.Equal(GCLatencyMode.LowLatency, GCSettings.LatencyMode);
        using var sustained = GcLatencyScope.Enter();
        Assert.Equal(concurrent, sustained.Applied);
    }
}
