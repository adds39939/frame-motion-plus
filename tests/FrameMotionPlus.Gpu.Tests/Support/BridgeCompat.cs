using FrameMotionPlus.CudaVulkan;

namespace FrameMotionPlus.Gpu.Tests.Support;

/// <summary>
/// The bridge members the review regression tests need that the pre-review bridge did not have, in one place, so the
/// same tests can be compiled against the old bridge (with a stub of this file) to show that they fail there.
/// </summary>
internal static class BridgeCompat
{
    public static CudaVulkanBridgeOptions Options(int maxLead = 0, int operationsPerWrite = 0, long drainTimeoutNs = 2_000_000_000) =>
        new() { MaxLead = maxLead, OperationsPerWrite = operationsPerWrite, DrainTimeoutNs = drainTimeoutNs };

    public static bool WaitForReaper(TimeSpan timeout) => CudaVulkanBridge.WaitForReaper(timeout);

    public static int PendingReaps => CudaVulkanBridge.PendingReaps;

    public static long OutOfOrderSignals(CudaVulkanBridge bridge) => bridge.OutOfOrderSignals;
}
