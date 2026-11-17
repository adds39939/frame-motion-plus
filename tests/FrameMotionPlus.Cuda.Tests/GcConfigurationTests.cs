using System.Reflection;
using System.Runtime;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// <see cref="AllocationTests"/> demands exactly zero bytes, which holds only if the thread allocation counter is exact.
/// Under concurrent (background) GC a gen2 collection triggered by another thread can move it by a few KB on a thread
/// that allocated nothing, so this test project turns concurrent GC off and measures in a serial collection.
/// </summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class GcConfigurationTests
{
    /// <summary>The csproj's TieredPGO=false reaches the test host (see the comment there).</summary>
    [Fact]
    public void Test_host_runs_without_dynamic_pgo() =>
        Assert.True(AppContext.TryGetSwitch("System.Runtime.TieredPGO", out var pgo) && !pgo, "System.Runtime.TieredPGO is not false in this host");

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
    }
}
