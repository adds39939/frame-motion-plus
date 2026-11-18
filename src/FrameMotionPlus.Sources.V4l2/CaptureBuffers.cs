using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>How the capture buffers are shared between the driver and CUDA.</summary>
public enum CaptureBufferMode
{
    /// <summary>Try <see cref="UserPtrPinned"/>, then <see cref="UserPtrRegistered"/>, then <see cref="MmapRegistered"/>.</summary>
    Auto = 0,

    /// <summary>V4L2 USERPTR into page-aligned CUDA pinned memory (<c>cuMemHostAlloc</c>): the driver writes straight into
    /// memory the GPU copies from by DMA. The design doc's choice.</summary>
    UserPtrPinned = 1,

    /// <summary>V4L2 USERPTR into anonymous page-aligned memory pinned for CUDA with <c>cuMemHostRegister</c> (for a driver
    /// that cannot take the CUDA allocation's pages).</summary>
    UserPtrRegistered = 2,

    /// <summary>The driver's own MMAP buffers, mapped and pinned for CUDA with <c>cuMemHostRegister</c>.</summary>
    MmapRegistered = 3,
}

/// <summary>
/// The capture buffers: their host memory, pinned for CUDA, queued to the driver by index. Built and freed at drained
/// points only (constructor, <see cref="Dispose"/>): freeing pinned memory synchronises the CUDA context.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class CaptureBuffers : IDisposable
{
    private readonly V4l2Device _device;
    private readonly CudaPinnedBuffer?[] _pinned;
    private readonly CudaHostRegistration?[] _registered;
    private readonly nint[] _host;
    private readonly uint[] _length;
    private readonly bool[] _mapped;
    private readonly bool[] _anonymous;
    private bool _disposed;

    private CaptureBuffers(V4l2Device device, int count, CaptureBufferMode mode, uint memory)
    {
        _device = device;
        Mode = mode;
        Memory = memory;
        _pinned = new CudaPinnedBuffer?[count];
        _registered = new CudaHostRegistration?[count];
        _host = new nint[count];
        _length = new uint[count];
        _mapped = new bool[count];
        _anonymous = new bool[count];
    }

    public CaptureBufferMode Mode { get; }

    /// <summary>V4L2_MEMORY_USERPTR or V4L2_MEMORY_MMAP.</summary>
    public uint Memory { get; }

    public int Count => _host.Length;

    /// <summary>Why the modes tried before this one were refused (empty when the first worked).</summary>
    public List<string> Refused { get; } = [];

    public void* Host(int index) => (void*)_host[index];

    public uint Length(int index) => _length[index];

    /// <summary>Allocates <paramref name="count"/> buffers of at least <paramref name="frameBytes"/> in the first mode the
    /// driver accepts, and proves it by queueing each one (then takes them back without streaming).</summary>
    public static CaptureBuffers Create(V4l2Device device, CudaContext context, int count, long frameBytes, CaptureBufferMode mode)
    {
        var refused = new List<string>();
        CaptureBufferMode[] order = mode == CaptureBufferMode.Auto
            ? [CaptureBufferMode.UserPtrPinned, CaptureBufferMode.UserPtrRegistered, CaptureBufferMode.MmapRegistered]
            : [mode];
        foreach (var m in order)
        {
            var buffers = TryCreate(device, context, count, frameBytes, m, out var why);
            if (buffers is not null)
            {
                buffers.Refused.AddRange(refused);
                return buffers;
            }

            refused.Add($"{Name(m)}: {why}");
        }

        throw new CaptureUnavailableException($"{device.Path} accepted no buffer mode ({string.Join("; ", refused)})");
    }

    public static string Name(CaptureBufferMode mode) => mode switch
    {
        CaptureBufferMode.UserPtrPinned => "USERPTR (CUDA pinned memory)",
        CaptureBufferMode.UserPtrRegistered => "USERPTR (anonymous memory registered with CUDA)",
        CaptureBufferMode.MmapRegistered => "MMAP (driver buffers registered with CUDA)",
        _ => "auto",
    };

    /// <summary>Queues buffer <paramref name="index"/> to the driver: 0 or the errno. Allocation-free.</summary>
    public int Queue(int index)
    {
        var b = new V4l2Buffer { Index = (uint)index, Type = V4l2Defs.BufTypeVideoCapture, Memory = Memory };
        if (Memory == V4l2Defs.MemoryUserPtr)
        {
            b.UserPtr = (ulong)_host[index];
            b.Length = _length[index];
        }

        return _device.QueueBuffer(ref b);
    }

    /// <summary>Queues every buffer (before streaming): the first failure as an exception.</summary>
    public void QueueAll()
    {
        for (var i = 0; i < Count; i++)
        {
            var errno = Queue(i);
            if (errno != 0)
            {
                throw new V4l2Exception("VIDIOC_QBUF", errno, _device.Path);
            }
        }
    }

    /// <summary>
    /// Gives the buffers back to the driver's control and frees them: REQBUFS 0 (the driver lets go of the user pages or
    /// its own buffers), then the CUDA pinning and the memory. The pinned frees synchronise the CUDA context: a drained
    /// point only. With <paramref name="cudaToo"/> false only the driver side is released (GPU work may be stuck).
    /// </summary>
    public void Dispose() => Release(cudaToo: true);

    public void Release(bool cudaToo)
    {
        if (_disposed)
        {
            return;
        }

        if (_device.IsOpen)
        {
            _ = _device.StreamOff();
        }

        // MMAP buffers must be unmapped before the driver can free them; unmapping CUDA-registered memory needs the
        // registration gone first. Neither happens when GPU work may be stuck (cudaToo false): the process exits then.
        for (var i = 0; i < Count && cudaToo; i++)
        {
            if (_registered[i] is { } r)
            {
                r.Dispose();
                _registered[i] = null;
            }

            if (_mapped[i] && _host[i] != 0)
            {
                _ = LibC.Munmap((void*)_host[i], _length[i]);
                _mapped[i] = false;
                _host[i] = 0;
            }
        }

        if (_device.IsOpen)
        {
            _ = _device.RequestBuffers(0, Memory, out _);
        }

        if (!cudaToo)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < Count; i++)
        {
            _pinned[i]?.Dispose();
            _pinned[i] = null;
            if (_anonymous[i] && _host[i] != 0)
            {
                _ = LibC.Munmap((void*)_host[i], _length[i]);
                _anonymous[i] = false;
            }

            _host[i] = 0;
        }
    }

    private static CaptureBuffers? TryCreate(V4l2Device device, CudaContext context, int count, long frameBytes, CaptureBufferMode mode, out string? why)
    {
        var memory = mode == CaptureBufferMode.MmapRegistered ? V4l2Defs.MemoryMmap : V4l2Defs.MemoryUserPtr;
        var granted = device.RequestBuffers((uint)count, memory, out _);
        if (granted < 0)
        {
            why = $"VIDIOC_REQBUFS refused it: {LibC.ErrorName(-granted)}";
            return null;
        }

        if (granted < 2)
        {
            _ = device.RequestBuffers(0, memory, out _);
            why = string.Create(CultureInfo.InvariantCulture, $"the driver granted {granted} buffer(s)");
            return null;
        }

        var page = Environment.SystemPageSize;
        var length = (uint)((frameBytes + page - 1) / page * page);
        var buffers = new CaptureBuffers(device, granted, mode, memory);
        try
        {
            for (var i = 0; i < granted; i++)
            {
                switch (mode)
                {
                    case CaptureBufferMode.UserPtrPinned:
                        {
                            var pinned = CudaPinnedBuffer.Allocate(context, length, CudaHostAllocOptions.Portable, alignment: page);
                            buffers._pinned[i] = pinned;
                            buffers._host[i] = (nint)pinned.HostPointer;
                            buffers._length[i] = length;
                            break;
                        }

                    case CaptureBufferMode.UserPtrRegistered:
                        {
                            var p = LibC.Mmap(null, length, LibC.ProtRead | LibC.ProtWrite, LibC.MapPrivate | LibC.MapAnonymous, -1, 0);
                            if (p == LibC.MapFailed)
                            {
                                why = $"mmap of {length} B failed: {LibC.ErrorName(Marshal.GetLastPInvokeError())}";
                                buffers.Release(cudaToo: true);
                                return null;
                            }

                            buffers._host[i] = p;
                            buffers._length[i] = length;
                            buffers._anonymous[i] = true;
                            buffers._registered[i] = CudaHostRegistration.Register(context, (void*)p, length, CudaHostRegisterOptions.Portable);
                            break;
                        }

                    default:
                        {
                            var q = device.QueryBuffer((uint)i, V4l2Defs.MemoryMmap);
                            if (q.Length < frameBytes)
                            {
                                why = string.Create(CultureInfo.InvariantCulture, $"the driver's buffer is {q.Length} B, less than a frame ({frameBytes} B)");
                                buffers.Release(cudaToo: true);
                                return null;
                            }

                            var p = device.Map(q.Offset, q.Length, out var errno);
                            if (p == null)
                            {
                                why = $"mmap of the driver's buffer failed: {LibC.ErrorName(errno)}";
                                buffers.Release(cudaToo: true);
                                return null;
                            }

                            buffers._host[i] = (nint)p;
                            buffers._length[i] = q.Length;
                            buffers._mapped[i] = true;
                            buffers._registered[i] = CudaHostRegistration.Register(context, p, q.Length, CudaHostRegisterOptions.Portable);
                            break;
                        }
                }
            }

            // The proof: the driver must take every buffer (a USERPTR it cannot pin fails here, with EFAULT or EINVAL).
            for (var i = 0; i < granted; i++)
            {
                var errno = buffers.Queue(i);
                if (errno != 0)
                {
                    why = $"VIDIOC_QBUF of buffer {i} failed: {LibC.ErrorName(errno)}";
                    buffers.Release(cudaToo: true);
                    return null;
                }
            }

            // Take them back without streaming; Start queues them again.
            _ = device.StreamOff();
            why = null;
            return buffers;
        }
        catch (CudaException e)
        {
            why = $"CUDA refused the memory: {e.Message}";
            buffers.Release(cudaToo: true);
            return null;
        }
        catch
        {
            buffers.Release(cudaToo: true);
            throw;
        }
    }
}
