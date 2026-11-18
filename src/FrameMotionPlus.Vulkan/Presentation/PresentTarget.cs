using FrameMotionPlus.Core.Time;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>Colour space of the images a target shows.</summary>
public enum TargetColorSpace
{
    /// <summary>sRGB / BT.709 non-linear (VK_COLOR_SPACE_SRGB_NONLINEAR_KHR).</summary>
    Sdr = 0,

    /// <summary>BT.2020 primaries with the SMPTE ST 2084 (PQ) transfer (VK_COLOR_SPACE_HDR10_ST2084_EXT).</summary>
    Hdr10St2084 = 1,
}

internal enum AcquireOutcome
{
    Acquired = 0,

    /// <summary>No image right now (all in use or a previous present still holds this frame slot's fence).</summary>
    NotReady = 1,

    /// <summary>The target must be recreated before it can show anything.</summary>
    OutOfDate = 2,
}

internal enum PresentOutcome
{
    Presented = 0,
    Suboptimal = 1,
    OutOfDate = 2,
}

/// <summary>What the presenter wants from a target when it (re)configures it.</summary>
internal readonly record struct OutputRequest(bool PreferHdr10, bool TenBitContent);

/// <summary>
/// Where the presenter's pass writes: a window's swapchain (<see cref="SwapchainTarget"/>) or offscreen images read
/// back to the CPU (<see cref="OffscreenTarget"/>). The presenter's code is the same for both; a target only owns its
/// images, how one is acquired and what happens after the pass (present, or copy to a readback buffer).
/// <para>Targets are created by the caller and handed to one <see cref="VulkanPresenter"/>, which drives them.</para>
/// </summary>
public abstract class PresentTarget : IDisposable
{
    private protected PresentTarget(VulkanDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
    }

    public TargetColorSpace ColorSpace { get; private protected set; }

    /// <summary>Current image size in pixels (0 when the target cannot show anything, e.g. a minimised window).</summary>
    public int Width { get; private protected set; }

    public int Height { get; private protected set; }

    /// <summary>Refresh rate of the display the target is on (or the configured one for offscreen).</summary>
    public abstract FrameRate RefreshRate { get; }

    /// <summary>
    /// False while <see cref="RefreshRate"/> is only a guess: a window is on no output until it has shown a frame, and
    /// SDL reports the primary display until then.
    /// </summary>
    public virtual bool RefreshRateKnown => true;

    /// <summary>True when the target can show HDR10 (PQ, BT.2020) if asked to.</summary>
    public abstract bool SupportsHdr10 { get; }

    internal VulkanDevice Device { get; }

    internal VkFormat Format { get; private protected set; }

    internal int ImageCount { get; private protected set; }

    internal VkImage[] Images { get; private protected set; } = [];

    internal VkImageView[] Views { get; private protected set; } = [];

    /// <summary>Changes each time images are recreated, so the presenter can rebuild what depends on them.</summary>
    internal int Version { get; private protected set; }

    /// <summary>Nothing can be shown right now (zero size); presents are dropped, slots still released.</summary>
    internal bool IsUnavailable => Width == 0 || Height == 0 || ImageCount == 0;

    internal virtual bool UsesAcquireSemaphore => false;

    /// <summary>A rebuild would produce no images (the window is minimised): no point rebuilding yet.</summary>
    internal virtual bool WantsZeroSize => false;

    /// <summary>The target exists but nothing it shows is visible (occluded window): presents are refused.</summary>
    internal virtual bool IsOccluded => false;

    /// <summary>Non-blocking: may frame slot <paramref name="frameSlot"/> be used (its last present released its semaphores)?</summary>
    internal virtual bool IsFrameSlotReady(int frameSlot) => true;

    /// <summary>Non-blocking: has every present released its semaphores, so images and semaphores may be destroyed?</summary>
    internal virtual bool PresentsComplete() => true;

