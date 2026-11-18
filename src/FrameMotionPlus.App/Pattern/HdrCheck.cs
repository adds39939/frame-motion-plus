using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.App.Pattern;

/// <summary>Configuration of <c>fmp hdr-pattern --check</c>.</summary>
internal sealed record HdrCheckOptions
{
    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    /// <summary>Generate a frame between two identical ones with VFG and compare it with the real frame (needs the SDK).</summary>
    public bool Vfg { get; init; } = true;

    public FrameGenQuality Quality { get; init; } = FrameGenQuality.Medium;

    public string? SdkDirectory { get; init; }

    public int PeakNits { get; init; } = ColorMetadata.DefaultPeakNits;

    public int? MaxCll { get; init; }

    public int MaxFall { get; init; }
}

/// <summary>One line of the check's report: what was checked, whether it passed, the numbers.</summary>
internal sealed record HdrCheckLine(string Name, bool Pass, string Detail);

/// <summary>
/// <c>fmp hdr-pattern --check</c>: the HDR path proved by numbers, through the real code: each HDR pattern encoded as a
/// console would send it (P010, limited range, BT.2020 matrix, 4:2:0), uploaded and converted by the capture's CUDA
/// kernel into RGB10A2, written into the presentation ring as a real frame and as a VFG frame generated between two
/// identical frames, presented by the Vulkan presenter (following the frames' colour, as playback does) to an offscreen
/// HDR10 target and read back. Checked: the conversion bit-exact against the exact CPU reference; the presented real frame
/// bit-exact against the conversion; the generated frame within a stated tolerance; scaling against a CPU linear-light
/// reference; the HDR metadata handed to Vulkan; an SDR picture staying SDR (10-bit and 8-bit) and the switch back.
/// </summary>
internal sealed class HdrCheck : IDisposable
{
    /// <summary>
    /// A generated frame between two identical frames: inside the pattern's features (flat areas and the ramp, away from
    /// edges) it may differ by this much (codes, per channel; VFG reproduces flat areas exactly and the ramp's one-code
    /// steps within two), and its mean signed difference over the whole frame by <see cref="VfgBiasToleranceCodes"/>. A
    /// change of transfer or range would move every code by tens to hundreds; VFG's own artefacts on a few high-contrast
    /// edges are reported, not judged.
    /// </summary>
    public const int VfgToleranceCodes = 2;

    public const double VfgBiasToleranceCodes = 0.05;

    /// <summary>Scaled PQ against the CPU linear-light reference (codes): float precision and the target's rounding.</summary>
    public const double ScaleToleranceCodes = 1.0;

    private readonly HdrCheckOptions _o;
    private readonly List<HdrCheckLine> _lines = [];
    private VulkanInstance? _instance;
    private VulkanDevice? _device;
    private OffscreenTarget? _target;
    private VulkanPresenter? _presenter;
    private CudaContext? _cuda;
    private CudaVulkanBridge? _bridge;
    private ImageKernels? _kernels;
    private CudaStream? _stream;
    private VfgGenerator? _vfg;
    private CudaDeviceBuffer? _planes;
    private CudaDeviceBuffer? _frameA;
    private CudaDeviceBuffer? _frameB;
    private long _slotIndex;
    private long _contentNs;

    public HdrCheck(HdrCheckOptions options)
    {
        _o = options;
    }

    public string Device { get; private set; } = "";

    public string? SdkDescription { get; private set; }

