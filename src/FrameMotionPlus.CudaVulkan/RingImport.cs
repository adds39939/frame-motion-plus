using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.CudaVulkan;

/// <summary>Which import a <see cref="BridgeTestHooks"/> callback is about to run.</summary>
internal enum ImportStep
{
    SlotMemory = 0,
    SlotMapping = 1,
    WrittenSemaphore = 2,
    ReleasedSemaphore = 3,
}

/// <summary>Test seams, so failure paths can be driven without broken hardware. Never set in production.</summary>
internal sealed class BridgeTestHooks
{
    /// <summary>Replaces the CUDA device UUID the bridge compares against (a stand-in for a second GPU).</summary>
    public DeviceUuid? CudaUuid { get; init; }

    /// <summary>Runs before each import call (step, slot index or -1); may throw to fail the import there.</summary>
    public Action<ImportStep, int>? BeforeImport { get; init; }

    /// <summary>Replaces the descriptor passed to an import (step, index, exported fd) so the driver itself rejects it.</summary>
    public Func<ImportStep, int, int, int>? FdOverride { get; init; }

    /// <summary>Runs before the bridge releases outstanding writes through the presenter (last write); may throw to make that fail.</summary>
    public Action<ulong>? BeforeRelease { get; init; }

    /// <summary>Runs at the start of each look the reaper takes at this bridge's abandoned import; may throw (e.g. a lost context).</summary>
    public Action? BeforeReapStep { get; init; }

    /// <summary>Runs just before the reaper signals <em>released</em> from CUDA for this bridge's abandoned import (the value).</summary>
    public Action<ulong>? BeforeReapRelease { get; init; }
}

/// <summary>
/// One presentation ring as imported into CUDA: per slot an external memory object and a device pointer mapped onto
/// it, plus the <em>written</em> and <em>released</em> timeline semaphores. Owns only the CUDA objects; the
/// <see cref="PresentationRing"/> (Vulkan) stays the presenter's.
/// <para>
/// FD ownership: <see cref="Import"/> takes a fresh <see cref="PresentationRingExport"/>, passes each descriptor to its
/// CUDA import and marks it transferred the moment that import succeeds (CUDA then owns and closes it). When anything
/// fails part-way, the CUDA objects imported so far are destroyed (closing what CUDA owns) and the export's disposal
/// closes exactly the descriptors that were never transferred. No descriptor is closed twice or left open.
/// </para>
/// </summary>
internal sealed class RingImport : IDisposable
{
    private readonly CudaExternalMemory?[] _memory;
    private readonly CudaMappedBuffer?[] _mapped;
    private readonly GpuFrame[] _producerViews;
    private CudaExternalSemaphore? _written;
    private CudaExternalSemaphore? _released;
    private ulong _writtenHandle;
    private ulong _releasedHandle;
    private bool _disposed;

    /// <summary>The highest write any bridge has handed out on each ring, so numbering never restarts on a ring.</summary>
    private static readonly ConditionalWeakTable<PresentationRing, StrongBox<ulong>> Handed = [];

    private RingImport(PresentationRing ring)
    {
        Ring = ring;
        HighestHandedOut = Handed.GetValue(ring, static _ => new StrongBox<ulong>());
        SlotCount = ring.SlotCount;
        _memory = new CudaExternalMemory?[SlotCount];
        _mapped = new CudaMappedBuffer?[SlotCount];
        _producerViews = new GpuFrame[SlotCount];
    }

    public PresentationRing Ring { get; }

    /// <summary>
    /// The highest write handed out on <see cref="Ring"/> by any bridge, ever (shared by every import of the ring).
    /// Updated per acquire with a plain field write; read when a ring is imported.
    /// </summary>
    public StrongBox<ulong> HighestHandedOut { get; }

    public int SlotCount { get; }

    public CudaExternalSemaphore Written => _written!;

    public CudaExternalSemaphore Released => _released!;

    /// <summary>CUDA objects this import holds (memory, mappings, semaphores); for leak checks.</summary>
    public int LiveObjects
    {
        get
        {
            var count = 0;
            for (var i = 0; i < SlotCount; i++)
            {
                count += (_memory[i] is { IsDisposed: false } ? 1 : 0) + (_mapped[i] is { IsDisposed: false } ? 1 : 0);
            }

            return count + (_written is { IsDisposed: false } ? 1 : 0) + (_released is { IsDisposed: false } ? 1 : 0);
        }
    }

