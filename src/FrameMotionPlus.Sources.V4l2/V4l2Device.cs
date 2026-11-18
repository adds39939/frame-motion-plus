using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>A V4L2 call failed. <see cref="Errno"/> is the C library's error number.</summary>
public sealed class V4l2Exception : Exception
{
    public V4l2Exception()
    {
    }

    public V4l2Exception(string message)
        : base(message)
    {
    }

    public V4l2Exception(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal V4l2Exception(string operation, int errno, string device)
        : base($"{operation} on {device} failed: {LibC.ErrorName(errno)}")
    {
        Operation = operation;
        Errno = errno;
    }

    public string? Operation { get; }

    public int Errno { get; }
}

/// <summary>The capture device is missing, busy, not a V4L2 capture device, or capture is unavailable on this OS. One line.</summary>
public sealed class CaptureUnavailableException : Exception
{
    public CaptureUnavailableException()
    {
    }

    public CaptureUnavailableException(string message)
        : base(message)
    {
    }

    public CaptureUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The device does not offer what was asked for (a size, rate or pixel format), or offered something else. One line.</summary>
public sealed class CaptureFormatException : Exception
{
    public CaptureFormatException()
    {
    }

    public CaptureFormatException(string message)
        : base(message)
    {
    }

    public CaptureFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What VIDIOC_QUERYCAP says about a device.</summary>
public sealed record V4l2DeviceInfo
{
    public required string Path { get; init; }

    public required string Driver { get; init; }

    public required string Card { get; init; }

    public required string BusInfo { get; init; }

    public uint Capabilities { get; init; }

    public uint DeviceCaps { get; init; }

    /// <summary>A single-planar video capture device that streams (what this source can use).</summary>
    public bool IsVideoCapture => (DeviceCaps & V4l2Defs.CapVideoCapture) != 0 && (DeviceCaps & V4l2Defs.CapStreaming) != 0;

    public bool IsMultiplanarOnly => (DeviceCaps & V4l2Defs.CapVideoCaptureMplane) != 0 && (DeviceCaps & V4l2Defs.CapVideoCapture) == 0;
}

/// <summary>A pixel format a device offers (VIDIOC_ENUM_FMT).</summary>
public readonly record struct V4l2FormatDescription(uint FourCc, string Description, bool Compressed, bool Emulated)
{
    public string Name => FourCcText(FourCc);

    public static string FourCcText(uint fourCc) => string.Create(4, fourCc, static (span, code) =>
    {
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((code >> (8 * i)) & 0xff);
            span[i] = c is >= ' ' and <= '~' ? c : '?';
        }
    });
}

/// <summary>A frame size and the rates offered at it (discrete sizes and intervals; ranges reported as their ends).</summary>
public sealed record V4l2FrameSize(int Width, int Height, IReadOnlyList<V4l2Interval> Intervals, bool Stepwise = false);

/// <summary>A frame interval as V4L2 gives it: <see cref="Numerator"/>/<see cref="Denominator"/> seconds per frame.</summary>
public readonly record struct V4l2Interval(uint Numerator, uint Denominator)
{
    public double Fps => Numerator == 0 ? 0 : (double)Denominator / Numerator;

    public override string ToString() => Fps.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// An open V4L2 device node (<c>/dev/videoN</c>), opened non-blocking. The typed calls throw <see cref="V4l2Exception"/>;
/// the per-frame ones (<see cref="QueueBuffer"/>, <see cref="DequeueBuffer"/>, <see cref="DequeueEvent"/>) return the
/// errno instead, allocation-free. One thread at a time; <see cref="Dispose"/> closes the descriptor (which also stops
/// streaming and releases the buffers in the driver).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class V4l2Device : IDisposable
{
    private int _fd;

    private V4l2Device(int fd, string path)
    {
        _fd = fd;
        Path = path;
    }

    public string Path { get; }

    public int Fd => _fd;

    public bool IsOpen => _fd >= 0;

    /// <summary>Opens <paramref name="path"/> read-write and non-blocking. Missing, inaccessible or not a device: <see cref="CaptureUnavailableException"/>.</summary>
    public static V4l2Device Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        int fd;
        fixed (byte* p = bytes)
        {
            fd = LibC.Open(p, LibC.ORdWr | LibC.ONonBlock | LibC.OCloExec);
        }

        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new CaptureUnavailableException(errno switch
            {
                LibC.Enoent => $"no such capture device {path}",
                LibC.Eacces => $"no permission to open {path} (the desktop user normally gets it through logind; otherwise the 'video' group)",
                LibC.Ebusy => $"{path} is busy (another application has it)",
                _ => $"cannot open {path}: {LibC.ErrorName(errno)}",
            });
        }

        return new V4l2Device(fd, path);
    }

