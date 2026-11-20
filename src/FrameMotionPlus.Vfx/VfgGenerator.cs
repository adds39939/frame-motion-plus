using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;

namespace FrameMotionPlus.Vfx;

/// <summary>Options of a <see cref="VfgGenerator"/>.</summary>
public sealed record VfgOptions
{
    /// <summary>
    /// VFG's automatic shot-change detection (the SDK's default and the design doc's choice; `fmp play` keeps it on,
    /// `fmp capture` turns it off: it takes a game's large, uneven motion for a cut and outputs copies of real frames for
    /// as long as it lasts, docs/benchmarks/shot-change.md). Measured cost: with it on, every <c>NvVFX_Run</c> blocks the calling thread
    /// until the work queued on the generator's stream and the generation itself have finished (spinning, under the
    /// default context scheduling), so <see cref="FrameGenCapabilities.EnqueueMayBlock"/> is set. With it off a run only
    /// enqueues (40 to 90 µs), but a hard cut that nobody reports through
    /// <see cref="IFrameGenerator.NotifyDiscontinuity"/> comes out as a morph of the two shots, neither real frame.
    /// </summary>
    public bool AutomaticShotChangeDetection { get; init; } = true;

    /// <summary>Runs on dummy frames after each load, so the SDK's lazy allocations and first-use work happen then.</summary>
    public int WarmupRuns { get; init; } = 8;

    /// <summary>
    /// Instances kept loaded at once (the design doc keeps RGBA8 and RGB10A2 loaded for SDR/HDR switching). Loading one
    /// more destroys the least recently used, which synchronises the context.
    /// </summary>
    public int MaxLoadedInstances { get; init; } = 2;

    /// <summary>Priority of the stream the generator creates; ignored when <see cref="Stream"/> is given.</summary>
    public CudaStreamPriority StreamPriority { get; init; } = CudaStreamPriority.High;

    /// <summary>A stream of the caller's to generate on (the caller keeps and disposes it); null: one of its own.</summary>
    public CudaStream? Stream { get; init; }
}

/// <summary>What loading one VFG instance took.</summary>
public readonly record struct VfgLoadReport
{
    public VfgInstanceKey Key { get; init; }

    /// <summary><c>NvVFX_CreateEffect</c> and the parameters set before load, wall clock.</summary>
    public long CreateNs { get; init; }

    /// <summary><c>NvVFX_Load</c>, wall clock.</summary>
    public long LoadNs { get; init; }

    /// <summary>The warm-up runs, wall clock until the GPU finished them.</summary>
    public long WarmupNs { get; init; }

    public int WarmupRuns { get; init; }

    /// <summary>Device memory the SDK allocated for the instance (load and warm-up), from the context's free-memory count.</summary>
    public long DeviceBytes { get; init; }

    /// <summary>True for the first VFG load in this process (it includes the SDK's one-time initialisation).</summary>
    public bool FirstInProcess { get; init; }
}

