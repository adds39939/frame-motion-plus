using System.Collections.Concurrent;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// The CUDA events and external semaphores this library has created and not yet destroyed. A <c>GpuSignal</c> is a
/// raw handle with no lifetime tie, and the driver spins and then crashes when asked to wait on or record a destroyed
/// event, so every signal is checked here first (a lock-free, allocation-free lookup of a few nanoseconds).
/// <para>
/// Events carry a generation tag in <c>GpuSignal.Value</c> (see <see cref="CudaEvent.Signal"/>), which also catches a
/// stale signal whose handle the driver has since reused for a new event. Semaphore signals use <c>Value</c> for the
/// timeline value, so for them only the handle is checked.
/// </para>
/// </summary>
internal static class LiveHandles
{
    private static readonly ConcurrentDictionary<nint, ulong> _events = new();
    private static readonly ConcurrentDictionary<nint, byte> _semaphores = new();
    private static long _generation;

    internal static ulong AddEvent(nint handle)
    {
        var generation = (ulong)Interlocked.Increment(ref _generation);
        _events[handle] = generation;
        return generation;
    }

    internal static void RemoveEvent(nint handle) => _events.TryRemove(handle, out _);

    /// <summary>True when <paramref name="handle"/> is a live event and <paramref name="tag"/> is 0 or its generation.</summary>
    internal static bool IsLiveEvent(nint handle, ulong tag) =>
        _events.TryGetValue(handle, out var generation) && (tag == 0 || tag == generation);

    internal static void AddSemaphore(nint handle) => _semaphores[handle] = 0;

    internal static void RemoveSemaphore(nint handle) => _semaphores.TryRemove(handle, out _);

    internal static bool IsLiveSemaphore(nint handle) => _semaphores.ContainsKey(handle);
}
