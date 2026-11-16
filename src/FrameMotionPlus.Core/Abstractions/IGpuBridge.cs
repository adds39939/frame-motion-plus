using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

public readonly record struct BridgeConfig
{
    /// <summary>Ring depth; the design uses 4 to 6 slots.</summary>
    public int SlotCount { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public PixelFormat Format { get; init; }
}

/// <summary>
/// One presentation-ring slot as seen from both sides of the bridge. Two timelines run between the sides (design doc,
/// "Synchronisation"): <em>written</em>, which the producer signals when it has written the slot and the consumer
/// waits on before reading it, and <em>released</em>, which the consumer signals when it has finished reading and the
/// producer waits on before writing the slot again. Each side gets those points with its own API's handles.
/// </summary>
public readonly record struct BridgeSlot
{
    public int Index { get; init; }

    /// <summary>The slot in the producer's domain (e.g. a CUDA pointer into the imported VkBuffer).</summary>
    public GpuFrame ProducerView { get; init; }

    /// <summary>The same memory in the consumer's domain (e.g. the VkBuffer).</summary>
    public GpuFrame ConsumerView { get; init; }

    /// <summary>
    /// Producer waits on this GPU-side before writing: the consumer has released the slot's previous contents. The
    /// <em>released</em> timeline in the producer's domain, at the value the previous use's
    /// <see cref="ConsumerSignal"/> carried.
    /// </summary>
    public GpuSignal ProducerWait { get; init; }

    /// <summary>Producer signals this when the slot is written: the <em>written</em> timeline in the producer's domain.</summary>
    public GpuSignal ProducerSignal { get; init; }

    /// <summary>
    /// Consumer waits on this GPU-side before reading: the same <em>written</em> point as <see cref="ProducerSignal"/>
    /// (same value), in the consumer's domain (e.g. the <c>VkSemaphore</c> the present pass waits on).
    /// </summary>
    public GpuSignal ConsumerWait { get; init; }

    /// <summary>
    /// Consumer signals this when it has finished reading the slot: the <em>released</em> timeline in the consumer's
    /// domain. The producer's <see cref="ProducerWait"/> for the slot's next use is this point in its own domain.
    /// </summary>
    public GpuSignal ConsumerSignal { get; init; }
}

/// <summary>
/// The single boundary between memory domains (CUDA processing to Vulkan presentation on Linux):
/// a ring of shared buffers plus a timeline semaphore in each direction.
/// </summary>
public interface IGpuBridge : IDisposable
{
    MemoryDomain ProducerDomain { get; }

    MemoryDomain ConsumerDomain { get; }

    int SlotCount { get; }

    void Configure(in BridgeConfig config);

    /// <summary>Returns the next slot in ring order. Never blocks; ordering is enforced by the GPU signals.</summary>
    BridgeSlot AcquireSlot();
}
