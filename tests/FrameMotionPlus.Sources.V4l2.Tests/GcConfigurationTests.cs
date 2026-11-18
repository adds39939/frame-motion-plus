using System.Runtime;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The zero-allocation tests demand exactly zero bytes, which holds only if the thread allocation counter is exact:
/// this test project turns concurrent GC off, as the other hardware test projects do.
/// </summary>
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
        Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);
    }
}