    public IReadOnlyList<HdrCheckLine> Run(TextWriter? progress)
    {
        Setup();
        var w = _o.Width;
        var h = _o.Height;
        var hdr = ColorMetadata.Hdr10WithPeak(_o.PeakNits, _o.MaxCll ?? _o.PeakNits, _o.MaxFall);
        var conversion = YuvConversion.Create(MatrixCoefficients.Bt2020NonConstant, ColorRange.Limited, 10, ChromaSiting.Left, PixelFormat.Rgb10A2);
        var reference = YuvReference.Create(MatrixCoefficients.Bt2020NonConstant, ColorRange.Limited, 10, ChromaSiting.Left, PixelFormat.Rgb10A2);
        uint[]? rampFrame = null;
        uint[]? peakFrame = null;
        foreach (var kind in (HdrPatternKind[])[HdrPatternKind.Ramp, HdrPatternKind.Peak, HdrPatternKind.Bars, HdrPatternKind.Black])
        {
            progress?.WriteLine($"  checking    {HdrPatterns.Name(kind)}");
            var intended = HdrPatterns.Render(kind, w, h);
            var (y, cb, cr) = HdrPatterns.ToYcbcr(intended, w, h, MatrixCoefficients.Bt2020NonConstant, 10);
            var converted = Convert(y, cb, cr, conversion, PixelFormat.Rgb10A2);
            var expected = new uint[w * h];
            reference.Convert(y, cb, cr, w, h, expected);
            var mismatches = Mismatches(converted, expected);
            Add($"{HdrPatterns.Name(kind)}: P010 → RGB10A2", mismatches == 0,
                $"the capture's conversion kernel against the exact CPU reference (BT.2020 matrix, limited range, chroma left-sited): {mismatches} of {w * h} pixels differ");
            Add($"{HdrPatterns.Name(kind)}: PQ codes kept", true, IntendedVersusConverted(kind, intended, expected, w, h));
            // The real frame through the ring and the presenter (HDR10, following the frame's colour) and back.
            var shown = Present(_frameA!, hdr, write: null);
            var presentedMismatches = Mismatches(shown, converted);
            Add($"{HdrPatterns.Name(kind)}: presented", presentedMismatches == 0 && _target!.ColorSpace == TargetColorSpace.Hdr10St2084 && _presenter!.IsScalingExact,
                $"real frame through the ring and the presenter to an HDR10 ST 2084 target of the same size (no resampling): {presentedMismatches} pixels differ from the conversion; " +
                $"target {_target!.ColorSpace}, transform {_presenter!.ActiveTransform}");
            if (kind == HdrPatternKind.Ramp)
            {
                // Every grey level the signal carries reaches the screen: an 8-bit stage anywhere would leave at most 256.
                var (x0, y0, ww, _) = HdrPatterns.Interiors(kind, w, h)[0];
                var carried = new HashSet<uint>();
                var shownGreys = new HashSet<uint>();
                for (var x = x0; x < x0 + ww; x++)
                {
                    carried.Add(expected[((y0 + 1) * w) + x] & 0x3FF);
                    shownGreys.Add(shown[((y0 + 1) * w) + x] & 0x3FF);
                }

                var provable = carried.Count > 256;
                Add("ramp: 10 bits carried", shownGreys.SetEquals(carried) && (shownGreys.Count > 256 || !provable),
                    $"{shownGreys.Count} distinct grey codes on screen, the same {carried.Count} the signal carries (the ramp's {Math.Min(1024, w / 2)} codes through limited-range " +
                    $"10-bit luma, 877 levels){(provable ? "; more than 256: no stage between capture and swapchain is 8-bit" : "; the frame is too narrow to prove more than 8 bits (give --size at least 1024 wide)")}");
            }
            if (_vfg is not null)
            {
                var generated = Present(_frameA!, hdr, write: GenerateBetweenIdentical);
                var (maxDiff, meanDiff, identical) = Compare(generated, converted);
                var (interiorMax, bias) = InteriorAndBias(kind, generated, converted, w, h);
                Add($"{HdrPatterns.Name(kind)}: VFG frame", interiorMax <= VfgToleranceCodes && Math.Abs(bias) <= VfgBiasToleranceCodes,
                    string.Create(CultureInfo.InvariantCulture,
                        $"VFG {Vfg.VfgReport.ModeName(_o.Quality)} at t = 0.5 between two identical frames, presented, against the real frame: inside the features at most " +
                        $"{interiorMax} code(s) off (tolerance {VfgToleranceCodes}), mean signed difference over the frame {bias:0.0000} codes (tolerance {VfgBiasToleranceCodes}): " +
                        $"the same transfer and range; {identical:0.00}% of pixels identical, mean |difference| {meanDiff:0.0000}, the largest {maxDiff} codes on a few high-contrast edges (VFG's own)"));
            }

            if (kind == HdrPatternKind.Ramp)
            {
                rampFrame = converted;
            }
            else if (kind == HdrPatternKind.Peak)
            {
                peakFrame = converted;
            }
        }

        CheckMetadata(hdr);
        CheckScaling(peakFrame!, hdr, "peak");
        CheckScaling(rampFrame!, hdr, "ramp");
        CheckSdr(hdr);
        return _lines;
    }

