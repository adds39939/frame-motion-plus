using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// Memory allocated by another API (a Vulkan <c>VkDeviceMemory</c> exported as an opaque FD or NT handle) and
/// imported with <c>cuImportExternalMemory</c>. <see cref="MapBuffer"/> gives CUDA pointers into it.
/// </summary>
public sealed unsafe class CudaExternalMemory : CudaObject
{
    private nint _handle;

    private CudaExternalMemory(CudaContext context, nint handle, long sizeBytes)
        : base(context)
    {
        _handle = handle;
        SizeBytes = sizeBytes;
    }

    /// <summary>The <c>CUexternalMemory</c>.</summary>
    public nint Handle => _handle;

    public long SizeBytes { get; }

    /// <summary>
    /// Imports an opaque POSIX file descriptor (<c>VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT</c>).
    /// <paramref name="sizeBytes"/> is the whole allocation's size (<c>VkMemoryRequirements.size</c>), and
    /// <paramref name="dedicated"/> must say whether it was a dedicated allocation.
    /// <para>On success CUDA owns the descriptor and closes it; the caller must not use or close it again. On failure
    /// the caller still owns it (the driver itself would close it on some failures; see <c>DescriptorHandoff</c>).
    /// The driver checks neither the size nor that the descriptor is memory, so pass the exporter's values.</para>
    /// </summary>
    public static CudaExternalMemory ImportOpaqueFd(CudaContext context, int fd, long sizeBytes, bool dedicated)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(fd);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        CuExternalMemoryHandleDesc description = default;
        description.Type = CuExternalMemoryHandleType.OpaqueFd;
        description.Size = (ulong)sizeBytes;
        description.Flags = dedicated ? CuConstants.ExternalMemoryDedicated : 0;
        if (!OperatingSystem.IsLinux())
        {
            description.Handle.Fd = fd;
            return Import(context, &description, sizeBytes);
        }

        // Serialised, so a failed import's check-then-close of its duplicate cannot hit another import's duplicate.
        lock (DescriptorHandoff.Gate)
        {
            var duplicate = DescriptorHandoff.Duplicate(fd);
            description.Handle.Fd = duplicate;
            CudaExternalMemory memory;
            try
            {
                memory = Import(context, &description, sizeBytes);
            }
            catch
            {
                DescriptorHandoff.Discard(duplicate);
                throw;
            }

            DescriptorHandoff.Consumed(fd);
            return memory;
        }
    }

    /// <summary>
    /// Imports an opaque NT handle (<c>VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT</c>), Windows only. CUDA does not
    /// take ownership of NT handles; the caller closes it.
    /// </summary>
    public static CudaExternalMemory ImportOpaqueWin32(CudaContext context, nint handle, long sizeBytes, bool dedicated)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NT handles exist only on Windows; use ImportOpaqueFd.");
        }

        if (handle == 0)
        {
            throw new ArgumentException("The NT handle is null.", nameof(handle));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        CuExternalMemoryHandleDesc description = default;
        description.Type = CuExternalMemoryHandleType.OpaqueWin32;
        description.Handle.Win32Handle = handle;
        description.Size = (ulong)sizeBytes;
        description.Flags = dedicated ? CuConstants.ExternalMemoryDedicated : 0;
        return Import(context, &description, sizeBytes);
    }

    /// <summary>
    /// Maps <paramref name="sizeBytes"/> at <paramref name="offset"/> as a device buffer
    /// (<c>cuExternalMemoryGetMappedBuffer</c>). The mapping keeps this import alive until it is disposed too.
    /// </summary>
    public CudaMappedBuffer MapBuffer(long offset, long sizeBytes)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        if (offset > SizeBytes || sizeBytes > SizeBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), $"[{offset}, {offset} + {sizeBytes}) lies outside the {SizeBytes}-byte import.");
        }

        // The mapping's references (on this import and on the context) come before the driver call.
        AddReference();
        try
        {
            Context.AddReference();
            try
            {
                Context.EnsureCurrent();
                CuExternalMemoryBufferDesc description = default;
                description.Offset = (ulong)offset;
                description.Size = (ulong)sizeBytes;
                ulong address;
                Cu.Check(
                    CuNative.cuExternalMemoryGetMappedBuffer(&address, _handle, &description),
                    nameof(CuNative.cuExternalMemoryGetMappedBuffer));
                return new CudaMappedBuffer(this, address, sizeBytes);
            }
            catch
            {
                Context.Release();
                throw;
            }
        }
        catch
        {
            Release();
            throw;
        }
    }

    private static CudaExternalMemory Import(CudaContext context, CuExternalMemoryHandleDesc* description, long sizeBytes)
    {
        // The context reference comes before the driver call: a successful import owns (and closes) the descriptor,
        // so nothing may fail after it, as taking the reference would for a disposed context.
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            nint handle;
            Cu.Check(CuNative.cuImportExternalMemory(&handle, description), nameof(CuNative.cuImportExternalMemory));
            return new CudaExternalMemory(context, handle, sizeBytes);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuDestroyExternalMemory(_handle);
        _handle = 0;
    }
}

/// <summary>
/// A device buffer mapped onto <see cref="CudaExternalMemory"/>; freed with <c>cuMemFree</c> on disposal, which blocks
/// until all queued GPU work in the context has finished: never on the pacing thread.
/// </summary>
public sealed class CudaMappedBuffer : CudaObject
{
    private readonly CudaExternalMemory _memory;
    private ulong _address;

    internal CudaMappedBuffer(CudaExternalMemory memory, ulong address, long sizeBytes)
        : base(memory.Context)
    {
        _memory = memory;
        _address = address;
        SizeBytes = sizeBytes;
    }

    /// <summary>The <c>CUdeviceptr</c>.</summary>
    public ulong Address => _address;

    public long SizeBytes { get; }

    /// <summary>A <see cref="GpuFrame"/> view of part of this buffer (e.g. one ring slot), checked to lie inside it.</summary>
    public GpuFrame AsFrame(int width, int height, PixelFormat format, int pitchBytes, long offset = 0,
        ColorMetadata color = default, GpuSignal ready = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var frame = new GpuFrame
        {
            Domain = MemoryDomain.CudaDevice,
            Address = _address,
            Offset = (ulong)offset,
            SizeBytes = SizeBytes,
            Width = width,
            Height = height,
            PitchBytes = pitchBytes,
            Format = format,
            Color = color,
            Ready = ready,
        };
        FrameChecks.ValidateCudaImage(frame, nameof(width));
        return frame;
    }

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuMemFree_v2(_address);
        _address = 0;
    }

    private protected override void ReleaseParent() => _memory.Release();
}
