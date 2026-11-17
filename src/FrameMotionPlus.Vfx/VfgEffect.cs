using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Interop;

namespace FrameMotionPlus.Vfx;

/// <summary>VFG's quality mode (<c>NVVFXVIDEOFRAMEGENERATION_MODE_*</c>); set before load, changing it needs a reload.</summary>
public enum VfgMode
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>What one loaded VFG instance is built for: everything that needs a reload to change.</summary>
public readonly record struct VfgInstanceKey(int Width, int Height, PixelFormat Format, VfgMode Mode)
{
    public static VfgMode ModeOf(FrameGenQuality quality) => quality switch
    {
        FrameGenQuality.Low => VfgMode.Low,
        FrameGenQuality.High => VfgMode.High,
        _ => VfgMode.Medium,
    };

    public static VfgInstanceKey Of(in FrameGenConfig config) => new(config.Width, config.Height, config.Format, ModeOf(config.Quality));

    public override string ToString() => $"{Width}x{Height} {Format} {Mode}";
}

/// <summary>
/// One VFG effect instance (<c>NvVFX_Handle</c>), loaded for one <see cref="VfgInstanceKey"/> on one CUDA stream. Its
/// three image descriptors are pointed at the caller's frames per run (<see cref="Bind"/>), so a run copies no pixels.
/// Not thread-safe; create and run it on one thread at a time.
/// <para>
/// <b>Descriptor lifetime (an invariant this class keeps).</b> <c>NvVFX_SetImage</c> does not copy the
/// <c>NvCVImage</c> it is given, whatever nvVideoEffects.h says: the SDK keeps the caller's pointer and reads the
/// descriptor at every <c>NvVFX_Run</c> (measured: rewriting a descriptor in place, without calling SetImage again,
/// redirected the next run's output; a descriptor on the stack made the next run fail). So the three descriptors live
/// in native memory allocated with the effect and freed only after <c>NvVFX_DestroyEffect</c>, never move, and are only
/// ever rewritten for the next run, after the previous <c>NvVFX_Run</c> returned (a run captures the pixel addresses
/// when it enqueues its kernels, so a queued run is not affected).
/// </para>
/// <para>
/// Measured (SDK 1.3.0.0, driver 620.70.06): the CUDA stream and the mode are fixed at load, setting either afterwards
/// makes every later run fail with NVCV_ERR_INITIALIZATION until the effect is loaded again. <c>NvVFX_Load</c> and
/// <c>NvVFX_DestroyEffect</c> wait for all work queued in the context and block other threads' CUDA calls meanwhile;
/// runs do neither (see docs/benchmarks/phase3-vfg.md).
/// </para>
/// </summary>
internal sealed unsafe class VfgEffect : IDisposable
{
    internal const int Previous = 0;
    internal const int Current = 1;
    internal const int Output = 2;

    private readonly NvCVImage* _images;
    private readonly ulong[] _bound = new ulong[3];
    private readonly int[] _boundPitch = new int[3];
    private nint _handle;
    private bool _faulted;

    private VfgEffect(nint handle, NvCVImage* images, VfgInstanceKey key, CudaStream stream, bool automaticShotChange)
    {
        _handle = handle;
        _images = images;
        Key = key;
        Stream = stream;
        AutomaticShotChangeDetection = automaticShotChange;
    }

    public VfgInstanceKey Key { get; }

    public CudaStream Stream { get; }

    public bool AutomaticShotChangeDetection { get; }

    public bool IsLoaded { get; private set; }

    public long LastUsed { get; set; }

    /// <summary>
    /// The generator's last activation of this instance (<see cref="VfgGenerator"/>), kept so switching back to the same
    /// configuration allocates nothing.
    /// </summary>
    public VfgActivation? Activation { get; set; }

    internal nint Handle => _handle;