    /// <summary>Intended PQ codes against what the YCbCr round trip leaves (inside features): the console's own quantisation.</summary>
    private static string IntendedVersusConverted(HdrPatternKind kind, (int R, int G, int B)[] intended, uint[] converted, int w, int h)
    {
        var worst = 0;
        var worstCode = 0;
        double worstRel = 0;
        double worstAbs = 0;
        foreach (var (x0, y0, ww, hh) in HdrPatterns.Interiors(kind, w, h))
        {
            for (var y = y0; y < y0 + hh; y += 2)
            {
                for (var x = x0; x < x0 + ww; x++)
                {
                    var p = intended[(y * w) + x];
                    var c = converted[(y * w) + x];
                    Span<int> want = [p.R, p.G, p.B];
                    Span<int> got = [(int)(c & 0x3FF), (int)((c >> 10) & 0x3FF), (int)((c >> 20) & 0x3FF)];
                    for (var i = 0; i < 3; i++)
                    {
                        var d = Math.Abs(want[i] - got[i]);
                        if (d > worst)
                        {
                            worst = d;
                            worstCode = want[i];
                        }

                        // Relative luminance from code 64 (0.05 nits) up; below, the absolute error in nits says more.
                        if (want[i] >= 64)
                        {
                            var rel = Math.Abs(HdrPatterns.Nits(got[i] / 1023.0) / HdrPatterns.Nits(want[i] / 1023.0) - 1);
                            worstRel = Math.Max(worstRel, rel);
                        }
                        else
                        {
                            worstAbs = Math.Max(worstAbs, Math.Abs(HdrPatterns.Nits(got[i] / 1023.0) - HdrPatterns.Nits(want[i] / 1023.0)));
                        }
                    }
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"the pattern's PQ codes through 10-bit limited-range YCbCr 4:2:0 (as the console sends it) and back: at most {worst} code(s) off (at code {worstCode}), " +
            $"at most {worstRel * 100:0.00}% in luminance from 0.05 nits up and {worstAbs:0.#####} nits below: the quantisation of the signal itself, not of the app");
    }

    private void CheckMetadata(in ColorMetadata hdr)
    {
        var sent = _target!.HdrMetadataSent;
        var intended = HdrMetadata.For(hdr);
        var ok = sent is { } s && s == intended && s.MaxLuminanceNits == _o.PeakNits && s.MaxContentLightLevel == (_o.MaxCll ?? _o.PeakNits)
            && s.MaxFrameAverageLightLevel == _o.MaxFall && s.Red == new Chromaticity(0.708f, 0.292f) && s.WhitePoint == new Chromaticity(0.3127f, 0.3290f);
        Add("HDR metadata", ok, $"handed to Vulkan (read back from the VkHdrMetadataEXT passed): {(sent is { } m ? m.ToString() : "none")}; intended {intended}");
    }

    /// <summary>The presented frame at other output sizes against a CPU reference that filters in linear light.</summary>
    private void CheckScaling(uint[] frame, in ColorMetadata hdr, string name)
    {
        var w = _o.Width;
        var h = _o.Height;
        _stream!.CopyFrameFromHost(frame, _frameA!.AsFrame());
        _stream.Synchronize();
        foreach (var (tw, th) in ((int, int)[])[(w * 4 / 3, h * 4 / 3), (w / 2, h / 2), (w * 3 / 2, h * 13 / 10)])
        {
            _target!.Resize(tw, th);
            var shown = Present(_frameA, hdr, write: null);
            var rect = ContentRect.Fit(w, h, tw, th);
            double worst = 0, codeAverageWorst = 0, worstNitsOld = 0, worstNitsNew = 0;
            var bars = 0;
            for (var y = 0; y < th; y += 3)
            {
                for (var x = 0; x < tw; x++)
                {
                    var c = shown[(y * tw) + x];
                    if (x < rect.X || x >= rect.X + rect.Width || y < rect.Y || y >= rect.Y + rect.Height)
                    {
                        bars += (c & 0x3FFFFFFF) == 0 ? 0 : 1;
                        continue;
                    }

                    for (var ch = 0; ch < 3; ch++)
                    {
                        var (linear, average) = Bilinear(frame, w, h, rect, x, y, ch * 10);
                        var got = (int)((c >> (ch * 10)) & 0x3FF);
                        worst = Math.Max(worst, Math.Abs(got - linear));
                        var gap = Math.Abs(average - linear);
                        if (gap > codeAverageWorst)
                        {
                            codeAverageWorst = gap;
                            worstNitsNew = HdrPatterns.Nits(linear / 1023);
                            worstNitsOld = HdrPatterns.Nits(average / 1023);
                        }
                    }
                }
            }

            Add($"scaling {name} {w}x{h} → {rect.Width}x{rect.Height}", worst <= ScaleToleranceCodes && bars == 0 && !_presenter!.IsScalingExact,
                string.Create(CultureInfo.InvariantCulture,
                    $"on a {tw}x{th} HDR10 target: largest difference from the CPU linear-light bilinear reference {worst:0.00} codes (tolerance {ScaleToleranceCodes}); " +
                    $"bars {(bars == 0 ? "all code 0 (true black)" : bars + " pixels not black")}; filtering code values instead (the Phase 2 presenter) would be up to " +
                    $"{codeAverageWorst:0} codes off: {worstNitsOld:0.#} nits where linear light gives {worstNitsNew:0.#}"));
        }

        _target!.Resize(w, h);
    }

    /// <summary>An SDR picture (BT.709) stays SDR: 10-bit on the same ring (the output switches), then 8-bit, then back to HDR10.</summary>
    private void CheckSdr(in ColorMetadata hdr)
    {
        var w = _o.Width;
        var h = _o.Height;
        var intended = HdrPatterns.Render(HdrPatternKind.Bars, w, h);
        var (y, cb, cr) = HdrPatterns.ToYcbcr(intended, w, h, MatrixCoefficients.Bt709, 10);
        var converted = Convert(y, cb, cr, YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, 10, ChromaSiting.Left, PixelFormat.Rgb10A2), PixelFormat.Rgb10A2);
        var changes = _presenter!.Stats.OutputColorChanges;
        var shown = Present(_frameA!, ColorMetadata.SdrBt709, write: null);
        var mismatches = Mismatches(shown, converted);
        Add("SDR, 10-bit", mismatches == 0 && _target!.ColorSpace == TargetColorSpace.Sdr && _target.HdrMetadataSent is null && _presenter.Stats.OutputColorChanges == changes + 1,
            $"P010 BT.709 SDR on the same RGB10A2 ring: the output switched to SDR ({_target!.ColorSpace}, {(_target.PixelFormat == PixelFormat.Rgb10A2 ? "10-bit" : "8-bit")} images, " +
            $"no HDR metadata), {mismatches} pixels differ from the conversion; the switch waited {_presenter.LastOutputColorChange.WaitNs / 1e6:0.00} ms for the frames in flight " +
            $"and took {_presenter.LastOutputColorChange.RebuildNs / 1e6:0.00} ms");

        // And back: the next PQ frame switches the output to HDR10 again.
        var back = Present(_frameB!, hdr, write: null);
        Add("back to HDR10", _target.ColorSpace == TargetColorSpace.Hdr10St2084 && _target.HdrMetadataSent is not null && _presenter.Stats.OutputColorChanges == changes + 2,
            $"the next PQ frame switched the output back: {_target.ColorSpace}, metadata {(_target.HdrMetadataSent is null ? "none" : "sent again")}");
        _ = back;

        // 8-bit SDR (an NV12 capture): an RGBA8 ring, an SDR target, every code as converted.
        _bridge!.Configure(new BridgeConfig { Width = w, Height = h, Format = PixelFormat.Rgba8 });
        var intended8 = intended.Select(p => (p.R, p.G, p.B)).ToArray();
        var (y8, cb8, cr8) = HdrPatterns.ToYcbcr(intended8, w, h, MatrixCoefficients.Bt709, 8);
        using var frame8 = CudaDeviceBuffer.AllocateImage(_cuda!, w, h, PixelFormat.Rgba8);
        var converted8 = Convert(y8, cb8, cr8, YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, 8, ChromaSiting.Left, PixelFormat.Rgba8), PixelFormat.Rgba8, frame8);
        var shown8 = Present(frame8, ColorMetadata.SdrBt709, write: null);

        // This target's SDR images are 10-bit: an 8-bit code v lands as v/255 in UNORM10 (within a code of v x 1023/255).
        var off8 = 0;
        for (var i = 0; i < shown8.Length; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = (int)((converted8[i] >> (8 * c)) & 0xFF);
                var got = _target.PixelFormat == PixelFormat.Rgb10A2 ? (int)((shown8[i] >> (10 * c)) & 0x3FF) : (int)((shown8[i] >> (8 * c)) & 0xFF);
                var want = _target.PixelFormat == PixelFormat.Rgb10A2 ? v * 1023.0 / 255 : v;
                off8 += Math.Abs(got - want) <= 1.0 ? 0 : 1;
            }
        }

