namespace FrameMotionPlus.Vulkan;

/// <summary>
/// The value protocol on the presentation ring's two timeline semaphores. It needs nothing from the producer but
/// these rules, so a CUDA producer, the Vulkan diagnostic producer or anything else that can import an opaque FD
/// timeline semaphore follows the same steps.
/// <list type="number">
/// <item>Writes into a ring are numbered n = 1, 2, 3, ... (per ring instance, and never reused on it: a producer that
/// imports a ring with history continues after the highest write it has seen; a recreated ring starts again at 1).
/// Write n goes to slot <see cref="SlotOf"/>(n) = (n − 1) mod N, so slots are used in ring order.</item>
/// <item>Before writing n, the producer waits GPU-side until <em>released</em> ≥ <see cref="ReleasedValueBeforeWrite"/>(n)
/// = n − N: the consumer has finished with the slot's previous contents (write n − N). For n ≤ N there is nothing to
/// wait for (the value is 0, which a fresh semaphore already holds).</item>
/// <item>When write n is complete, the producer signals <em>written</em> = n. Writes complete in order, so the values
/// it signals strictly increase.</item>
/// <item>The consumer waits GPU-side until <em>written</em> ≥ n before reading write n, and signals <em>released</em> = n
/// when it has finished reading it. Released values strictly increase; releasing n also releases every earlier write
/// that was never read.</item>
/// <item>The presenter never releases more than N past its previous release in one step: a release of v waits for
/// <em>written</em> ≥ v, and the producer's write v waits for <em>released</em> ≥ v − N, so a longer jump could wait on
/// itself. It submits the intermediate releases itself, so presenting or releasing any later write is safe.</item>
/// <item>When a present request is refused (TryPresent returns false), the newest write handed to the presenter stays
/// presentable: it is not released, and a retry can still show it. Every older write is released. That write is
/// released when a newer write is handed to the presenter (shown or refused), by an explicit release
/// (<see cref="VulkanPresenter.Release"/> / <see cref="VulkanPresenter.ReleaseThrough"/>), when Configure replaces the
/// ring, or when the presenter is disposed. So at most one unshown write holds the producer back: with N ≥ 2 the
/// producer can always write at least one more frame.</item>
/// <item>Writes that are never handed to the presenter are released by the next one that is, or explicitly.</item>
/// <item>A request is refused before anything is submitted when accepting it would leave more than
/// <see cref="MaxWaitingReleases"/> (500) submissions that signal <em>released</em> (catch-up and explicit releases,
/// and the copies of shown writes) waiting for writes the producer has not finished. Those already waiting count:
/// they are counted from the ring's actual <em>released</em> value, which is read only when the count might pass the
/// bound. A request more than <see cref="MaxWriteLead"/> (<see cref="MaxLeadInSlots"/> × N) past the last release
/// needs more than that on its own and is refused without that read. This is a safety bound for the device, not a
/// pacing rule: a producer's write n only <em>completes</em> once released ≥ n − N, but it can be handed out and
/// queued GPU-side much earlier (the CUDA/Vulkan bridge lets a producer run up to its own, lower limit ahead), and
/// catching up takes one release submission per N writes. Measured on NVIDIA (Vulkan review): on a queue that had
/// never run a command buffer, a present submitted behind 511 waiting releases lost the device (510 did not); on a
/// queue that had, about 6,550 waiting releases did. So once the earlier releases have run a request may still be
/// 500 × N writes ahead (2,000 at N = 4). While a stalled producer has that many releases waiting, requests are
/// refused (TryPresent false, Release ignored, both counted; ReleaseThrough throws) until its writes finish. A
/// refused write is neither shown, retained nor released; the next accepted request or release covers it. So a
/// producer must not depend on a refused request being released: it must stay within these bounds, and the
/// CUDA/Vulkan bridge makes sure it does (it hands out a write only while the writes the GPU has not finished are
/// within a limit far below them, so neither bound can be reached through it).</item>
/// <item>Guidance, separate from that bound: a pipeline that follows the design doc ("no queued frames anywhere")
/// keeps the writes it has handed out but the GPU has not finished within about N, and skips frames at the source
/// rather than queueing them.</item>
/// <item>A write may be presented more than once while it is the most recent one presented: repeats are drawn from
/// the presenter's own staged copy and do not touch the slot. Once a later write has been presented or released, an
/// earlier one may have been overwritten and is refused (stale).</item>
/// </list>
/// Both semaphores start at 0. Values are 64-bit and never wrap in practice (2^64 frames).
/// </summary>
public static class RingProtocol
{
    public const ulong FirstWrite = 1;

    /// <summary>
    /// The most submissions signalling <em>released</em> that may wait for unfinished writes (see the class remarks).
    /// 500: below the 511 waiting releases that lost the device when a present was the queue's first
    /// command buffer. Also below the NVIDIA driver's period of one slow <c>vkQueueSubmit2</c> (about 1.6 ms every
    /// 1,024th submission), so one call meets that stall at most once.
    /// </summary>
    public const int MaxWaitingReleases = 500;

    /// <summary>
    /// How far past the last release, in multiples of the slot count, a write may be (<see cref="MaxWriteLead"/>): a
    /// request that far ahead makes <see cref="MaxWaitingReleases"/> release submissions by itself (one per N writes).
    /// </summary>
    public const int MaxLeadInSlots = MaxWaitingReleases;

    /// <summary>
    /// The largest distance past the last release that a present or release request may name: <see cref="MaxLeadInSlots"/>
    /// × N. Accepted when the earlier releases have run (see <see cref="MaxWaitingReleases"/>).
    /// </summary>
    public static int MaxWriteLead(int slotCount) => MaxLeadInSlots * slotCount;

    public static int SlotOf(ulong write, int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(write);
        return (int)((write - 1) % (ulong)slotCount);
    }

    /// <summary>The <em>released</em> value the producer must wait for before starting write <paramref name="write"/>.</summary>
    public static ulong ReleasedValueBeforeWrite(ulong write, int slotCount) =>
        write > (ulong)slotCount ? write - (ulong)slotCount : 0;
}
