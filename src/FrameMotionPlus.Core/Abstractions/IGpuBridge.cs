using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

public readonly record struct BridgeConfig
{
    /// <summary>
    /// Ring depth; the design uses 4 to 6 slots. The consumer that owns the ring decides it (for Vulkan presentation,
    /// <c>VulkanPresenterOptions.SlotCount</c>); 0 means "the consumer's", any other value must equal it.
    /// </summary>
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

    /// <summary>
    /// The write this slot carries (the value of <see cref="ProducerSignal"/> and <see cref="ConsumerWait"/>). Numbers
    /// run 1, 2, … over the life of a ring and are never reused on it: a bridge that imports a ring with history (a new
    /// bridge, or a Configure that keeps the ring) continues after the highest number the ring has seen.
    /// </summary>
    public ulong Write { get; init; }

    /// <summary>
    /// Which configuration of the bridge handed the slot out (starts at 1, grows with every Configure that replaces the
    /// ring). A slot from an earlier generation names memory and semaphores that no longer exist: the bridge refuses it
    /// wherever it is given one back, and a producer must not use it.
    /// </summary>
    public int Generation { get; init; }

    /// <summary>
    /// The slot in the producer's domain (e.g. a CUDA pointer into the imported VkBuffer). Its
    /// <see cref="GpuFrame.Ready"/> is <see cref="ProducerWait"/>, so a generator handed this view as its output waits
    /// for the release without further plumbing.
    /// </summary>
    public GpuFrame ProducerView { get; init; }

    /// <summary>
    /// The same memory in the consumer's domain (e.g. the VkBuffer). Its <see cref="GpuFrame.Ready"/> is
    /// <see cref="ConsumerWait"/>.
    /// </summary>
    public GpuFrame ConsumerView { get; init; }

    /// <summary>
    /// Producer waits on this GPU-side before writing: the consumer has released the slot's previous contents. The
    /// <em>released</em> timeline in the producer's domain, at the value the previous use's
    /// <see cref="ConsumerSignal"/> carried.
    /// </summary>
    public GpuSignal ProducerWait { get; init; }

    /// <summary>
    /// Producer signals this when the slot is written: the <em>written</em> timeline in the producer's domain. Writes
    /// must be signalled in order (see <see cref="IGpuBridge"/>, "Order").
    /// </summary>
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
/// <para>
/// <b>Order.</b> Writes are numbered 1, 2, … and must be <em>signalled in that order</em>: <em>written</em> is a
/// timeline, so signalling write n after write n + 1 moves it backwards and the consumer may read a slot before it is
/// written. A producer that writes from one stream gets this for free. One that uses several (a generator on a stream
/// of its own, real frames copied on another) must order the signals: before signalling write n on one stream, make
/// that stream wait for write n − 1's <see cref="BridgeSlot.ProducerSignal"/> (a GPU-side wait on the same timeline).
/// An implementation may notice a violation (it reads <em>written</em> now and then) but cannot catch every one: a
/// backwards step undone before its next read goes unseen, so detection is best effort and no substitute for the rule.
/// </para>
/// <para>
/// <b>Lead.</b> Handing out a slot never waits, so a producer can get ahead of the GPU: write n can be handed out and
/// queued long before its work can run (it waits for the consumer's release of write n − N). Two numbers bound how far:
/// </para>
/// <list type="bullet">
/// <item><see cref="MaxOutstanding"/>, a hard limit on <see cref="Outstanding"/>, the writes handed out that the GPU
/// has not finished. It reflects completed work, not just requests made, so a slow producer, a stalled GPU or a
/// stalled consumer all show up in it and are held back: <see cref="TryAcquireSlot"/> then says no (skip producing
/// this frame) and <see cref="AcquireSlot"/> throws. Once the stall ends and the queued work runs, it falls again and
/// acquiring resumes by itself. The bridge chooses the limit so that, within it, queueing a write's work never blocks
/// on a full queue and the consumer never refuses a request for one. That is all the limit promises: where the
/// producer's API can also block for other reasons (on CUDA, while a call that synchronises the context waits on
/// another thread), the implementation says when it makes such calls itself.</item>
/// <item>Guidance, much lower: a pipeline that follows the design doc ("no queued frames anywhere") keeps
/// <see cref="Outstanding"/> within about <see cref="SlotCount"/> and skips frames at the source (does not produce
/// them) rather than queueing them, so every frame shown is the newest one.</item>
/// </list>
/// <para>
/// <b>Completion.</b> Every slot handed out must have its <see cref="BridgeSlot.ProducerSignal"/> enqueued (a write
/// that is never signalled cannot be told from a slow one: drains time out on it) and must reach the consumer, shown
/// or released (handing the consumer a later write, shown or not, or releasing it, covers the earlier ones). A slot
/// handed out and then forgotten keeps its writes, and the ones after it that wait for its release, unfinished, and
/// so counts against the limit until something newer reaches the consumer.
/// </para>
/// <para>
/// <b>Staleness.</b> A <see cref="BridgeSlot"/> belongs to one configuration (<see cref="BridgeSlot.Generation"/>).
/// After a Configure that replaced the ring, an old slot's views and signals name memory and semaphores of the old
/// ring (or, where the driver has reused an address, of the new one): never use one. <see cref="IsCurrent"/> tells.
/// </para>
/// </summary>
public interface IGpuBridge : IDisposable
{
    MemoryDomain ProducerDomain { get; }

    MemoryDomain ConsumerDomain { get; }

    int SlotCount { get; }

    /// <summary>
    /// Writes handed out whose work the GPU has not finished (not yet signalled <em>written</em>). Allocation-free;
    /// read it on the producer thread.
    /// </summary>
    long Outstanding { get; }

    /// <summary>The hard limit on <see cref="Outstanding"/> (see the remarks); 0 before <see cref="Configure"/>.</summary>
    long MaxOutstanding { get; }

    void Configure(in BridgeConfig config);

    /// <summary>
    /// The next slot in ring order, if handing it out keeps <see cref="Outstanding"/> within
    /// <see cref="MaxOutstanding"/>; false (and <paramref name="slot"/> default) otherwise: skip producing this frame.
    /// Never blocks, allocation-free; ordering is enforced by the GPU signals.
    /// </summary>
    bool TryAcquireSlot(out BridgeSlot slot);

    /// <summary>
    /// The next slot in ring order. Never blocks; ordering is enforced by the GPU signals. Throws
    /// <see cref="InvalidOperationException"/> when that would exceed <see cref="MaxOutstanding"/> (use
    /// <see cref="TryAcquireSlot"/> to skip the frame instead).
    /// </summary>
    BridgeSlot AcquireSlot();

    /// <summary>True when <paramref name="slot"/> was handed out by the current configuration (cheap; allocation-free).</summary>
    bool IsCurrent(in BridgeSlot slot);
}