    /// <summary>Creates the effect and sets everything that must precede <see cref="Load"/>. Nothing is allocated on the GPU yet.</summary>
    public static VfgEffect Create(CudaStream stream, VfgInstanceKey key, bool automaticShotChangeDetection)
    {
        ArgumentNullException.ThrowIfNull(stream);
        VfxSdk.EnsureLoaded();
        stream.Context.EnsureCurrent();
        var mark = VfxLog.Count;
        nint handle;
        Nv.Check(VfxNative.NvVFX_CreateEffect(VfxSelectors.VideoFrameGeneration, &handle), nameof(VfxNative.NvVFX_CreateEffect), mark);
        var images = (NvCVImage*)NativeMemory.AllocZeroed(3, (nuint)sizeof(NvCVImage));
        var effect = new VfgEffect(handle, images, key, stream, automaticShotChangeDetection);
        try
        {
            effect.SetStream(stream);
            effect.SetU32(VfxSelectors.InputWidth, (uint)key.Width);
            effect.SetU32(VfxSelectors.InputHeight, (uint)key.Height);
            effect.SetU32(VfxSelectors.Mode, (uint)key.Mode);
            effect.SetU32(VfxSelectors.AutomaticShotChangeDetectionEnabled, automaticShotChangeDetection ? 1u : 0u);
        }
        catch
        {
            effect.Dispose();
            throw;
        }

        return effect;
    }

    /// <summary><c>NvVFX_Load</c>: builds the model for the key. Synchronises the context (a drained point only).</summary>
    public void Load()
    {
        ThrowIfDisposed();
        Stream.Context.EnsureCurrent();
        var mark = VfxLog.Count;
        Nv.Check(VfxNative.NvVFX_Load(_handle), nameof(VfxNative.NvVFX_Load), mark);
        IsLoaded = true;
    }

    /// <summary>
    /// Points image <paramref name="which"/> (<see cref="Previous"/>, <see cref="Current"/>, <see cref="Output"/>) at
    /// <paramref name="frame"/>'s pixels: the descriptor is rewritten in place and handed to <c>NvVFX_SetImage</c>
    /// again, only when the address or pitch changed. The SDK keeps the pointer to the descriptor (see the class
    /// remarks), which is why the descriptors live as long as the effect. Allocation-free.
    /// </summary>
    public void Bind(int which, in GpuFrame frame)
    {
        var address = frame.Address + frame.Offset;
        if (_bound[which] == address && _boundPitch[which] == frame.PitchBytes)
        {
            return;
        }

        var image = _images + which;
        var (format, type) = Describe(frame.Format);
        var mark = VfxLog.Count;
        Nv.Check(
            VfxFrameCalls.ImageInit(image, (uint)frame.Width, (uint)frame.Height, frame.PitchBytes, (void*)address, format, type, NvCVConstants.Chunky, NvCVConstants.Gpu),
            nameof(VfxNative.NvCVImage_Init), mark);
        Nv.Check(VfxFrameCalls.SetImage(_handle, Selector(which), image), nameof(VfxNative.NvVFX_SetImage), mark);
        _bound[which] = address;
        _boundPitch[which] = frame.PitchBytes;
    }

    /// <summary>The descriptor the SDK holds for image <paramref name="which"/> (tests pin the lifetime invariant with it).</summary>
    internal NvCVImage* Descriptor(int which) => _images + which;

    /// <summary>
    /// True once a run or bind failed: the SDK's state for this instance is unknown, so it is not run again. Set on the
    /// generator thread, read by a thread switching configurations (volatile).
    /// </summary>
    public bool IsFaulted
    {
        get => Volatile.Read(ref _faulted);
        set => Volatile.Write(ref _faulted, value);
    }

    /// <summary>Forgets what is bound, so the next <see cref="Bind"/> of each image hands its descriptor over again.</summary>
    public void InvalidateBindings()
    {
        Array.Clear(_bound);
        Array.Clear(_boundPitch);
    }