    public V4l2DeviceInfo QueryCapabilities()
    {
        V4l2Capability cap;
        Check(LibC.Ioctl(_fd, V4l2Ioctl.QueryCap, &cap), "VIDIOC_QUERYCAP");
        return new V4l2DeviceInfo
        {
            Path = Path,
            Driver = Text(cap.Driver, 16),
            Card = Text(cap.Card, 32),
            BusInfo = Text(cap.BusInfo, 32),
            Capabilities = cap.Capabilities,
            DeviceCaps = (cap.Capabilities & V4l2Defs.CapDeviceCaps) != 0 ? cap.DeviceCaps : cap.Capabilities,
        };
    }

    public List<V4l2FormatDescription> EnumerateFormats()
    {
        var list = new List<V4l2FormatDescription>();
        for (uint i = 0; i < 256; i++)
        {
            var d = new V4l2FmtDesc { Index = i, Type = V4l2Defs.BufTypeVideoCapture };
            if (LibC.Ioctl(_fd, V4l2Ioctl.EnumFmt, &d) != 0)
            {
                break;
            }

            list.Add(new V4l2FormatDescription(d.PixelFormat, Text(d.Description, 32), (d.Flags & V4l2Defs.FmtFlagCompressed) != 0, (d.Flags & V4l2Defs.FmtFlagEmulated) != 0));
        }

        return list;
    }

    /// <summary>The sizes offered for <paramref name="fourCc"/> with their rates. A stepwise or continuous range gives its two ends.</summary>
    public List<V4l2FrameSize> EnumerateSizes(uint fourCc)
    {
        var list = new List<V4l2FrameSize>();
        for (uint i = 0; i < 1024; i++)
        {
            var s = new V4l2FrmSizeEnum { Index = i, PixelFormat = fourCc };
            if (LibC.Ioctl(_fd, V4l2Ioctl.EnumFrameSizes, &s) != 0)
            {
                break;
            }

            if (s.Type == V4l2Defs.FrmSizeTypeDiscrete)
            {
                list.Add(new V4l2FrameSize((int)s.DiscreteWidth, (int)s.DiscreteHeight, EnumerateIntervals(fourCc, (int)s.DiscreteWidth, (int)s.DiscreteHeight)));
                continue;
            }

            list.Add(new V4l2FrameSize((int)s.MinWidth, (int)s.MinHeight, EnumerateIntervals(fourCc, (int)s.MinWidth, (int)s.MinHeight), Stepwise: true));
            list.Add(new V4l2FrameSize((int)s.MaxWidth, (int)s.MaxHeight, EnumerateIntervals(fourCc, (int)s.MaxWidth, (int)s.MaxHeight), Stepwise: true));
            break;
        }

        return list;
    }

    public List<V4l2Interval> EnumerateIntervals(uint fourCc, int width, int height)
    {
        var list = new List<V4l2Interval>();
        for (uint i = 0; i < 256; i++)
        {
            var v = new V4l2FrmIvalEnum { Index = i, PixelFormat = fourCc, Width = (uint)width, Height = (uint)height };
            if (LibC.Ioctl(_fd, V4l2Ioctl.EnumFrameIntervals, &v) != 0)
            {
                break;
            }

            if (v.Type == V4l2Defs.FrmIvalTypeDiscrete)
            {
                list.Add(new V4l2Interval(v.DiscreteNumerator, v.DiscreteDenominator));
                continue;
            }

            // A range: its fastest and slowest ends.
            list.Add(new V4l2Interval(v.MinNumerator, v.MinDenominator));
            list.Add(new V4l2Interval(v.MaxNumerator, v.MaxDenominator));
            break;
        }

        return list;
    }

