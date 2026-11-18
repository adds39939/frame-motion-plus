using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>A V4L2 capture device and everything it offers.</summary>
public sealed record CaptureDeviceDescription
{
    public required V4l2DeviceInfo Info { get; init; }

    /// <summary>Every pixel format with its sizes and rates (compressed ones included, to say they are not used).</summary>
    public required IReadOnlyList<(V4l2FormatDescription Format, IReadOnlyList<V4l2FrameSize> Sizes)> Formats { get; init; }

    /// <summary>The modes this source can capture (uncompressed NV12, P010, YUYV).</summary>
    public IReadOnlyList<CaptureMode> Modes => CaptureFormats.Modes(Formats);

    /// <summary>The device's current format, as it would capture now.</summary>
    public string? Current { get; init; }

    /// <summary>The current input and its status, when the driver reports them.</summary>
    public string? Input { get; init; }
}

/// <summary>Finding and describing V4L2 capture devices (<c>/dev/video*</c>).</summary>
public static class CaptureDevices
{
    /// <summary>Whether V4L2 capture can work in this process; false with the reason otherwise (Windows, macOS, 32-bit).</summary>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsAvailable([NotNullWhen(false)] out string? reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "capture needs Linux (V4L2); Windows capture (Media Foundation) is Phase 10";
            return false;
        }

        if (!Environment.Is64BitProcess)
        {
            reason = "capture needs a 64-bit process (the V4L2 structures are laid out for LP64)";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>The video device nodes, in numeric order.</summary>
    public static IReadOnlyList<string> Nodes()
    {
        if (!Directory.Exists("/dev"))
        {
            return [];
        }

        return Directory.GetFiles("/dev", "video*")
            .Select(p => (Path: p, Number: int.TryParse(Path.GetFileName(p).AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue))
            .Where(p => p.Number != int.MaxValue)
            .OrderBy(p => p.Number)
            .Select(p => p.Path)
            .ToList();
    }

    /// <summary>Every node that is a single-planar streaming capture device, described (nodes that cannot be opened are skipped).</summary>
    [SupportedOSPlatform("linux")]
    public static List<CaptureDeviceDescription> List()
    {
        var list = new List<CaptureDeviceDescription>();
        foreach (var node in Nodes())
        {
            try
            {
                var d = Describe(node);
                if (d.Info.IsVideoCapture)
                {
                    list.Add(d);
                }
            }
            catch (Exception e) when (e is CaptureUnavailableException or V4l2Exception)
            {
                // Not ours to report: a node that vanished or that this user cannot open.
            }
        }

        return list;
    }

    /// <summary>Opens <paramref name="path"/> (no streaming, nothing changed) and enumerates what it offers.</summary>
    [SupportedOSPlatform("linux")]
    public static CaptureDeviceDescription Describe(string path)
    {
        using var device = V4l2Device.Open(path);
        return Describe(device);
    }

    [SupportedOSPlatform("linux")]
    internal static CaptureDeviceDescription Describe(V4l2Device device)
    {
        var info = device.QueryCapabilities();
        var formats = new List<(V4l2FormatDescription, IReadOnlyList<V4l2FrameSize>)>();
        string? current = null;
        string? input = null;
        if (info.IsVideoCapture)
        {
            foreach (var f in device.EnumerateFormats())
            {
                formats.Add((f, device.EnumerateSizes(f.FourCc)));
            }

            try
            {
                var pix = device.GetFormat();
                var interval = device.GetFrameInterval(out _);
                current = string.Create(CultureInfo.InvariantCulture,
                    $"{pix.Width}x{pix.Height} {V4l2FormatDescription.FourCcText(pix.PixelFormat)} @ {interval} fps; colour " +
                    $"{CaptureColorMapping.ColorspaceName(pix.Colorspace)} / {CaptureColorMapping.XferName(pix.XferFunc)} / " +
                    $"{CaptureColorMapping.EncodingName(pix.YcbcrEnc)} / {CaptureColorMapping.QuantizationName(pix.Quantization)}");
            }
            catch (V4l2Exception)
            {
            }

            if (device.TryGetInputStatus(out var index, out var status, out var name))
            {
                input = string.Create(CultureInfo.InvariantCulture, $"{index} ({name}): {InputStatusText(status)}");
            }
        }

        return new CaptureDeviceDescription { Info = info, Formats = formats, Current = current, Input = input };
    }

    /// <summary>
    /// The device to capture from when none is named: the first capture device whose modes include P010 (10-bit, what a
    /// PS5 in HDR needs), else the first capture device with any supported mode. Null when there is none.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static string? Default(IReadOnlyList<CaptureDeviceDescription> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.FirstOrDefault(d => d.Modes.Any(m => m.Format == CapturePixelFormat.P010))?.Info.Path
            ?? devices.FirstOrDefault(d => d.Modes.Count > 0)?.Info.Path;
    }

    public static string InputStatusText(uint status)
    {
        if (status == 0)
        {
            return "ok";
        }

        var parts = new List<string>();
        if ((status & Interop.V4l2Defs.InStNoPower) != 0)
        {
            parts.Add("no power");
        }

        if ((status & Interop.V4l2Defs.InStNoSignal) != 0)
        {
            parts.Add("no signal");
        }

        if ((status & Interop.V4l2Defs.InStNoSync) != 0)
        {
            parts.Add("no sync");
        }

        if (parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"status 0x{status:x}"));
        }

        return string.Join(", ", parts);
    }
}