/// <summary>
/// NVIDIA Maxine Video Frame Generation behind <see cref="IFrameGenerator"/>, in the CUDA domain (design doc, "VFG
/// backend details"). Explicit timestep mode for everything; one CUDA stream (high priority, its own unless one is
/// given), handed to VFG as <c>NVVFX_CUDA_STREAM</c>; image descriptors preallocated and pointed at the caller's
/// frames each run, the output straight at the target (a ring slot), so no pixels are copied; automatic shot-change
/// detection on, and the manual flag set for the run after <see cref="NotifyDiscontinuity"/>.
/// <para>
/// <b>Enqueue.</b> Waits GPU-side on <c>prev.Ready</c>, <c>curr.Ready</c> and <c>output.Ready</c>, times a GPU span
/// around the run (<see cref="TryReadGpuTime"/>) and signals <c>done</c> after it, all on <see cref="Stream"/>; VFG does
/// all its work on that stream. t in (0, 1) runs VFG; t = 0 and t = 1, which VFG refuses, copy the real frame.
/// Zero managed allocations per call. <b>With automatic shot-change detection on (the default) the call blocks</b>
/// (<see cref="FrameGenCapabilities.EnqueueMayBlock"/>): the SDK waits inside <c>NvVFX_Run</c> until everything queued
/// on the stream before it (the GPU-side waits included) and the generation itself have finished. A pipeline calls it
/// from a generator thread of its own (see <see cref="IFrameGenerator.Enqueue"/>); see docs/benchmarks/phase3-vfg.md.
/// </para>
/// <para>
/// <b>Discontinuities</b> follow Core's rule (<see cref="DiscontinuityLatch"/>): every call whose pair spans a content
/// time given to <see cref="NotifyDiscontinuity"/> runs with VFG's manual shot-change flag (output: the current frame),
/// t = 0 still shows the previous frame, and the first pair past it ends the notification. A pair's content times must
/// increase.
/// </para>
/// <para>
/// <b>Failure.</b> A call that throws does not signal <c>done</c> (see <see cref="IFrameGenerator.Enqueue"/>,
/// "Failure"). One refused for its arguments enqueued nothing. If the SDK failed (a <see cref="VfxException"/>), the
/// three GPU-side waits were already enqueued on <see cref="Stream"/> and the run may have queued work that partly wrote
/// the output; the instance is marked faulted (<see cref="IsFaulted"/>): later calls throw until <see cref="Configure"/>
/// loads it again (a drained point).
/// </para>
/// <para>
/// <b>Configure and reload.</b> A configuration whose instance is already loaded (<see cref="Prepare"/>) is switched to
/// at once, without any CUDA call, so switching between prepared instances (SDR/HDR) is safe during playback. The
/// active instance and the configuration its frames are checked against are one immutable object, published whole, and
/// <see cref="Enqueue"/> reads it once: a switch from another thread lands before or after a call, never inside one, so
/// a call never runs one instance on frames checked against another's configuration (frames of the old shape after a
/// switch are refused as arguments, which does not fault anything). Loading a new instance is not safe during playback:
/// <c>NvVFX_Load</c> (and destroying an instance) waits for all work queued in the CUDA context and blocks every other
/// thread's CUDA calls meanwhile, so <see cref="Configure"/> of an unprepared configuration, <see cref="Prepare"/>,
/// <see cref="ReleaseInactive"/> and <see cref="Dispose"/> belong to a drained point (production stopped, the ring
/// drained) and never to a pacing thread.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="Enqueue"/> and <see cref="TryReadGpuTime"/> belong to the generator thread.
/// <see cref="TryConfigurePrepared"/> and <see cref="NotifyDiscontinuity"/> may come from any thread, also while that
/// thread enqueues (a pipeline's control thread). <see cref="Configure"/>, <see cref="Prepare"/>,
/// <see cref="ReleaseInactive"/> and <see cref="Dispose"/> are for one thread at a time, never concurrent with each other
/// or with <see cref="TryConfigurePrepared"/>, and never concurrent with <see cref="Enqueue"/> except for a
/// <see cref="Configure"/> that only switches to a prepared instance.
/// </para>
/// </summary>
public sealed class VfgGenerator : IFrameGenerator
{
    private static int _loadsInProcess;

    private readonly bool _ownsStream;
    private readonly CudaSpanTimerRing _timer;
    private readonly List<VfgEffect> _instances = [];
    private readonly DiscontinuityLatch _discontinuity = new();
    private VfgActivation? _active; // the active instance and its configuration: replaced whole, read once per Enqueue
    private bool _disposed;
    private long _useClock;

    public VfgGenerator(CudaContext context, VfgOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        options ??= new VfgOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.WarmupRuns, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLoadedInstances, 1, nameof(options));
        VfxSdk.EnsureLoaded();
        if (options.Stream is { } given)
        {
            given.ThrowIfDisposed();
            if (given.Context.Handle != context.Handle)
            {
                throw new ArgumentException("The stream belongs to another CUDA context.", nameof(options));
            }
        }

