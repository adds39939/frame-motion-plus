namespace FrameMotionPlus.Core.Frames;

public enum GpuSignalKind
{
    None = 0,

    /// <summary>A timeline semaphore (Vulkan, imported into CUDA); <see cref="GpuSignal.Value"/> is the timeline value.</summary>
    TimelineSemaphore = 1,

    /// <summary>
    /// A CUDA event (<c>CUevent</c>). A wait covers the event's most recent record at the time the wait is enqueued,
    /// so the producer records it before publishing the frame, and an event is not re-recorded while a published frame
    /// still refers to it (one event per frame buffer). <see cref="GpuSignal.Value"/> is the event's generation tag
    /// when the CUDA wrapper made the signal (it then rejects the signal once the event is destroyed, even if the
    /// driver reuses the handle), or 0.
    /// </summary>
    CudaEvent = 2,
}

/// <summary>
/// A GPU-side synchronisation point. Producers signal it when work completes and consumers wait on it
/// GPU-side, so the CPU never blocks on the GPU between capture and present.
/// <para>
/// <see cref="Handle"/> is only meaningful in its <see cref="Domain"/>. One timeline semaphore shared between APIs has
/// a different handle in each (on Linux a <c>VkSemaphore</c> exported as an FD and imported into CUDA as a
/// <c>CUexternalSemaphore</c>), so each side holds its own <see cref="GpuSignal"/> for the same timeline point.
/// </para>
/// <para>
/// <b>Lifetime.</b> A signal is a raw handle with no ownership: the event or semaphore behind it must outlive every
/// frame, request and queued GPU wait that names it. Waiting on or signalling a destroyed CUDA event makes the driver
/// spin and then crash; the CUDA wrapper checks every signal against its live objects and throws instead, but a
/// pipeline must not rely on that. On reconfigure, a source switch or ring recreation, a pipeline therefore stops
/// handing out frames that name the old objects, waits until the GPU work that uses them has finished (the last
/// <c>done</c> signal, or a stream synchronisation off the pacing thread), and only then disposes them. Signals kept
/// in history or telemetry after that point must not be used again.
/// </para>
/// </summary>
public readonly record struct GpuSignal
{
    public GpuSignalKind Kind { get; init; }

    /// <summary>
    /// The API <see cref="Handle"/> belongs to: <see cref="MemoryDomain.CudaDevice"/> for a CUDA external semaphore
    /// or event, <see cref="MemoryDomain.Vulkan"/> for a <c>VkSemaphore</c>. <see cref="MemoryDomain.Unknown"/> when
    /// no real API is involved (simulation, tests).
    /// </summary>
    public MemoryDomain Domain { get; init; }

    public ulong Handle { get; init; }

    public ulong Value { get; init; }

    public bool IsNone => Kind == GpuSignalKind.None;

    public static GpuSignal None => default;

    /// <summary>A timeline point whose handle belongs to no particular API (simulation, tests).</summary>
    public static GpuSignal Timeline(ulong handle, ulong value) =>
        new() { Kind = GpuSignalKind.TimelineSemaphore, Handle = handle, Value = value };

    /// <summary>A timeline point as seen from <paramref name="domain"/>, using that API's handle for the semaphore.</summary>
    public static GpuSignal Timeline(MemoryDomain domain, ulong handle, ulong value) =>
        new() { Kind = GpuSignalKind.TimelineSemaphore, Domain = domain, Handle = handle, Value = value };

    /// <summary>The most recent record of the CUDA event <paramref name="handle"/> (a <c>CUevent</c>).</summary>
    public static GpuSignal FromCudaEvent(ulong handle) =>
        new() { Kind = GpuSignalKind.CudaEvent, Domain = MemoryDomain.CudaDevice, Handle = handle };
}
