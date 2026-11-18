using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;

namespace FrameMotionPlus.Vulkan.Tests.Support;

/// <summary>Presenter + offscreen target + diagnostic producer (on its own device), wired the way the app will be.</summary>
public sealed class OffscreenRig : IDisposable
{
    private readonly GpuContext _context;
    private long _outputSlot;
    private int _ringGeneration;

    private OffscreenRig(GpuContext context, OffscreenTarget target, VulkanPresenter presenter)
    {
        _context = context;
        Target = target;
        Presenter = presenter;
    }

    public OffscreenTarget Target { get; }

    public VulkanPresenter Presenter { get; }

    public DiagnosticRingProducer Producer { get; private set; } = null!;

    public PresentationRing Ring => Presenter.Ring!;

    public static OffscreenRig Create(
        GpuContext context,
        int ringWidth,
        int ringHeight,
        PixelFormat format,
        OffscreenTargetOptions? target = null,
        VulkanPresenterOptions? presenter = null,
        ColorMetadata outputColor = default)
    {
        var t = new OffscreenTarget(context.Device, target ?? new OffscreenTargetOptions { Width = ringWidth, Height = ringHeight });
        VulkanPresenter p;
        try
        {
            p = new VulkanPresenter(context.Device, t, presenter);
        }
        catch
        {
            t.Dispose();
            throw;
        }

        var rig = new OffscreenRig(context, t, p);
        try
        {
            rig.Configure(ringWidth, ringHeight, format, outputColor);
            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>Reconfigures the presenter; when that replaced the ring, re-imports it into a new producer.</summary>
    public void Configure(int width, int height, PixelFormat format, ColorMetadata outputColor = default)
    {
        Presenter.Configure(new PresenterConfig { Width = width, Height = height, Format = format, OutputColor = outputColor });
        if (Presenter.RingGeneration != _ringGeneration)
        {
            Producer?.Dispose();
            using var export = Ring.Export();
            Producer = DiagnosticRingProducer.Create(_context.Instance, export);
            _ringGeneration = Presenter.RingGeneration;
        }
    }

    public PresentRequest Request(ulong write) => new()
    {
        SlotIndex = _outputSlot++,
        Frame = Ring.ConsumerFrame(write),
        TargetTimeNs = 0,
    };

    /// <summary>A request whose frame says its content is in <paramref name="color"/> (for a presenter that follows frame colours).</summary>
    public PresentRequest Request(ulong write, in ColorMetadata color) => new()
    {
        SlotIndex = _outputSlot++,
        Frame = Ring.ConsumerFrame(write) with { Color = color },
        TargetTimeNs = 0,
    };

    /// <summary>Writes ring-layout pixels and presents them as content in <paramref name="color"/>.</summary>
    public ulong PresentPixels(ReadOnlySpan<byte> ringLayoutPixels, in ColorMetadata color)
    {
        WaitReady();
        var n = Producer.WritePixels(ringLayoutPixels);
        Assert.True(Presenter.TryPresent(Request(n, color)), $"write {n} was not presented");
        return n;
    }

    /// <summary>Spins until the presenter has a free frame slot. Allocation-free unless it times out.</summary>
    public void WaitReady(long timeoutMs = 5000)
    {
        var start = Stopwatch.GetTimestamp();
        while (!Presenter.IsReadyForPresent())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The presenter never became ready.");
            }

            Thread.SpinWait(64);
        }
    }

    public ulong WriteAndPresent(TestPattern pattern, int frame)
    {
        WaitReady();
        var n = Producer.WritePattern(pattern, frame);
        Assert.True(Presenter.TryPresent(Request(n)), $"write {n} was not presented");
        return n;
    }

    public ulong PresentPixels(ReadOnlySpan<byte> ringLayoutPixels)
    {
        WaitReady();
        var n = Producer.WritePixels(ringLayoutPixels);
        Assert.True(Presenter.TryPresent(Request(n)), $"write {n} was not presented");
        return n;
    }

    /// <summary>The latest presented frame as packed pixels (target format), waiting for it to complete.</summary>
    public uint[] ReadPixels()
    {
        var bytes = new byte[Target.ReadbackSizeBytes];
        Assert.NotEqual(0UL, Target.ReadLatest(bytes));
        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    public void Dispose()
    {
        // Presenter first: it releases everything it was handed, which lets the producer's last writes finish.
        Presenter.Dispose();
        Producer?.Dispose();
        Target.Dispose();
    }
}