    public V4l2PixFormat GetFormat()
    {
        var f = new V4l2Format { Type = V4l2Defs.BufTypeVideoCapture };
        Check(LibC.Ioctl(_fd, V4l2Ioctl.GetFmt, &f), "VIDIOC_G_FMT");
        return f.Pix;
    }

    /// <summary>VIDIOC_S_FMT; returns what the driver set (which may differ from what was asked).</summary>
    public V4l2PixFormat SetFormat(in V4l2PixFormat requested)
    {
        var f = new V4l2Format { Type = V4l2Defs.BufTypeVideoCapture, Pix = requested };
        Check(LibC.Ioctl(_fd, V4l2Ioctl.SetFmt, &f), "VIDIOC_S_FMT");
        return f.Pix;
    }

    /// <summary>The frame interval (seconds per frame), or (0, 0) when the driver does not report one.</summary>
    public V4l2Interval GetFrameInterval(out bool settable)
    {
        var p = new V4l2StreamParm { Type = V4l2Defs.BufTypeVideoCapture };
        if (LibC.Ioctl(_fd, V4l2Ioctl.GetParm, &p) != 0)
        {
            settable = false;
            return default;
        }

        settable = (p.Capability & V4l2Defs.CapTimePerFrame) != 0;
        return new V4l2Interval(p.TimePerFrameNumerator, p.TimePerFrameDenominator);
    }

    /// <summary>VIDIOC_S_PARM with a frame interval; returns what the driver set.</summary>
    public V4l2Interval SetFrameInterval(V4l2Interval interval)
    {
        var p = new V4l2StreamParm
        {
            Type = V4l2Defs.BufTypeVideoCapture,
            TimePerFrameNumerator = interval.Numerator,
            TimePerFrameDenominator = interval.Denominator,
        };
        Check(LibC.Ioctl(_fd, V4l2Ioctl.SetParm, &p), "VIDIOC_S_PARM");
        return new V4l2Interval(p.TimePerFrameNumerator, p.TimePerFrameDenominator);
    }

    /// <summary>VIDIOC_REQBUFS: the number granted (0 frees them), or the errno as a negative number when refused.</summary>
    public int RequestBuffers(uint count, uint memory, out uint capabilities)
    {
        var r = new V4l2RequestBuffers { Count = count, Type = V4l2Defs.BufTypeVideoCapture, Memory = memory };
        if (LibC.Ioctl(_fd, V4l2Ioctl.ReqBufs, &r) != 0)
        {
            capabilities = 0;
            return -Marshal.GetLastPInvokeError();
        }

        capabilities = r.Capabilities;
        return (int)r.Count;
    }

    public V4l2Buffer QueryBuffer(uint index, uint memory)
    {
        var b = new V4l2Buffer { Index = index, Type = V4l2Defs.BufTypeVideoCapture, Memory = memory };
        Check(LibC.Ioctl(_fd, V4l2Ioctl.QueryBuf, &b), "VIDIOC_QUERYBUF");
        return b;
    }

    /// <summary>VIDIOC_QBUF: 0, or the errno (an interrupted call is retried). Allocation-free.</summary>
    public int QueueBuffer(ref V4l2Buffer buffer)
    {
        fixed (V4l2Buffer* b = &buffer)
        {
            return Retried(V4l2Ioctl.QBuf, b);
        }
    }

    /// <summary>VIDIOC_DQBUF (non-blocking): 0, or the errno (EAGAIN: nothing ready; EINTR is retried). Allocation-free.</summary>
    public int DequeueBuffer(ref V4l2Buffer buffer)
    {
        fixed (V4l2Buffer* b = &buffer)
        {
            return Retried(V4l2Ioctl.DqBuf, b);
        }
    }

