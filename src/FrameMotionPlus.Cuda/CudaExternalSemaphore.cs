using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// A semaphore created by another API (a Vulkan timeline <c>VkSemaphore</c> exported as an FD or NT handle) and
/// imported with <c>cuImportExternalSemaphore</c>. Signals and waits are enqueued on a stream and never block the CPU.
/// </summary>
public sealed unsafe class CudaExternalSemaphore : CudaObject
{
    /// <summary>Most semaphores one batched signal or wait call accepts (they are staged on the stack).</summary>
    public const int MaxBatch = 16;

    private nint _handle;

    private CudaExternalSemaphore(CudaContext context, nint handle, bool timeline)
        : base(context)
    {
        _handle = handle;
        IsTimeline = timeline;
        LiveHandles.AddSemaphore(handle);
    }

    /// <summary>The <c>CUexternalSemaphore</c>. Throws <see cref="ObjectDisposedException"/> once disposed.</summary>
    public nint Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    /// <summary>True for a timeline semaphore; false for a binary one, whose signal and wait values are ignored.</summary>
    public bool IsTimeline { get; }

    /// <summary>
    /// Imports a Vulkan timeline semaphore exported as an opaque FD
    /// (<c>CU_EXTERNAL_SEMAPHORE_HANDLE_TYPE_TIMELINE_SEMAPHORE_FD</c>). On success CUDA owns the descriptor and closes
    /// it; the caller must not use or close it again. On failure the caller still owns it.
    /// </summary>
    public static CudaExternalSemaphore ImportTimelineFd(CudaContext context, int fd) =>
        ImportFd(context, fd, CuExternalSemaphoreHandleType.TimelineSemaphoreFd, timeline: true);

    /// <summary>Imports a binary semaphore exported as an opaque FD. Ownership of the descriptor as for <see cref="ImportTimelineFd"/>.</summary>
    public static CudaExternalSemaphore ImportBinaryFd(CudaContext context, int fd) =>
        ImportFd(context, fd, CuExternalSemaphoreHandleType.OpaqueFd, timeline: false);

    /// <summary>Imports a Vulkan timeline semaphore exported as an NT handle, Windows only. The caller closes the handle.</summary>
    public static CudaExternalSemaphore ImportTimelineWin32(CudaContext context, nint handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NT handles exist only on Windows; use ImportTimelineFd.");
        }

        if (handle == 0)
        {
            throw new ArgumentException("The NT handle is null.", nameof(handle));
        }

        CuExternalSemaphoreHandleDesc description = default;
        description.Type = CuExternalSemaphoreHandleType.TimelineSemaphoreWin32;
        description.Handle.Win32Handle = handle;
        return Import(context, &description, timeline: true);
    }

    /// <summary>This semaphore at <paramref name="value"/> as a CUDA-domain <see cref="GpuSignal"/>.</summary>
    public GpuSignal At(ulong value) => GpuSignal.Timeline(MemoryDomain.CudaDevice, (ulong)Handle, value);

    /// <summary>Sets the timeline to <paramref name="value"/> once the work enqueued on <paramref name="stream"/> so far has finished.</summary>
    public void Signal(CudaStream stream, ulong value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Signal(At(value));
    }

    /// <summary>Later work on <paramref name="stream"/> waits, GPU-side, until the timeline reaches <paramref name="value"/>.</summary>
    public void Wait(CudaStream stream, ulong value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Wait(At(value));
    }

    /// <summary>One <c>cuSignalExternalSemaphoresAsync</c> for several semaphores: semaphore i is set to value i.</summary>
    public static void SignalMany(CudaStream stream, ReadOnlySpan<CudaExternalSemaphore> semaphores, ReadOnlySpan<ulong> values)
    {
        var count = ValidateBatch(stream, semaphores, values);
        var handles = stackalloc nint[count];
        var parameters = stackalloc CuExternalSemaphoreSignalParams[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = semaphores[i].Handle;
            parameters[i] = default;
            parameters[i].FenceValue = values[i];
        }

        Cu.Check(
            CuNative.cuSignalExternalSemaphoresAsync(handles, parameters, (uint)count, stream.Handle),
            nameof(CuNative.cuSignalExternalSemaphoresAsync));
    }

    /// <summary>One <c>cuWaitExternalSemaphoresAsync</c> for several semaphores: waits until semaphore i reaches value i.</summary>
    public static void WaitMany(CudaStream stream, ReadOnlySpan<CudaExternalSemaphore> semaphores, ReadOnlySpan<ulong> values)
    {
        var count = ValidateBatch(stream, semaphores, values);
        var handles = stackalloc nint[count];
        var parameters = stackalloc CuExternalSemaphoreWaitParams[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = semaphores[i].Handle;
            parameters[i] = default;
            parameters[i].FenceValue = values[i];
        }

        Cu.Check(
            CuNative.cuWaitExternalSemaphoresAsync(handles, parameters, (uint)count, stream.Handle),
            nameof(CuNative.cuWaitExternalSemaphoresAsync));
    }

    private static CudaExternalSemaphore ImportFd(CudaContext context, int fd, CuExternalSemaphoreHandleType type, bool timeline)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(fd);
        CuExternalSemaphoreHandleDesc description = default;
        description.Type = type;
        if (!OperatingSystem.IsLinux())
        {
            description.Handle.Fd = fd;
            return Import(context, &description, timeline);
        }

        // As for memory (DescriptorHandoff): this driver leaves the descriptor alone when a semaphore import fails,
        // but the ownership rule must not depend on that.
        lock (DescriptorHandoff.Gate)
        {
            var duplicate = DescriptorHandoff.Duplicate(fd);
            description.Handle.Fd = duplicate;
            CudaExternalSemaphore semaphore;
            try
            {
                semaphore = Import(context, &description, timeline);
            }
            catch
            {
                DescriptorHandoff.Discard(duplicate);
                throw;
            }

            DescriptorHandoff.Consumed(fd);
            return semaphore;
        }
    }

    private static CudaExternalSemaphore Import(CudaContext context, CuExternalSemaphoreHandleDesc* description, bool timeline)
    {
        // The reference comes before the driver call, as in CudaExternalMemory: a successful import owns the descriptor.
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            nint handle;
            Cu.Check(CuNative.cuImportExternalSemaphore(&handle, description), nameof(CuNative.cuImportExternalSemaphore));
            return new CudaExternalSemaphore(context, handle, timeline);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    private static int ValidateBatch(CudaStream stream, ReadOnlySpan<CudaExternalSemaphore> semaphores, ReadOnlySpan<ulong> values)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.ThrowIfDisposed();
        if (semaphores.Length != values.Length)
        {
            throw new ArgumentException("One value per semaphore.", nameof(values));
        }

        ArgumentOutOfRangeException.ThrowIfZero(semaphores.Length, nameof(semaphores));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(semaphores.Length, MaxBatch, nameof(semaphores));
        foreach (var semaphore in semaphores)
        {
            ArgumentNullException.ThrowIfNull(semaphore, nameof(semaphores));
            semaphore.ThrowIfDisposed();
            if (semaphore.Context.Handle != stream.Context.Handle)
            {
                throw new ArgumentException("Every semaphore must belong to the stream's context.", nameof(semaphores));
            }
        }

        stream.Context.EnsureCurrent();
        return semaphores.Length;
    }

    private protected override void Unregister() => LiveHandles.RemoveSemaphore(_handle);

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuDestroyExternalSemaphore(_handle);
        _handle = 0;
    }
}