        Add("SDR, 8-bit", off8 == 0 && _target.ColorSpace == TargetColorSpace.Sdr && _target.HdrMetadataSent is null,
            $"NV12 BT.709 SDR on an RGBA8 ring: {_target.ColorSpace} target ({(_target.PixelFormat == PixelFormat.Rgb10A2 ? "10-bit images: each 8-bit code within one code of v x 1023/255" : "8-bit images")}), " +
            $"{off8} channel values off");
    }

    private void Setup()
    {
        Require(OperatingSystem.IsLinux(), "needs Linux (CUDA/Vulkan interop through file descriptors)");
        Require(CudaDriver.TryInitialize(out var reason), $"needs an NVIDIA GPU with the CUDA driver: {reason}");
        Require(CudaDriver.DeviceCount > 0, "needs an NVIDIA GPU: the CUDA driver reports no device");
        var cudaDevice = CudaDriver.GetDevice(0);
        Device = cudaDevice.Name;
        var vfg = _o.Vfg && VfxSdk.TryLoad(out _, _o.SdkDirectory, null);
        if (_o.Vfg && !vfg)
        {
            Add("VFG", false, "the VFX SDK was not found (--no-vfg checks without it)");
        }

        if (vfg)
        {
            SdkDescription = $"VFX SDK {VfxSdk.Version} from {VfxSdk.Location}";
        }

        VulkanLayerPolicy.Isolated.ApplyToProcess();
        Require(VulkanLoader.TryInitialize(out var loaderError), $"needs the Vulkan loader: {loaderError}");
        try
        {
            _instance = VulkanInstance.Create(new VulkanInstanceOptions { ApplicationName = "fmp hdr-pattern", Layers = VulkanLayerPolicy.Isolated });
            _device = VulkanDevice.Create(_instance, GpuIdentity.VulkanSelectorFor(cudaDevice));
        }
        catch (Exception e) when (e is VulkanDeviceNotFoundException or VulkanException)
        {
            throw new HardwareUnavailableException($"needs a Vulkan device on the CUDA GPU: {e.Message.Split('\n')[0]}", e);
        }

        // SDR images in 10 bits, so a 10-bit SDR picture is compared code for code (a swapchain does the same: it takes a
        // 10-bit SDR format for 10-bit content where the surface offers one).
        _target = new OffscreenTarget(_device, new OffscreenTargetOptions { Width = _o.Width, Height = _o.Height, SdrFormat = PixelFormat.Rgb10A2 });
        _presenter = new VulkanPresenter(_device, _target, new VulkanPresenterOptions { FollowFrameColor = true });
        _cuda = CudaContext.RetainPrimary(cudaDevice);
        var hdr = ColorMetadata.Hdr10WithPeak(_o.PeakNits, _o.MaxCll ?? _o.PeakNits, _o.MaxFall);
        _bridge = new CudaVulkanBridge(_cuda, _presenter, new CudaVulkanBridgeOptions { OperationsPerWrite = 192, MaxLead = 4, OutputColor = hdr });
        _bridge.Configure(new BridgeConfig { Width = _o.Width, Height = _o.Height, Format = PixelFormat.Rgb10A2 });
        _kernels = ImageKernels.Load(_cuda);
        _stream = CudaStream.Create(_cuda, CudaStreamPriority.High);
        _planes = CudaDeviceBuffer.Allocate(_cuda, (long)_o.Width * 2 * (_o.Height + ((_o.Height + 1) / 2)));
        _frameA = CudaDeviceBuffer.AllocateImage(_cuda, _o.Width, _o.Height, PixelFormat.Rgb10A2);
        _frameB = CudaDeviceBuffer.AllocateImage(_cuda, _o.Width, _o.Height, PixelFormat.Rgb10A2);
        if (vfg)
        {
            _vfg = new VfgGenerator(_cuda, new VfgOptions { Stream = _stream });
            _vfg.Configure(new FrameGenConfig { Width = _o.Width, Height = _o.Height, Format = PixelFormat.Rgb10A2, Quality = _o.Quality, Color = hdr });
        }
    }

    /// <summary>Uploads the planes (P010 or NV12 layout) and runs the capture's conversion into <paramref name="into"/> (frame A by default); returns its pixels.</summary>
    private uint[] Convert(ushort[] y, ushort[] cb, ushort[] cr, in YuvConversion conversion, PixelFormat format, CudaDeviceBuffer? into = null)
    {
        var w = _o.Width;
        var h = _o.Height;
        var sixteen = conversion.SourceBits > 8;
        var bytesPerSample = sixteen ? 2 : 1;
        var pitch = w * bytesPerSample;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var host = new byte[(pitch * h) + (pitch * ch)];
        for (var i = 0; i < w * h; i++)
        {
            Put(host, i * bytesPerSample, y[i], sixteen);
        }

        for (var j = 0; j < ch; j++)
        {
            for (var i = 0; i < cw; i++)
            {
                var at = (pitch * h) + (j * pitch) + (2 * i * bytesPerSample);
                Put(host, at, cb[(j * cw) + i], sixteen);
                Put(host, at + bytesPerSample, cr[(j * cw) + i], sixteen);
            }
        }

        var stream = _stream!;
        stream.CopyFromHost<byte>(host, _planes!.Address);
        var planes = new YuvPlanes
        {
            Luma = _planes.Address,
            LumaPitch = pitch,
            Chroma = _planes.Address + (ulong)(pitch * h),
            ChromaPitch = pitch,
            Width = w,
            Height = h,
            Format = sixteen ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12,
        };
        var target = into ?? _frameA!;
        _kernels!.EnqueueYuvToRgb(stream, planes, target.AsFrame(), conversion);
        if (into is null)
        {
            stream.CopyAsync(_frameB!.Address, _frameA!.Address, _frameA.SizeBytes);
        }

        var pixels = new uint[w * h];
        stream.CopyFrameToHost(target.AsFrame(), pixels);
        stream.Synchronize();
        _ = format;
        return pixels;
    }

    private static void Put(byte[] host, int at, ushort code, bool sixteen)
    {
        if (sixteen)
        {
            BitConverter.TryWriteBytes(host.AsSpan(at), (ushort)(code << 6));
        }
        else
        {
            host[at] = (byte)code;
        }
    }

    /// <summary>
    /// Writes a ring slot (a copy of <paramref name="source"/>, or what <paramref name="write"/> enqueues), presents it as
    /// content in <paramref name="color"/> and reads the target back.
    /// </summary>
    private uint[] Present(CudaDeviceBuffer source, in ColorMetadata color, Action<BridgeSlot>? write)
    {
        var bridge = _bridge!;
        var stream = _stream!;
        var slot = bridge.AcquireSlot();
        if (write is null)
        {
            stream.Wait(slot.ProducerWait);
            stream.CopyFrameAsync(source.AsFrame(), slot.ProducerView);
            stream.Signal(slot.ProducerSignal);
        }
        else
        {
            write(slot);
        }

        var request = bridge.CreatePresentRequest(slot, _slotIndex++, 0);
        request = request with { Frame = request.Frame with { Color = color } };
        var start = Environment.TickCount64;
        while (!_presenter!.TryPresent(request))
        {
            if (Environment.TickCount64 - start > 5000)
            {
                throw new TimeoutException("The presenter refused the check's frame for 5 s.");
            }

            Thread.Sleep(1);
        }

        var bytes = new byte[_target!.ReadbackSizeBytes];
        if (_target.ReadLatest(bytes) == 0)
        {
            throw new InvalidOperationException("Nothing was read back.");
        }

        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    /// <summary>VFG at t = 0.5 between frame A and frame B (identical pixels; content times one frame apart) into the slot.</summary>
    private void GenerateBetweenIdentical(BridgeSlot slot)
    {
        var a = _frameA!.AsFrame(ColorMetadata.Hdr10, contentTimeNs: _contentNs += 33_333_333);
        var b = _frameB!.AsFrame(ColorMetadata.Hdr10, contentTimeNs: _contentNs += 33_333_333);
        _vfg!.Enqueue(a, b, 0.5f, slot.ProducerView, slot.ProducerSignal);
    }

    /// <summary>Linear-light and code-value bilinear of one channel (bit offset <paramref name="shift"/>) at target pixel (x, y).</summary>
    private static (double Linear, double Average) Bilinear(uint[] frame, int w, int h, ContentRect rect, int x, int y, int shift)
    {
        var sx = ((x + 0.5 - rect.X) / rect.Width * w) - 0.5;
        var sy = ((y + 0.5 - rect.Y) / rect.Height * h) - 0.5;
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        int At(int px, int py) => (int)((frame[(Math.Clamp(py, 0, h - 1) * w) + Math.Clamp(px, 0, w - 1)] >> shift) & 0x3FF);
        var c00 = At(x0, y0);
        var c10 = At(x0 + 1, y0);
        var c01 = At(x0, y0 + 1);
        var c11 = At(x0 + 1, y0 + 1);
        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;
        var linear = (w00 * Lin(c00)) + (w10 * Lin(c10)) + (w01 * Lin(c01)) + (w11 * Lin(c11));
        return (HdrPatterns.Pq(linear) * 1023, (w00 * c00) + (w10 * c10) + (w01 * c01) + (w11 * c11));
    }

    private static readonly double[] Linear = BuildLinear();

    private static double Lin(int code) => Linear[code];

    private static double[] BuildLinear()
    {
        var t = new double[1024];
        for (var i = 0; i < 1024; i++)
        {
            t[i] = HdrPatterns.Nits(i / 1023.0);
        }

        return t;
    }

    private static int Mismatches(uint[] a, uint[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            n += a[i] == b[i] ? 0 : 1;
        }

        return n;
    }

    /// <summary>The largest difference inside the pattern's features, and the mean signed difference (a minus b) over the whole frame.</summary>
    private static (int InteriorMax, double Bias) InteriorAndBias(HdrPatternKind kind, uint[] a, uint[] b, int w, int h)
    {
        var max = 0;
        foreach (var (x0, y0, ww, hh) in HdrPatterns.Interiors(kind, w, h))
        {
            // Two pixels further in than the features' edges: VFG's support near an edge.
            for (var y = y0 + 4; y < y0 + hh - 4; y++)
            {
                for (var x = x0 + 4; x < x0 + ww - 4; x++)
                {
                    for (var s = 0; s < 30; s += 10)
                    {
                        var d = Math.Abs((int)((a[(y * w) + x] >> s) & 0x3FF) - (int)((b[(y * w) + x] >> s) & 0x3FF));
                        max = Math.Max(max, d);
                    }
                }
            }
        }

        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            for (var s = 0; s < 30; s += 10)
            {
                sum += (int)((a[i] >> s) & 0x3FF) - (int)((b[i] >> s) & 0x3FF);
            }
        }

        return (max, sum / (3.0 * a.Length));
    }

    private static (int Max, double Mean, double IdenticalPercent) Compare(uint[] a, uint[] b)
    {
        var max = 0;
        long sum = 0;
        var same = 0;
        for (var i = 0; i < a.Length; i++)
        {
            same += a[i] == b[i] ? 1 : 0;
            for (var s = 0; s < 30; s += 10)
            {
                var d = Math.Abs((int)((a[i] >> s) & 0x3FF) - (int)((b[i] >> s) & 0x3FF));
                max = Math.Max(max, d);
                sum += d;
            }
        }

        return (max, sum / (3.0 * a.Length), 100.0 * same / a.Length);
    }

    private void Add(string name, bool pass, string detail) => _lines.Add(new HdrCheckLine(name, pass, detail));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new HardwareUnavailableException(message);
        }
    }

    public void Dispose()
    {
        try
        {
            _stream?.Synchronize();
        }
        catch (CudaException)
        {
        }

        _bridge?.Dispose();
        _vfg?.Dispose();
        _planes?.Dispose();
        _frameA?.Dispose();
        _frameB?.Dispose();
        _stream?.Dispose();
        _kernels?.Dispose();
        _presenter?.Dispose();
        _target?.Dispose();
        _ = CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(3));
        _cuda?.Dispose();
        _device?.Dispose();
        _instance?.Dispose();
    }
}