    public void StreamOn()
    {
        var type = (int)V4l2Defs.BufTypeVideoCapture;
        Check(LibC.Ioctl(_fd, V4l2Ioctl.StreamOn, &type), "VIDIOC_STREAMON");
    }

    /// <summary>VIDIOC_STREAMOFF: 0 or the errno (never throws: it is called on every way out).</summary>
    public int StreamOff()
    {
        var type = (int)V4l2Defs.BufTypeVideoCapture;
        return LibC.Ioctl(_fd, V4l2Ioctl.StreamOff, &type) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>Subscribes to source-change events (a new input resolution or signal); false when the driver has none.</summary>
    public bool TrySubscribeSourceChange()
    {
        var s = new V4l2EventSubscription { Type = V4l2Defs.EventSourceChange };
        return LibC.Ioctl(_fd, V4l2Ioctl.SubscribeEvent, &s) == 0;
    }

    /// <summary>VIDIOC_DQEVENT: 0, or the errno (ENOENT: no event pending; EINTR is retried). Allocation-free.</summary>
    public int DequeueEvent(ref V4l2Event e)
    {
        fixed (V4l2Event* p = &e)
        {
            return Retried(V4l2Ioctl.DqEvent, p);
        }
    }

    /// <summary>An ioctl retried while a signal interrupts it (EINTR): 0 or the errno.</summary>
    private int Retried(nuint request, void* argument)
    {
        while (true)
        {
            if (LibC.Ioctl(_fd, request, argument) == 0)
            {
                return 0;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != LibC.Eintr)
            {
                return errno;
            }
        }
    }

    /// <summary>A control's value; false when the driver has no such control.</summary>
    public bool TryGetControl(uint id, out int value)
    {
        var c = new V4l2Control { Id = id };
        var ok = LibC.Ioctl(_fd, V4l2Ioctl.GetCtrl, &c) == 0;
        value = c.Value;
        return ok;
    }

    /// <summary>Sets a control: 0 or the errno.</summary>
    public int SetControl(uint id, int value)
    {
        var c = new V4l2Control { Id = id, Value = value };
        return LibC.Ioctl(_fd, V4l2Ioctl.SetCtrl, &c) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>The current input's index and status (V4L2_IN_ST_*), when the driver reports them.</summary>
    public bool TryGetInputStatus(out int index, out uint status, out string name)
    {
        int current;
        status = 0;
        name = "";
        if (LibC.Ioctl(_fd, V4l2Ioctl.GetInput, &current) != 0)
        {
            index = -1;
            return false;
        }

        index = current;
        var input = new V4l2Input { Index = (uint)current };
        if (LibC.Ioctl(_fd, V4l2Ioctl.EnumInput, &input) != 0)
        {
            return false;
        }

        status = input.Status;
        name = Text(input.Name, 32);
        return true;
    }

    /// <summary>Selects input <paramref name="index"/>: 0 or the errno.</summary>
    public int SetInput(int index) => LibC.Ioctl(_fd, V4l2Ioctl.SetInput, &index) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Maps buffer memory of an MMAP buffer (shared, read-write); null with the errno on failure.</summary>
    public void* Map(uint offset, uint length, out int errno)
    {
        var p = LibC.Mmap(null, length, LibC.ProtRead | LibC.ProtWrite, LibC.MapShared, _fd, offset);
        errno = p == LibC.MapFailed ? Marshal.GetLastPInvokeError() : 0;
        return p == LibC.MapFailed ? null : (void*)p;
    }

    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            _ = LibC.Close(fd);
        }
    }

    private void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new V4l2Exception(operation, Marshal.GetLastPInvokeError(), Path);
        }
    }

    private static string Text(byte* bytes, int length)
    {
        var n = 0;
        while (n < length && bytes[n] != 0)
        {
            n++;
        }

        return Encoding.UTF8.GetString(bytes, n).Trim();
    }
}