    /// <summary>
    /// One run at <paramref name="t"/> in (0, 1) in explicit timestep mode: <c>FrameMultiplier</c> = 0 (which resets the
    /// timestep, so it is set every run), then <c>Timestep</c>, then <c>NvVFX_Run</c>; with <paramref name="shotChange"/>
    /// the manual flag is set for this run and cleared after it. Every status is checked, the clearing one included
    /// (a flag left set would turn every later run into a copy); a failure to clear is reported once the run's own
    /// status has been, so it never hides it. Allocation-free.
    /// </summary>
    public void Run(float t, bool shotChange)
    {
        var handle = _handle;
        var mark = VfxLog.Count;
        if (shotChange)
        {
            Nv.Check(VfxFrameCalls.SetU32(handle, VfxSelectors.ShotChange, 1), nameof(VfxNative.NvVFX_SetU32), mark);
        }

        var multiplier = VfxFrameCalls.SetU32(handle, VfxSelectors.FrameMultiplier, 0);
        var timestep = multiplier == NvCVStatus.Success ? VfxFrameCalls.SetF32(handle, VfxSelectors.Timestep, t) : NvCVStatus.Success;
        var run = multiplier == NvCVStatus.Success && timestep == NvCVStatus.Success ? VfxFrameCalls.Run(handle, 1) : NvCVStatus.Success;
        var clear = shotChange ? VfxFrameCalls.SetU32(handle, VfxSelectors.ShotChange, 0) : NvCVStatus.Success;
        Nv.Check(multiplier, nameof(VfxNative.NvVFX_SetU32), mark);
        Nv.Check(timestep, nameof(VfxNative.NvVFX_SetF32), mark);
        Nv.Check(run, nameof(VfxNative.NvVFX_Run), mark);
        Nv.Check(clear, nameof(VfxNative.NvVFX_SetU32), mark, "clearing the shot-change flag after a run");
    }

    public uint GetU32(byte* selector)
    {
        ThrowIfDisposed();
        uint value;
        var mark = VfxLog.Count;
        Nv.Check(VfxNative.NvVFX_GetU32(_handle, selector, &value), nameof(VfxNative.NvVFX_GetU32), mark);
        return value;
    }

    public void SetU32(byte* selector, uint value)
    {
        ThrowIfDisposed();
        var mark = VfxLog.Count;
        Nv.Check(VfxNative.NvVFX_SetU32(_handle, selector, value), nameof(VfxNative.NvVFX_SetU32), mark);
    }

    /// <summary>
    /// <c>NvVFX_DestroyEffect</c>. Frees the model's CUDA memory, which synchronises the context: never during playback
    /// and never on a pacing thread.
    /// </summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0)
        {
            return;
        }

        // The stream keeps the context alive; make it current so the SDK frees in it (it may be disposed by its owner
        // already, and the release path must not throw).
        _ = TryEnsureCurrent(Stream.Context);
        VfxNative.NvVFX_DestroyEffect(handle);
        NativeMemory.Free(_images);
    }

    internal static (NvCVImagePixelFormat Format, NvCVImageComponentType Type) Describe(PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 => (NvCVImagePixelFormat.Rgba, NvCVImageComponentType.U8),
        PixelFormat.Bgra8 => (NvCVImagePixelFormat.Bgra, NvCVImageComponentType.U8),
        PixelFormat.Rgb10A2 => (NvCVImagePixelFormat.Rgb10A2, NvCVImageComponentType.P32),
        _ => ThrowFormat(format),
    };

    private void SetStream(CudaStream stream)
    {
        var mark = VfxLog.Count;
        Nv.Check(VfxNative.NvVFX_SetCudaStream(_handle, VfxSelectors.CudaStream, stream.Handle), nameof(VfxNative.NvVFX_SetCudaStream), mark);
    }

    private static byte* Selector(int which) => which switch
    {
        Previous => VfxSelectors.InputImage0,
        Current => VfxSelectors.InputImage1,
        _ => VfxSelectors.OutputImage0,
    };

    private static bool TryEnsureCurrent(CudaContext context)
    {
        try
        {
            context.EnsureCurrent();
            return true;
        }
        catch (Exception e) when (e is CudaException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);

    [DoesNotReturn]
    private static (NvCVImagePixelFormat, NvCVImageComponentType) ThrowFormat(PixelFormat format) =>
        throw new ArgumentOutOfRangeException(nameof(format), format, "VFG takes RGBA8, BGRA8 or RGB10A2.");
}