    /// <summary>
    /// Exports <paramref name="ring"/> (new descriptors) and imports every slot and both semaphores into
    /// <paramref name="context"/>. Checks the device UUID before importing anything.
    /// </summary>
    public static RingImport Import(CudaContext context, PresentationRing ring, DeviceUuid cudaUuid, BridgeTestHooks? hooks)
    {
        using var export = ring.Export();
        GpuIdentity.EnsureSameDevice(cudaUuid, export.DeviceUuid, "The presentation ring");
        var import = new RingImport(ring);
        try
        {
            import.ImportAll(context, export, hooks);
            return import;
        }
        catch
        {
            import.Dispose();
            throw;
        }

        // export.Dispose() closes every descriptor not marked transferred: all of them on a UUID mismatch, the
        // not-yet-imported ones after a failure part-way, none after success.
    }

    /// <summary>
    /// Write <paramref name="write"/> of this ring (slot (n − 1) mod N) as both sides see it, following
    /// <see cref="RingProtocol"/>, stamped with the bridge's configuration <paramref name="generation"/>. Allocation-free.
    /// </summary>
    public BridgeSlot Slot(ulong write, int generation)
    {
        var n = (ulong)SlotCount;
        var index = (int)((write - 1) % n);

        // Write n may start once the consumer has released write n − N, the slot's previous contents. Nothing to wait
        // for during the first lap: a fresh semaphore is 0.
        var producerWait = write > n
            ? GpuSignal.Timeline(MemoryDomain.CudaDevice, _releasedHandle, write - n)
            : GpuSignal.None;
        var ring = Ring;
        return new BridgeSlot
        {
            Index = index,
            Write = write,
            Generation = generation,
            ProducerView = _producerViews[index] with { Ready = producerWait },
            ConsumerView = ring.ConsumerFrame(write),
            ProducerWait = producerWait,
            ProducerSignal = GpuSignal.Timeline(MemoryDomain.CudaDevice, _writtenHandle, write),
            ConsumerWait = ring.WrittenSignal(write),
            ConsumerSignal = ring.ReleasedSignal(write),
        };
    }

    /// <summary>
    /// Frees the memory imports and hands the two semaphores to the caller, who keeps them alive (so the driver cannot
    /// give their handles to a later import while a stale slot may still name them) and disposes them later. The bridge
    /// keeps them until it is disposed: two per ring replacement, a bound that grows with reconfigures (300 on one bridge
    /// showed a flat Configure cost and unchanged GPU memory).
    /// </summary>
    public (CudaExternalSemaphore? Written, CudaExternalSemaphore? Released) Retire()
    {
        var semaphores = (_written, _released);
        _written = null;
        _released = null;
        Dispose();
        return semaphores;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Mappings before the memory they map (CudaObject reference counting would cope either way).
        for (var i = 0; i < SlotCount; i++)
        {
            _mapped[i]?.Dispose();
            _memory[i]?.Dispose();
        }

        _written?.Dispose();
        _released?.Dispose();
    }

    private void ImportAll(CudaContext context, PresentationRingExport export, BridgeTestHooks? hooks)
    {
        var color = export.Format == PixelFormat.Rgb10A2 ? ColorMetadata.Hdr10 : ColorMetadata.SdrBt709;
        for (var i = 0; i < SlotCount; i++)
        {
            var fd = export.SlotMemory[i];
            hooks?.BeforeImport?.Invoke(ImportStep.SlotMemory, i);
            var raw = hooks?.FdOverride?.Invoke(ImportStep.SlotMemory, i, fd.Fd) ?? fd.Fd;
            var memory = CudaExternalMemory.ImportOpaqueFd(context, raw, export.AllocationSizeBytes, export.DedicatedAllocation);
            if (raw == fd.Fd)
            {
                fd.MarkTransferred();
            }

            _memory[i] = memory;

            hooks?.BeforeImport?.Invoke(ImportStep.SlotMapping, i);
            var mapped = memory.MapBuffer(0, export.ContentSizeBytes);
            _mapped[i] = mapped;
            _producerViews[i] = mapped.AsFrame(export.Width, export.Height, export.Format, export.PitchBytes, color: color);
        }

        _written = ImportTimeline(context, export.WrittenSemaphore, ImportStep.WrittenSemaphore, hooks);
        _writtenHandle = (ulong)_written.Handle;
        _released = ImportTimeline(context, export.ReleasedSemaphore, ImportStep.ReleasedSemaphore, hooks);
        _releasedHandle = (ulong)_released.Handle;
    }

    private static CudaExternalSemaphore ImportTimeline(CudaContext context, ExportedFd fd, ImportStep step, BridgeTestHooks? hooks)
    {
        hooks?.BeforeImport?.Invoke(step, -1);
        var raw = hooks?.FdOverride?.Invoke(step, -1, fd.Fd) ?? fd.Fd;
        var semaphore = CudaExternalSemaphore.ImportTimelineFd(context, raw);
        if (raw == fd.Fd)
        {
            fd.MarkTransferred();
        }

        return semaphore;
    }
}