    /// <summary>
    /// <see cref="WaitForPresents"/> idles the whole queue (no present fences), so it may only run once everything the
    /// presenter queued (including release batches that wait on the producer) has finished.
    /// </summary>
    internal virtual bool NeedsIdleQueueForPresents => false;

    /// <summary>A present has been reported complete (the presenter calls this for every completed present).</summary>
    internal virtual void OnPresentCompleted()
    {
    }

    internal virtual bool SupportsPresentWait => false;

    /// <summary>False when presents can queue behind the screen beyond the frame slots (a swapchain with no feedback).</summary>
    internal virtual bool CanCapQueuedPresents => true;

    internal bool IsBound { get; private set; }

    internal VkSemaphore FrameDone { get; private set; }

    /// <summary>Called once by the presenter that will drive this target.</summary>
    internal void Bind(VkSemaphore frameDone)
    {
        if (IsBound)
        {
            throw new InvalidOperationException("A target can be driven by only one presenter.");
        }

        IsBound = true;
        FrameDone = frameDone;
    }

    /// <summary>(Re)creates the images for <paramref name="request"/>. The presenter has drained its GPU work.</summary>
    internal abstract void Configure(in OutputRequest request);

    /// <summary>Cheap per-frame check: does the target need recreating (size change, out of date)?</summary>
    internal abstract bool NeedsRecreate();

    /// <summary>Recreates the images at the current size. The presenter has drained its GPU work.</summary>
    internal abstract void Recreate();

    /// <summary>Non-blocking. Gets the image for frame <paramref name="frame"/> (frame slot <paramref name="frameSlot"/>).</summary>
    internal abstract AcquireOutcome TryAcquire(ulong frame, int frameSlot, VkSemaphore acquireSemaphore, out int imageIndex);

    /// <summary>Binary semaphore the render batch signals and the present waits on; null for offscreen.</summary>
    internal virtual VkSemaphore RenderDoneSemaphore(int imageIndex) => default;

    /// <summary>Records what follows the pass on <paramref name="imageIndex"/> (layout for present, or readback copy).</summary>
    internal abstract unsafe void RecordEpilogue(VkCommandBuffer cmd, int imageIndex, ulong frame);

    internal abstract PresentOutcome Present(int imageIndex, int frameSlot, ulong presentId);

    /// <summary>Non-blocking: has present <paramref name="presentId"/> completed (shown)? Only when <see cref="SupportsPresentWait"/>.</summary>
    internal virtual bool PollPresentComplete(ulong presentId) => true;

    /// <summary>True when the target reports when each present's first pixel went out (<see cref="TryTakePresentTime"/>).</summary>
    internal virtual bool ReportsPresentTimes => false;

    /// <summary>
    /// Non-blocking: whether present <paramref name="presentId"/>'s report has come and, in <paramref name="timeNs"/>, when its
    /// first pixel went out on <c>CLOCK_MONOTONIC</c> (0 when the report came without that time: some compositors leave it
    /// out for some presents). Reports for older presents are discarded. Allocation-free.
    /// </summary>
    internal virtual bool TryTakePresentTime(ulong presentId, out long timeNs)
    {
        timeNs = 0;
        return false;
    }

    /// <summary>
    /// Non-blocking: whether present <paramref name="presentId"/>'s report has come (with a time or without), without taking
    /// it or discarding any report (older ones included). Allocation-free.
    /// </summary>
    internal virtual bool HasPresentReport(ulong presentId) => false;

    /// <summary>Waits (bounded) until every present has released its semaphores, before images or semaphores go.</summary>
    internal virtual void WaitForPresents(long timeoutNs)
    {
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);

    private protected static unsafe VkImageView CreateView(VulkanDevice device, VkImage image, VkFormat format)
    {
        var info = new VkImageViewCreateInfo
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        VkImageView view;
        VulkanException.Check(device.Api.vkCreateImageView(&info, &view), "vkCreateImageView");
        return view;
    }
}
