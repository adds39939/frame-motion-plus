using FrameMotionPlus.Core.Frames;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

public readonly record struct PresentationRingConfig
{
    public const int DefaultSlotCount = 4;
    public const int DefaultPitchAlignment = 256;

    /// <summary>4 to 6 (0 means <see cref="DefaultSlotCount"/>).</summary>
    public int SlotCount { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary><see cref="PixelFormat.Rgba8"/> or <see cref="PixelFormat.Rgb10A2"/>.</summary>
    public PixelFormat Format { get; init; }

    /// <summary>Row pitch alignment in bytes, a multiple of 4 (0 means <see cref="DefaultPitchAlignment"/>).</summary>
    public int PitchAlignment { get; init; }
}

/// <summary>One slot's buffer as the ring allocated it.</summary>
public readonly record struct RingSlotInfo
{
    public int Index { get; init; }

    /// <summary>The <c>VkBuffer</c> handle (also <see cref="GpuFrame.Address"/> of the slot's consumer view).</summary>
    public ulong BufferHandle { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int PitchBytes { get; init; }

    public PixelFormat Format { get; init; }

    /// <summary>Bytes the image occupies: <see cref="PitchBytes"/> × <see cref="Height"/>, from offset 0.</summary>
    public long ContentSizeBytes { get; init; }

    /// <summary>Size of the exported memory object. This is the size an importer passes (e.g. to cuImportExternalMemory).</summary>
    public long AllocationSizeBytes { get; init; }
}

/// <summary>
/// The presentation ring: the single boundary between the producer (CUDA) and Vulkan presentation. N slots, each a
/// <c>VkBuffer</c> in its own dedicated, exportable device-local allocation (opaque FD), pitch-linear, plus two
/// exportable timeline semaphores, <em>written</em> (producer to presenter) and <em>released</em> (presenter to
/// producer). The value protocol is <see cref="RingProtocol"/>; FD ownership is <see cref="ExportedFd"/>.
/// <para>
/// A ring has a fixed shape. A size or format change means a new ring (the presenter does this in Configure);
/// producers import the new one and start again at write 1. Pixel layout: row r starts at byte r × pitch; Rgba8 is
/// bytes R, G, B, A; Rgb10A2 is one little-endian uint32 per pixel with R in bits 0–9, G 10–19, B 20–29 and A 30–31
/// (VK_FORMAT_A2B10G10R10_UNORM_PACK32, NVCV_RGB10A2).
/// </para>
/// </summary>
public sealed unsafe class PresentationRing : IDisposable
{
    private const VkBufferUsageFlags Usage =
        VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.StorageBuffer;

    private readonly VulkanDevice _device;
    private readonly VkBuffer[] _buffers;
    private readonly VkDeviceMemory[] _memory;
    private readonly ulong[] _bufferHandles;
    private readonly Lock _counterGate = new(); // a read from another thread against Dispose (TryReadWrittenValue)
    private VkSemaphore _written;
    private VkSemaphore _released;
    private bool _disposed;

    private PresentationRing(VulkanDevice device, in PresentationRingConfig config, int pitch)
    {
        _device = device;
        SlotCount = config.SlotCount;
        Width = config.Width;
        Height = config.Height;
        Format = config.Format;
        PitchBytes = pitch;
        ContentSizeBytes = (long)pitch * config.Height;
        _buffers = new VkBuffer[SlotCount];
        _memory = new VkDeviceMemory[SlotCount];
        _bufferHandles = new ulong[SlotCount];
    }

    public int SlotCount { get; }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int PitchBytes { get; }

    public long ContentSizeBytes { get; }

    /// <summary>Size of each slot's memory object (every slot has the same). Pass this when importing.</summary>
    public long AllocationSizeBytes { get; private set; }

    /// <summary>Vulkan memory type index of the slots, which a Vulkan importer must allocate with.</summary>
    public uint MemoryTypeIndex { get; private set; }

    /// <summary>Always true: each slot is a dedicated allocation (CUDA: import with CUDA_EXTERNAL_MEMORY_DEDICATED).</summary>
    public bool DedicatedAllocation { get; } = true;

    public DeviceUuid DeviceUuid => _device.PhysicalDevice.Uuid;

    /// <summary>The <c>VkSemaphore</c> handle of <em>written</em> (as a <see cref="GpuSignal.Handle"/>).</summary>
    public ulong WrittenSemaphoreHandle => _written.Handle;

    /// <summary>The <c>VkSemaphore</c> handle of <em>released</em>.</summary>
    public ulong ReleasedSemaphoreHandle => _released.Handle;

    internal VulkanDevice Device => _device;

    internal VkSemaphore Written => _written;

    internal VkSemaphore Released => _released;

    internal VkFormat VkFormat => ToVkFormat(Format);

    public static PresentationRing Create(VulkanDevice device, in PresentationRingConfig config)
    {
        ArgumentNullException.ThrowIfNull(device);
        var c = Normalize(config);
        var pitch = AlignUp(c.Format.MinimumPitch(c.Width), c.PitchAlignment);
        var ring = new PresentationRing(device, c, pitch);
        try
        {
            ring.Allocate();
            return ring;
        }
        catch
        {
            ring.Dispose();
            throw;
        }
    }

    public RingSlotInfo Slot(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        return new RingSlotInfo
        {
            Index = index,
            BufferHandle = _bufferHandles[index],
            Width = Width,
            Height = Height,
            PitchBytes = PitchBytes,
            Format = Format,
            ContentSizeBytes = ContentSizeBytes,
            AllocationSizeBytes = AllocationSizeBytes,
        };
    }

    public int SlotOf(ulong write) => RingProtocol.SlotOf(write, SlotCount);

    /// <summary>
    /// The Vulkan-domain view of write <paramref name="write"/>'s slot, for <see cref="Core.Abstractions.PresentRequest.Frame"/>.
    /// Its <see cref="GpuFrame.Ready"/> is <see cref="WrittenSignal"/>(n), the point the presenter waits on.
    /// </summary>
    public GpuFrame ConsumerFrame(ulong write) => new()
    {
        Ready = WrittenSignal(write),
        Domain = MemoryDomain.Vulkan,
        Address = _bufferHandles[SlotOf(write)],
        SizeBytes = AllocationSizeBytes,
        Width = Width,
        Height = Height,
        PitchBytes = PitchBytes,
        Format = Format,
        Color = Format == PixelFormat.Rgb10A2 ? ColorMetadata.Hdr10 : ColorMetadata.SdrBt709,
    };

    /// <summary>The point the presenter waits on for write <paramref name="write"/>: <em>written</em> ≥ n, Vulkan domain.</summary>
    public GpuSignal WrittenSignal(ulong write) => GpuSignal.Timeline(MemoryDomain.Vulkan, _written.Handle, write);

    /// <summary>The point the presenter signals after reading write <paramref name="write"/>: <em>released</em> = n, Vulkan domain.</summary>
    public GpuSignal ReleasedSignal(ulong write) => GpuSignal.Timeline(MemoryDomain.Vulkan, _released.Handle, write);

    /// <summary>A new FD for slot <paramref name="index"/>'s memory, owned by the returned object.</summary>
    public ExportedFd ExportSlotMemory(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
        var info = new VkMemoryGetFdInfoKHR { memory = _memory[index], handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        int fd;
        VulkanException.Check(_device.Api.vkGetMemoryFdKHR(&info, &fd), "vkGetMemoryFdKHR");
        return new ExportedFd(fd);
    }

    public ExportedFd ExportWrittenSemaphore() => ExportSemaphore(_written);

    public ExportedFd ExportReleasedSemaphore() => ExportSemaphore(_released);

    /// <summary>Everything a producer needs to import the ring, with fresh FDs it owns until imported.</summary>
    public PresentationRingExport Export()
    {
        var fds = new List<ExportedFd>(SlotCount + 2);
        try
        {
            for (var i = 0; i < SlotCount; i++)
            {
                fds.Add(ExportSlotMemory(i));
            }

            fds.Add(ExportWrittenSemaphore());
            fds.Add(ExportReleasedSemaphore());
            return new PresentationRingExport(this, fds[..SlotCount], fds[SlotCount], fds[SlotCount + 1]);
        }
        catch
        {
            foreach (var fd in fds)
            {
                fd.Dispose();
            }

            throw;
        }
    }

    /// <summary>The current value of <em>written</em> (host query, no wait).</summary>
    public ulong ReadWrittenValue() => ReadCounter(_written);

    /// <summary>The current value of <em>released</em> (host query, no wait).</summary>
    public ulong ReadReleasedValue() => ReadCounter(_released);

    /// <summary>
    /// The current value of <em>written</em> (host query, no wait), from any thread and safely against a concurrent
    /// <see cref="Dispose"/> by the ring's owner: false once the ring is disposed, or while it is being disposed.
    /// For observers on other threads that do not own the ring (the CUDA/Vulkan bridge's import reaper); the owner's
    /// thread uses <see cref="ReadWrittenValue"/>. Allocation-free.
    /// </summary>
    public bool TryReadWrittenValue(out ulong value)
    {
        lock (_counterGate)
        {
            if (_disposed || _written.IsNull)
            {
                value = 0;
                return false;
            }

            value = ReadCounter(_written);
            return true;
        }
    }

    /// <summary>Host wait for <em>written</em> ≥ <paramref name="value"/>. Diagnostics and teardown only.</summary>
    public bool WaitWritten(ulong value, long timeoutNs) => WaitCounter(_written, value, timeoutNs);

    /// <summary>Host wait for <em>released</em> ≥ <paramref name="value"/>. Diagnostics and teardown only.</summary>
    public bool WaitReleased(ulong value, long timeoutNs) => WaitCounter(_released, value, timeoutNs);

    internal int FindSlot(ulong bufferHandle)
    {
        var handles = _bufferHandles;
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i] == bufferHandle)
            {
                return i;
            }
        }

        return -1;
    }

    internal VkBuffer Buffer(int index) => _buffers[index];

    /// <summary>
    /// Host-signals <em>written</em> to <paramref name="value"/> so GPU waits on it can complete. Only for teardown,
    /// after the producer has stopped: a producer that later signals a lower value would be an error.
    /// </summary>
    internal void ForceWritten(ulong value)
    {
        if (ReadCounter(_written) >= value)
        {
            return;
        }

        var info = new VkSemaphoreSignalInfo { semaphore = _written, value = value };
        VulkanException.Check(_device.Api.vkSignalSemaphore(&info), "vkSignalSemaphore");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_counterGate)
        {
            _disposed = true; // from here no TryReadWrittenValue touches the semaphores
        }

        var api = _device.Api;
        for (var i = 0; i < SlotCount; i++)
        {
            if (!_buffers[i].IsNull)
            {
                api.vkDestroyBuffer(_buffers[i]);
            }

            if (!_memory[i].IsNull)
            {
                api.vkFreeMemory(_memory[i]);
            }
        }

        if (!_written.IsNull)
        {
            api.vkDestroySemaphore(_written);
        }

        if (!_released.IsNull)
        {
            api.vkDestroySemaphore(_released);
        }

        _written = default;
        _released = default;
    }

    internal static VkFormat ToVkFormat(PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 => VkFormat.R8G8B8A8Unorm,
        PixelFormat.Rgb10A2 => VkFormat.A2B10G10R10UnormPack32,
        _ => throw new NotSupportedException($"Ring format {format} is not supported (Rgba8 and Rgb10A2 are)."),
    };

    /// <summary>
    /// The configuration with defaults filled in (0 slots or pitch alignment = the defaults); throws for one
    /// <see cref="Create"/> would refuse. Lets a caller check a shape before tearing anything down.
    /// </summary>
    public static PresentationRingConfig Normalize(in PresentationRingConfig config)
    {
        var c = config with
        {
            SlotCount = config.SlotCount == 0 ? PresentationRingConfig.DefaultSlotCount : config.SlotCount,
            PitchAlignment = config.PitchAlignment == 0 ? PresentationRingConfig.DefaultPitchAlignment : config.PitchAlignment,
        };
        if (c.SlotCount is < 4 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(config), c.SlotCount, "The ring has 4 to 6 slots.");
        }

        if (c.Width <= 0 || c.Height <= 0 || c.Width > 16384 || c.Height > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(config), $"{c.Width}x{c.Height}", "Ring size must be 1 to 16384 per side.");
        }

        if (c.PitchAlignment <= 0 || c.PitchAlignment % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), c.PitchAlignment, "Pitch alignment must be a positive multiple of 4.");
        }

        _ = ToVkFormat(c.Format);
        return c;
    }

    private static int AlignUp(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private void Allocate()
    {
        var api = _device.Api;
        var iapi = _device.Instance.Api;
        var phys = _device.PhysicalDevice.Handle;

        var bufferQuery = new VkPhysicalDeviceExternalBufferInfo { usage = Usage, handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        var bufferProps = new VkExternalBufferProperties();
        iapi.vkGetPhysicalDeviceExternalBufferProperties(phys, &bufferQuery, &bufferProps);
        if ((bufferProps.externalMemoryProperties.externalMemoryFeatures & VkExternalMemoryFeatureFlags.Exportable) == 0)
        {
            throw new VulkanException("This device cannot export buffer memory as an opaque FD.");
        }

        var semaphoreType = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline };
        var semaphoreQuery = new VkPhysicalDeviceExternalSemaphoreInfo
        {
            pNext = &semaphoreType,
            handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD,
        };
        var semaphoreProps = new VkExternalSemaphoreProperties();
        iapi.vkGetPhysicalDeviceExternalSemaphoreProperties(phys, &semaphoreQuery, &semaphoreProps);
        if ((semaphoreProps.externalSemaphoreFeatures & VkExternalSemaphoreFeatureFlags.Exportable) == 0)
        {
            throw new VulkanException("This device cannot export timeline semaphores as opaque FDs.");
        }

        for (var i = 0; i < SlotCount; i++)
        {
            var external = new VkExternalMemoryBufferCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
            var bufferInfo = new VkBufferCreateInfo
            {
                pNext = &external,
                size = (ulong)ContentSizeBytes,
                usage = Usage,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkBuffer buffer;
            VulkanException.Check(api.vkCreateBuffer(&bufferInfo, &buffer), "vkCreateBuffer");
            _buffers[i] = buffer;
            _bufferHandles[i] = buffer.Handle;

            var dedicatedReqs = new VkMemoryDedicatedRequirements();
            var reqs = new VkMemoryRequirements2 { pNext = &dedicatedReqs };
            var reqInfo = new VkBufferMemoryRequirementsInfo2 { buffer = buffer };
            api.vkGetBufferMemoryRequirements2(&reqInfo, &reqs);
            var typeIndex = _device.FindMemoryType(
                reqs.memoryRequirements.memoryTypeBits,
                VkMemoryPropertyFlags.DeviceLocal,
                VkMemoryPropertyFlags.HostVisible);
            if (i == 0)
            {
                AllocationSizeBytes = (long)reqs.memoryRequirements.size;
                MemoryTypeIndex = typeIndex;
            }
            else if ((long)reqs.memoryRequirements.size != AllocationSizeBytes || typeIndex != MemoryTypeIndex)
            {
                throw new VulkanException("Ring slots got different memory requirements.");
            }

            var export = new VkExportMemoryAllocateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
            var dedicated = new VkMemoryDedicatedAllocateInfo { pNext = &export, buffer = buffer };
            var allocInfo = new VkMemoryAllocateInfo
            {
                pNext = &dedicated,
                allocationSize = reqs.memoryRequirements.size,
                memoryTypeIndex = typeIndex,
            };
            VkDeviceMemory memory;
            VulkanException.Check(api.vkAllocateMemory(&allocInfo, &memory), "vkAllocateMemory");
            _memory[i] = memory;
            VulkanException.Check(api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
        }

        _written = CreateExportableTimeline();
        _released = CreateExportableTimeline();
    }

    private VkSemaphore CreateExportableTimeline()
    {
        var export = new VkExportSemaphoreCreateInfo { handleTypes = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        var type = new VkSemaphoreTypeCreateInfo { pNext = &export, semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var info = new VkSemaphoreCreateInfo { pNext = &type };
        VkSemaphore semaphore;
        VulkanException.Check(_device.Api.vkCreateSemaphore(&info, &semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    private ExportedFd ExportSemaphore(VkSemaphore semaphore)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new VkSemaphoreGetFdInfoKHR { semaphore = semaphore, handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        int fd;
        VulkanException.Check(_device.Api.vkGetSemaphoreFdKHR(&info, &fd), "vkGetSemaphoreFdKHR");
        return new ExportedFd(fd);
    }

    private ulong ReadCounter(VkSemaphore semaphore)
    {
        ulong value;
        VulkanException.Check(_device.Api.vkGetSemaphoreCounterValue(semaphore, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    private bool WaitCounter(VkSemaphore semaphore, ulong value, long timeoutNs)
    {
        var s = semaphore;
        var info = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &s, pValues = &value };
        var result = _device.Api.vkWaitSemaphores(&info, (ulong)Math.Max(0, timeoutNs));
        VulkanException.Check(result, "vkWaitSemaphores");
        return result == VkResult.Success;
    }
}

/// <summary>
/// A ring as a producer sees it: shape, memory parameters and freshly exported FDs (see <see cref="ExportedFd"/> for
/// ownership). Disposing closes every FD that has not been transferred to an importer.
/// </summary>
public sealed class PresentationRingExport : IDisposable
{
    internal PresentationRingExport(PresentationRing ring, IReadOnlyList<ExportedFd> slots, ExportedFd written, ExportedFd released)
    {
        DeviceUuid = ring.DeviceUuid;
        SlotCount = ring.SlotCount;
        Width = ring.Width;
        Height = ring.Height;
        PitchBytes = ring.PitchBytes;
        Format = ring.Format;
        ContentSizeBytes = ring.ContentSizeBytes;
        AllocationSizeBytes = ring.AllocationSizeBytes;
        MemoryTypeIndex = ring.MemoryTypeIndex;
        SlotMemory = slots;
        WrittenSemaphore = written;
        ReleasedSemaphore = released;
    }

    /// <summary>The device the ring lives on; an importer must use the same physical device.</summary>
    public DeviceUuid DeviceUuid { get; }

    public int SlotCount { get; }

    public int Width { get; }

    public int Height { get; }

    public int PitchBytes { get; }

    public PixelFormat Format { get; }

    public long ContentSizeBytes { get; }

    /// <summary>Size of each slot's memory object; the size to import with.</summary>
    public long AllocationSizeBytes { get; }

    /// <summary>For Vulkan importers: opaque FD memory must be imported with the exporter's memory type.</summary>
    public uint MemoryTypeIndex { get; }

    /// <summary>Every slot is a dedicated allocation (CUDA: CUDA_EXTERNAL_MEMORY_DEDICATED).</summary>
    public bool DedicatedAllocation { get; } = true;

    /// <summary>One opaque memory FD per slot, in slot order.</summary>
    public IReadOnlyList<ExportedFd> SlotMemory { get; }

    /// <summary>Opaque FD of the <em>written</em> timeline semaphore (import as a timeline semaphore).</summary>
    public ExportedFd WrittenSemaphore { get; }

    /// <summary>Opaque FD of the <em>released</em> timeline semaphore.</summary>
    public ExportedFd ReleasedSemaphore { get; }

    public void Dispose()
    {
        foreach (var fd in SlotMemory)
        {
            fd.Dispose();
        }

        WrittenSemaphore.Dispose();
        ReleasedSemaphore.Dispose();
    }
}