        Context = context;
        Options = options;
        Capabilities = CapabilitiesFor(options.AutomaticShotChangeDetection);
        _ownsStream = options.Stream is null;
        Stream = options.Stream ?? CudaStream.Create(context, options.StreamPriority);
        try
        {
            _timer = CudaSpanTimerRing.Create(context, 32);
        }
        catch
        {
            if (_ownsStream)
            {
                Stream.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Max multiplier 8, any t, interpolation only, no scaling, RGBA8/BGRA8/RGB10A2, CUDA device memory;
    /// <see cref="FrameGenCapabilities.EnqueueMayBlock"/> exactly when automatic shot-change detection is on (measured:
    /// the call then returns when the generation has finished; with it off it only enqueues).
    /// </summary>
    public static FrameGenCapabilities CapabilitiesFor(bool automaticShotChangeDetection) => new()
    {
        MaxMultiplier = 8,
        SupportsArbitraryTimestep = true,
        SupportsExtrapolation = false,
        SupportsScaling = false,
        Formats = PixelFormatSet.All,
        Domain = MemoryDomain.CudaDevice,
        EnqueueMayBlock = automaticShotChangeDetection,
    };

    public FrameGenCapabilities Capabilities { get; }

    public CudaContext Context { get; }

    public VfgOptions Options { get; }

    /// <summary>The stream every run, wait, span and <c>done</c> signal goes on, and the one VFG was given.</summary>
    public CudaStream Stream { get; }

    /// <summary>The active configuration (default before <see cref="Configure"/>).</summary>
    public FrameGenConfig Config => Volatile.Read(ref _active)?.Config ?? default;

    public bool IsConfigured => Volatile.Read(ref _active) is not null;

    /// <summary>
    /// True when the active instance failed in the SDK: <see cref="Enqueue"/> throws until <see cref="Configure"/>
    /// (same configuration or another) loads it again.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _active) is { Effect.IsFaulted: true };

    /// <summary>The active instance (tests).</summary>
    internal VfgEffect? ActiveEffect => Volatile.Read(ref _active)?.Effect;

    /// <summary>The most recent load (<see cref="Configure"/> or <see cref="Prepare"/> of a new instance).</summary>
    public VfgLoadReport LastLoad { get; private set; }

    /// <summary>Instances loaded over the generator's life (a Configure to a loaded one adds none).</summary>
    public long Loads { get; private set; }

    /// <summary>Instances currently loaded (the active one and prepared ones).</summary>
    public int LoadedInstances => _instances.Count;

    /// <summary>VFG runs enqueued by <see cref="Enqueue"/> (diagnostics).</summary>
    public long Runs { get; private set; }

    /// <summary>Of those, runs with the manual shot-change flag set (after <see cref="NotifyDiscontinuity"/>).</summary>
    public long ShotChangeRuns { get; private set; }

    /// <summary>Enqueue calls at t = 0 or t = 1, answered with a copy of the real frame.</summary>
    public long RealFrameCopies { get; private set; }

    /// <summary>Enqueue calls not timed because <see cref="TryReadGpuTime"/> was the timer ring's capacity behind.</summary>
    public long DroppedGpuTimes => _timer.Dropped;

    /// <summary>
    /// Makes <paramref name="config"/> active. Instant and allocation-free when its instance is loaded already
    /// (<see cref="Prepare"/>); otherwise loads it now, with warm-up, which synchronises the context: a drained point only.
    /// </summary>
    public void Configure(in FrameGenConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = Validate(config);
        var instance = Find(key);
        if (instance is { IsFaulted: true })
        {
            // A faulted instance is reloaded from scratch (destroying it synchronises the context, as the load will).
            _instances.Remove(instance);
            if (ReferenceEquals(instance, ActiveEffect))
            {
                Volatile.Write(ref _active, null);
            }

            instance.Dispose();
            instance = null;
        }

        instance ??= Build(key);
        Activate(instance, config);
    }

    /// <summary>
    /// Switches to <paramref name="config"/> only if its instance is loaded already (and not faulted): no load and no CUDA
    /// call, so it is safe during playback, from any thread, also while the generator thread is inside
    /// <see cref="Enqueue"/> (see the class remarks, "Threads"): the instance and the configuration are published as one
    /// object, which a call in progress does not see. Allocation-free when switching to a configuration the instance was
    /// prepared, configured or switched to before (one small object otherwise). False, and nothing changed, otherwise.
    /// </summary>
    public bool TryConfigurePrepared(in FrameGenConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (config.Width <= 0 || config.Height <= 0 || !Capabilities.Formats.Contains(config.Format) || !Enum.IsDefined(config.Quality))
        {
            return false;
        }

        var instance = Find(VfgInstanceKey.Of(config));
        if (instance is null || instance.IsFaulted)
        {
            return false;
        }

        Activate(instance, config);
        return true;
    }

    /// <summary>
    /// Loads (with warm-up) the instance for <paramref name="config"/> without making it active, so a later
    /// <see cref="Configure"/> to it is instant. Synchronises the context: a drained point only. Returns what the load
    /// took (default when it was loaded already).
    /// </summary>
    public VfgLoadReport Prepare(in FrameGenConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = Validate(config);
        var existing = Find(key);
        if (existing is { IsFaulted: false })
        {
            return default;
        }

        var wasActive = Volatile.Read(ref _active);
        if (existing is not null)
        {
            // Faulted: reload it from scratch (and keep it active if it was).
            _instances.Remove(existing);
            if (ReferenceEquals(existing, wasActive?.Effect))
            {
                Volatile.Write(ref _active, null);
            }
            else
            {
                wasActive = null;
            }

            existing.Dispose();
        }
        else
        {
            wasActive = null;
        }

        var instance = Build(key);
        instance.Activation = new VfgActivation(instance, config);
        if (wasActive is not null)
        {
            Activate(instance, wasActive.Config);
        }

        return LastLoad;
    }

    /// <summary>True when the instance for <paramref name="config"/> is loaded (Configure to it is instant).</summary>
    public bool IsPrepared(in FrameGenConfig config) =>
        config.Width > 0 && config.Height > 0 && Find(VfgInstanceKey.Of(config)) is { IsFaulted: false };

    /// <summary>Destroys every loaded instance except the active one. Synchronises the context: a drained point only.</summary>
    public void ReleaseInactive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var active = ActiveEffect;
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_instances[i], active))
            {
                var instance = _instances[i];
                _instances.RemoveAt(i);
                instance.Dispose();
            }
        }
    }

    public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
    {
        if (_disposed)
        {
            ThrowDisposed();
        }

        if (!(t >= 0f && t <= 1f))
        {
            ThrowTimestep(t);
        }

        // One read: the instance that runs and the configuration the frames are checked against belong together, whatever
        // another thread switches to meanwhile (TryConfigurePrepared).
        var active = Volatile.Read(ref _active);
        if (active is null)
        {
            ThrowNotConfigured();
        }

        var effect = active.Effect;
        if (effect.IsFaulted)
        {
            ThrowFaulted(active.Config);
        }

        Validate(prev, nameof(prev), active.Config);
        Validate(curr, nameof(curr), active.Config);
        Validate(output, nameof(output), active.Config);
        DiscontinuityLatch.ValidatePair(prev, curr);
        CudaStream.ValidateCudaSignal(prev.Ready, nameof(prev));
        CudaStream.ValidateCudaSignal(curr.Ready, nameof(curr));
        CudaStream.ValidateCudaSignal(output.Ready, nameof(output));
        CudaStream.ValidateCudaSignal(done, nameof(done));
        var stream = Stream;
        stream.ThrowIfDisposed();
        Context.EnsureCurrent();

        // Everything is validated above, so nothing below is enqueued for a call that then fails on its arguments.
        stream.WaitValidated(prev.Ready);
        if (curr.Ready != prev.Ready)
        {
            stream.WaitValidated(curr.Ready);
        }

        stream.WaitValidated(output.Ready);

        // Core's rule: every call whose pair spans a notified cut is discontinuous (t = 0 still shows prev).
        var discontinuity = _discontinuity.Observe(prev, curr);
        _timer.Begin(stream);
        try
        {
            if (t <= 0f || t >= 1f)
            {
                // VFG takes t strictly inside (0, 1) (it answers NVCV_ERR_PARAMETER for 0 and 1): show the real frame.
                stream.CopyFrameAsync(t <= 0f ? prev : curr, output);
                RealFrameCopies++;
            }
            else
            {
                effect.Bind(VfgEffect.Previous, prev);
                effect.Bind(VfgEffect.Current, curr);
                effect.Bind(VfgEffect.Output, output);
                effect.Run(t, discontinuity);
                Runs++;
                if (discontinuity)
                {
                    ShotChangeRuns++;
                }
            }
        }
        catch (VfxException)
        {
            // The SDK's state for this instance is unknown now: stop using it until it is loaded again.
            _timer.Abort();
            effect.IsFaulted = true;
            throw;
        }
        catch
        {
            _timer.Abort();
            throw;
        }

        _timer.End(stream);
        stream.SignalValidated(done);
    }

    /// <summary>
    /// Source switch, seek or detected cut at the frame with content time <paramref name="contentTimeNs"/> (Core's rule,
    /// <see cref="DiscontinuityLatch"/>): every <see cref="Enqueue"/> whose pair spans it runs with VFG's manual
    /// shot-change flag, which outputs the current frame unchanged (cleared again after each run); t = 0 shows the
    /// previous frame; the first pair that starts at or after it ends the notification. Safe from any thread, at any time
    /// before the pair across the cut is enqueued.
    /// </summary>
    public void NotifyDiscontinuity(long contentTimeNs) => _discontinuity.Notify(contentTimeNs);

    /// <summary>Non-blocking: GPU time of the oldest unread finished <see cref="Enqueue"/>, ns (the d of the latency model).</summary>
    public bool TryReadGpuTime(out long nanoseconds) => _timer.TryRead(out nanoseconds);

    /// <summary>
    /// Destroys every instance (which synchronises the context: a drained point, never a pacing thread), the timers and
    /// the stream it created. Work still queued that uses them must have finished or been abandoned.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _active, null);
        foreach (var instance in _instances)
        {
            instance.Dispose();
        }

        _instances.Clear();
        _timer.Dispose();
        if (_ownsStream)
        {
            Stream.Dispose();
        }
    }

    private VfgEffect? Find(in VfgInstanceKey key)
    {
        foreach (var instance in _instances)
        {
            if (instance.Key == key)
            {
                return instance;
            }
        }

        return null;
    }

    /// <summary>Creates, loads and warms up the instance for <paramref name="key"/>, making room first if needed.</summary>
    private VfgEffect Build(in VfgInstanceKey key)
    {
        while (_instances.Count >= Options.MaxLoadedInstances)
        {
            var oldest = -1;
            var active = ActiveEffect;
            for (var i = 0; i < _instances.Count; i++)
            {
                if (!ReferenceEquals(_instances[i], active) && (oldest < 0 || _instances[i].LastUsed < _instances[oldest].LastUsed))
                {
                    oldest = i;
                }
            }

            if (oldest < 0)
            {
                // Only the active one is loaded and the limit is 1: replace it.
                oldest = 0;
                Volatile.Write(ref _active, null);
            }

            var evicted = _instances[oldest];
            _instances.RemoveAt(oldest);
            evicted.Dispose();
        }

        Context.EnsureCurrent();
        var stream = Stream;

        // Dummy frames for the warm-up: two different flat images and an output, freed again at the end (this is a
        // drained point already: the load synchronises the context).
        using var a = CudaDeviceBuffer.AllocateImage(Context, key.Width, key.Height, key.Format);
        using var b = CudaDeviceBuffer.AllocateImage(Context, key.Width, key.Height, key.Format);
        using var o = CudaDeviceBuffer.AllocateImage(Context, key.Width, key.Height, key.Format);
        var fa = a.AsFrame();
        var fb = b.AsFrame();
        var fo = o.AsFrame();
        stream.FillFrameAsync(fa, key.Format == PixelFormat.Rgb10A2 ? 0xC0_4C_13_04u : 0xFF_30_30_30u);
        stream.FillFrameAsync(fb, key.Format == PixelFormat.Rgb10A2 ? 0xC2_32_0C_83u : 0xFF_C8_C8_C8u);
        stream.Synchronize();

        var freeBefore = Context.MemoryInfo.FreeBytes;
        var started = Stopwatch.GetTimestamp();
        var effect = VfgEffect.Create(stream, key, Options.AutomaticShotChangeDetection);
        long createNs, loadNs, warmupNs;
        try
        {
            effect.Bind(VfgEffect.Previous, fa);
            effect.Bind(VfgEffect.Current, fb);
            effect.Bind(VfgEffect.Output, fo);
            createNs = ElapsedNs(started);
            started = Stopwatch.GetTimestamp();
            effect.Load();
            loadNs = ElapsedNs(started);

            // Varied timesteps and one manual shot change, so every path the frames will take has run once.
            started = Stopwatch.GetTimestamp();
            for (var i = 0; i < Options.WarmupRuns; i++)
            {
                effect.Run(i % 4 == 0 ? 0.5f : 0.125f + (0.25f * (i % 4)), shotChange: i == Options.WarmupRuns - 1);
            }

            stream.Synchronize();
            warmupNs = ElapsedNs(started);
        }
        catch
        {
            stream.Synchronize();
            effect.Dispose();
            throw;
        }

        var freeAfter = Context.MemoryInfo.FreeBytes;
        effect.InvalidateBindings();
        _instances.Add(effect);
        Loads++;
        LastLoad = new VfgLoadReport
        {
            Key = key,
            CreateNs = createNs,
            LoadNs = loadNs,
            WarmupNs = warmupNs,
            WarmupRuns = Options.WarmupRuns,
            DeviceBytes = freeBefore - freeAfter,
            FirstInProcess = Interlocked.Increment(ref _loadsInProcess) == 1,
        };
        return effect;
    }

    private static long ElapsedNs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

    /// <summary>
    /// Makes <paramref name="instance"/> active with <paramref name="config"/>, published as one object. Reuses the
    /// instance's last activation when the configuration is the same (no allocation).
    /// </summary>
    private void Activate(VfgEffect instance, in FrameGenConfig config)
    {
        var activation = instance.Activation;
        if (activation is null || activation.Config != config)
        {
            activation = new VfgActivation(instance, config);
            instance.Activation = activation;
        }

        instance.LastUsed = Interlocked.Increment(ref _useClock);
        Volatile.Write(ref _active, activation);
    }

    private VfgInstanceKey Validate(in FrameGenConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.Width, nameof(config));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.Height, nameof(config));
        if (!Capabilities.Formats.Contains(config.Format))
        {
            throw new ArgumentException($"VFG does not take {config.Format}: RGBA8, BGRA8 or RGB10A2 only.", nameof(config));
        }

        if (!Enum.IsDefined(config.Quality))
        {
            throw new ArgumentException($"Unknown quality {config.Quality}.", nameof(config));
        }

        return VfgInstanceKey.Of(config);
    }

    private static void Validate(in GpuFrame frame, string paramName, in FrameGenConfig config)
    {
        FrameChecks.ValidateCudaImage(frame, paramName);
        if (frame.Width != config.Width || frame.Height != config.Height || frame.Format != config.Format)
        {
            ThrowShape(frame, paramName, config);
        }
    }

    [DoesNotReturn]
    private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(VfgGenerator));

    [DoesNotReturn]
    private static void ThrowTimestep(float t) =>
        throw new ArgumentOutOfRangeException(nameof(t), t, "The timestep must lie in [0, 1]; VFG only interpolates.");

    [DoesNotReturn]
    private static void ThrowNotConfigured() => throw new InvalidOperationException("VfgGenerator.Configure has not been called.");

    [DoesNotReturn]
    private static void ThrowFaulted(in FrameGenConfig config) => throw new InvalidOperationException(
        $"The VFG instance for {config.Width}x{config.Height} {config.Format} failed in the SDK earlier and is faulted; " +
        "Configure loads it again (at a drained point).");

    [DoesNotReturn]
    private static void ThrowShape(in GpuFrame frame, string paramName, in FrameGenConfig config) =>
        throw new ArgumentException(
            $"{frame.Width}x{frame.Height} {frame.Format} does not match the configured {config.Width}x{config.Height} {config.Format}.",
            paramName);
}

/// <summary>An instance of <see cref="VfgGenerator"/> made active with one configuration. Immutable; published whole.</summary>
internal sealed class VfgActivation(VfgEffect effect, FrameGenConfig config)
{
    public VfgEffect Effect { get; } = effect;

    public FrameGenConfig Config { get; } = config;
}
