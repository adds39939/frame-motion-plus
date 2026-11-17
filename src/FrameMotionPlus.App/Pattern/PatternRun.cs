using FrameMotionPlus.App.Bench;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.App.Pattern;

/// <summary>A GPU, driver, display or layer that <c>fmp pattern</c> needs is not there. The message is one line.</summary>
internal sealed class HardwareUnavailableException : Exception
{
    public HardwareUnavailableException()
    {
    }

    public HardwareUnavailableException(string message)
        : base(message)
    {
    }

    public HardwareUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <c>fmp pattern</c>: the Phase 2 pipeline end to end. CUDA draws the test pattern straight into a presentation-ring
/// slot (or, with blend, draws source frames into a small history and writes real frames and BlendGenerator output at
/// t = 0.5 alternately into the ring), the bridge's timelines order everything GPU-side, and the Vulkan presenter shows
/// each slot on a window or an offscreen target. One dedicated thread paces it on CLOCK_MONOTONIC at the refresh rate
/// and does all producing and presenting (the presenter is single-threaded); the main thread owns the window and pumps
/// its events. Offscreen, every frame is read back and verified bit for bit.
/// </summary>
internal sealed class PatternRun : IDisposable
{
    private const int HistorySize = 3;
    private const long StartDelayNs = 100 * Nanoseconds.PerMillisecond;

    private readonly PatternOptions _o;
    private readonly TextWriter? _validationEcho;
    private readonly long _failAt;
    private volatile bool _stop;
    private volatile bool _stoppedByUser;
    private bool _tornDown;

    // Pipeline objects (set up and torn down by Run).
    private CudaContext? _cuda;
    private ImageKernels? _kernels;
    private CudaStream? _ringStream;
    private CudaStream? _sourceStream;
    private BlendGenerator? _blend;
    private VfgGenerator? _vfg;
    private IFrameGenerator? _generator;
    private CudaSpanTimerRing? _patternTimer;
    private CudaSpanTimerRing? _copyTimer;
    private readonly CudaDeviceBuffer?[] _history = new CudaDeviceBuffer?[HistorySize];
    private readonly CudaEvent?[] _drawn = new CudaEvent?[HistorySize];
    private readonly GpuFrame[] _sourceFrames = new GpuFrame[HistorySize];
    private readonly GpuSignal[] _lastReader = new GpuSignal[HistorySize];
    private SdlWindow? _window;
    private VulkanInstance? _instance;
    private VulkanDevice? _device;
    private PresentTarget? _target;
    private VulkanPresenter? _presenter;
    private CudaVulkanBridge? _bridge;
    private PatternVerifier? _verifier;
    private byte[]? _readback;

    // Loop results, written by the present thread, read after it has been joined.
    private PatternSeries _series = null!;
    private long _sourceContentNs; // the last source frame's content time: every frame gets a distinct, later one
    private long _presented;
    private long _dropped;
    private long _skipped;
    private long _late;
    private long _verified;
    private long _mismatches;
    private string? _firstMismatch;
    private long _loopNs;
    private long _measuredFrames;
    private long _allocated = -1;
    private GcSnapshot _gc;
    private ulong _lastReadbackFrame;
    private long _slots;
    private long _loopStart;
    private long _waitedForImage;
    private long _heldBack;
    private long _maxOutstanding;
    private long _maxInFlight;
    private FrameRate _rate;
    private int _rateChanges;
    private long _generatedScored;
    private long _generatedGenuine;
    private long _generatedWrap;
    private double _sumToTruth;
    private double _sumBlendToTruth;
    private InterpolationScore _worstScore;
    private InterpolationScore _firstBadScore;
    private long _firstBadOutput = -1;
    private uint _firstBadFrame;

    /// <param name="options">The run.</param>
    /// <param name="validationEcho">Where each validation-layer problem is written as it arrives (with --validate), so
    /// none is ever counted but lost; null for none. Written from whichever thread made the Vulkan call: pass a
    /// synchronised writer.</param>
    public PatternRun(PatternOptions options, TextWriter? validationEcho = null)
    {
        _o = options;
        _validationEcho = validationEcho;

        // Fault injection for testing teardown (documented in `fmp --help`): FMP_PATTERN_FAIL_AT=<slot> makes the loop
        // throw between acquiring that slot's write and signalling it, as a producer failure would, leaving an
        // abandoned write. Read by this command only; not an option on purpose.
        _failAt = long.TryParse(Environment.GetEnvironmentVariable("FMP_PATTERN_FAIL_AT"), out var at) ? at : -1;
    }

    /// <summary>Asks the present loop to stop after the current frame.</summary>
    public void Stop() => _stop = true;

    /// <summary>
    /// The user asked to stop (window closed, Escape, Ctrl+C, SIGTERM): the loop stops after the current frame and the
    /// run tears down and reports as usual. Safe from any thread, including a signal handler, at any point of the run.
    /// </summary>
    public void StopByUser()
    {
        _stoppedByUser = true;
        _stop = true;
    }

    /// <summary>Sets up, runs and tears down the whole pipeline (once).</summary>
    public PatternResult Run()
    {
        ObjectDisposedException.ThrowIf(_tornDown, this);
        var result = new PatternResult { Options = _o };
        try
        {
            Setup(result);
            RunLoop(result);
        }
        finally
        {
            Teardown(result);
        }

        return result;
    }

    /// <summary>Tears down whatever <see cref="Run"/> did not (it normally tears everything down itself).</summary>
    public void Dispose()
    {
        if (!_tornDown)
        {
            Teardown(new PatternResult { Options = _o });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new HardwareUnavailableException(message);
        }
    }

    private void Setup(PatternResult result)
    {
        Require(OperatingSystem.IsLinux(), "needs Linux: CUDA/Vulkan interop shares memory through opaque file descriptors (Windows is Phase 10)");
        Require(CudaDriver.TryInitialize(out var reason), $"needs an NVIDIA GPU with the CUDA driver: {reason}");
        Require(CudaDriver.DeviceCount > 0, "needs an NVIDIA GPU: the CUDA driver reports no device");
        var cudaDevice = CudaDriver.GetDevice(0);
        result.DeviceName = cudaDevice.Name;
        result.DeviceUuid = GpuIdentity.UuidOf(cudaDevice).ToString();
        result.CudaDriverVersion = CudaDriver.DriverVersion;
        if (_o.Vfg)
        {
            // The documented lookup order: --vfx-sdk, $FMP_VFX_SDK, the settings file's generator.vfxSdkDirectory, ...
            var settingsDirectory = _o.SettingsPath is null ? null : Cli.SettingsCommand.Load(_o.SettingsPath).Generator.VfxSdkDirectory;
            Require(VfxSdk.TryLoad(out var why, _o.SdkDirectory, settingsDirectory), $"needs the NVIDIA VFX SDK with Video Frame Generation for --vfg: {why}");
            result.VfxSdk = $"VFX SDK {VfxSdk.Version} from {VfxSdk.Location}";
        }

        // The layer policy is process-wide and must be in place before anything touches the Vulkan loader, SDL creating
        // a Vulkan window included: the desktop session exports third-party layers that must stay out of our presents.
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        Require(VulkanLoader.TryInitialize(out var loaderError), $"needs the Vulkan loader: {loaderError}");
        if (!_o.Offscreen)
        {
            Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
                "needs a Wayland session for a window (WAYLAND_DISPLAY is not set); use --offscreen");
            Require(SdlWindow.IsLibraryAvailable(), "needs SDL3 (libSDL3.so.0) for a window; use --offscreen");
            try
            {
                _window = SdlWindow.Create(new SdlWindowOptions { Title = "fmp pattern", Width = _o.WindowWidth, Height = _o.WindowHeight });
            }
            catch (InvalidOperationException e)
            {
                throw new HardwareUnavailableException($"cannot open a window: {e.Message}; use --offscreen", e);
            }
        }

        try
        {
            _instance = VulkanInstance.Create(new VulkanInstanceOptions
            {
                ApplicationName = "fmp pattern",
                Layers = VulkanLayerPolicy.Isolated,
                EnableValidation = _o.Validate,
                SynchronizationValidation = _o.Validate,
                OnValidationMessage = _validationEcho is null ? null : EchoValidation,
                RequiredExtensions = _window?.RequiredInstanceExtensions ?? [],
            });
            _device = VulkanDevice.Create(_instance, GpuIdentity.VulkanSelectorFor(cudaDevice));
        }
        catch (VulkanDeviceNotFoundException e)
        {
            throw new HardwareUnavailableException($"needs a Vulkan device on the CUDA GPU ({result.DeviceUuid}): {FirstLine(e.Message)}", e);
        }
        catch (VulkanException e)
        {
            throw new HardwareUnavailableException($"needs a working Vulkan driver: {FirstLine(e.Message)}", e);
        }

        result.VulkanDriver = _device.PhysicalDevice.DriverVersionText;
        result.ValidationEnabled = _o.Validate;

        // Offscreen there is no display to follow: the option or 120 Hz. A window's rate is read per slot (CurrentRate).
        var offscreenRate = _o.Rate ?? FrameRate.FromHz(120);
        if (_window is not null)
        {
            _target = new SwapchainTarget(_device, _window);
        }
        else
        {
            _target = new OffscreenTarget(_device, new OffscreenTargetOptions
            {
                Width = _o.Width,
                Height = _o.Height,
                Readback = _o.Verify,
                RefreshRate = offscreenRate,
            });
        }

        _presenter = new VulkanPresenter(_device, _target, new VulkanPresenterOptions { SlotCount = _o.Slots, MeasureGpuTime = true });
        _cuda = CudaContext.RetainPrimary(cudaDevice);
        // A VFG write enqueues one run (measured: about 28, 33 and 170 stream operations in Low, Medium and High) plus
        // three waits, two timer records and the signal; declare 192 so the bridge's limit (4 writes, 768 operations)
        // keeps the producer's stream from filling.
        _bridge = new CudaVulkanBridge(_cuda, _presenter, _o.Vfg ? new CudaVulkanBridgeOptions { OperationsPerWrite = 192, MaxLead = 4 } : null);
        _bridge.Configure(new BridgeConfig { SlotCount = _o.Slots, Width = _o.Width, Height = _o.Height, Format = _o.Format });
        var ring = _bridge.Ring!;
        result.PitchBytes = ring.PitchBytes;
        result.SlotBytes = ring.AllocationSizeBytes;
        result.TargetDescription = _target is SwapchainTarget swapchain
            ? $"window {_window!.PixelWidth}x{_window.PixelHeight} px ({_window.VideoDriver}), swapchain {swapchain.SwapchainFormatName} / {swapchain.SwapchainColorSpaceName}"
            : $"offscreen {_target.Width}x{_target.Height} {((OffscreenTarget)_target).PixelFormat} / {_target.ColorSpace}{(_o.Verify ? ", read back and verified" : ", no readback")}";
        result.Transform = _presenter.ActiveTransform.ToString();
        result.MaxFramesInFlight = _presenter.Capabilities.MaxFramesInFlight;
        result.MaxOutstanding = _bridge.MaxOutstanding;
        if (_target is SwapchainTarget feedbackTarget)
        {
            result.FeedbackMode = feedbackTarget.ActiveFeedback.ToString();
            result.FeedbackFallbackReason = feedbackTarget.FeedbackFallbackReason;
        }
        else
        {
            result.FeedbackMode = "offscreen (a present completes with its GPU work)";
        }

        _kernels = ImageKernels.Load(_cuda);
        _ringStream = CudaStream.Create(_cuda, CudaStreamPriority.High);
        _patternTimer = CudaSpanTimerRing.Create(_cuda, 32);
        if (_o.Generates)
        {
            // Ring writes (real copies and generated frames) all go on one stream, so 'written' is signalled in order.
            _sourceStream = CudaStream.Create(_cuda);
            _copyTimer = CudaSpanTimerRing.Create(_cuda, 32);
            var config = new FrameGenConfig { Width = _o.Width, Height = _o.Height, Format = _o.Format, Quality = _o.Quality };
            if (_o.Vfg)
            {
                // VFG on the ring stream itself (the caller's stream option), loaded and warmed up here, before the loop.
                _vfg = new VfgGenerator(_cuda, new VfgOptions { Stream = _ringStream });
                _vfg.Configure(config);
                result.VfgLoad = _vfg.LastLoad;
                _generator = _vfg;
            }
            else
            {
                _blend = new BlendGenerator(_cuda, _kernels, _ringStream);
                ((IFrameGenerator)_blend).Configure(config);
                _generator = _blend;
            }

            for (var i = 0; i < HistorySize; i++)
            {
                _history[i] = CudaDeviceBuffer.AllocateImage(_cuda, _o.Width, _o.Height, _o.Format);
                _drawn[i] = CudaEvent.Create(_cuda);
            }

            result.GeneratorMayBlock = _generator.Capabilities.EnqueueMayBlock;
        }

        if (_target is OffscreenTarget offscreen && _o.Verify)
        {
            _verifier = new PatternVerifier(_o.Width, _o.Height, _o.Format);
            _readback = new byte[offscreen.ReadbackSizeBytes];
        }
    }

    private void RunLoop(PatternResult result)
    {
        // A frame count, or a duration whose frame count depends on a refresh rate that may only become known (or
        // change) once the window is on an output: the series are sized for a 500 Hz display and the loop is bounded
        // by time.
        var maxFrames = _o.Frames ?? long.MaxValue;
        var durationNs = _o.Frames is null ? (long)(_o.DurationSeconds * Nanoseconds.PerSecond) : 0;
        _series = new PatternSeries(_o.Frames ?? ((long)(_o.DurationSeconds * 500) + 64));
        var warmup = (int)Math.Min(_o.WarmupFrames, _o.Frames is { } f ? f / 2 : (long)(_o.DurationSeconds * 30));
        result.WarmupFrames = warmup;

        var thread = DedicatedThread.Start("fmp-present", ThreadSchedulingRequest.Default, () => Loop(maxFrames, durationNs, warmup));
        result.PresentThread = thread.Report.Summary;
        while (thread.IsAlive)
        {
            if (_window is not null)
            {
                var events = _window.PumpEvents();
                if ((events & (WindowEvents.CloseRequested | WindowEvents.EscapePressed)) != 0)
                {
                    StopByUser();
                }
            }

            _ = thread.Join(TimeSpan.FromMilliseconds(4));
        }

        thread.Join();
        result.StoppedByUser = _stoppedByUser;
        result.Failure = thread.Failure;
        if (thread.Failure is not null && _loopStart != 0 && _loopNs == 0)
        {
            // The loop threw: report the slots it got through and the time it ran, not zeros.
            _loopNs = Math.Max(0, Monotonic.NowNs - _loopStart);
        }

        result.Frames = _slots;
        result.Rate = _rate;
        result.RateChanges = _rateChanges;
        result.RateKnown = _o.Rate is not null || _target!.RefreshRateKnown;
        result.Presented = _presented;
        result.Dropped = _dropped;
        result.Skipped = _skipped;
        result.ImageWaits = _waitedForImage;
        result.HeldBack = _heldBack;
        result.MaxOutstandingSeen = _maxOutstanding;
        result.MaxPresentsInFlightSeen = _maxInFlight;
        result.Late = _late;
        result.Verified = _verified;
        result.Mismatches = _mismatches;
        result.FirstMismatch = _firstMismatch;
        result.LoopNs = _loopNs;
        result.MeasuredFrames = _measuredFrames;
        result.AllocatedBytes = _allocated;
        result.Gc = _gc;
        result.Series = _series;
        result.PresenterStats = _presenter!.Stats;
        result.OutOfOrderSignals = _bridge!.OutOfOrderSignals;
        result.TimerDrops = (_patternTimer?.Dropped ?? 0) + (_copyTimer?.Dropped ?? 0) + (_blend?.DroppedGpuTimes ?? 0) + (_vfg?.DroppedGpuTimes ?? 0);
        result.GeneratedScored = _generatedScored;
        result.GeneratedGenuine = _generatedGenuine;
        result.GeneratedWrap = _generatedWrap;
        result.MeanToTruth = _generatedScored == 0 ? 0 : _sumToTruth / _generatedScored;
        result.MeanBlendToTruth = _generatedScored == 0 ? 0 : _sumBlendToTruth / _generatedScored;
        result.WorstScore = _worstScore;
        result.FirstBadGenerated = _firstBadOutput < 0 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"output {_firstBadOutput} (VFG between frames {_firstBadFrame} and {_firstBadFrame + 2}): box band error {_firstBadScore.ToTruth:0.000} " +
            $"vs the blend's {_firstBadScore.BlendToTruth:0.000}, to the neighbours {_firstBadScore.ToPrev:0.000}/{_firstBadScore.ToCurr:0.000}, " +
            $"worst static bar row {_firstBadScore.StaticBars:0.000}");
    }

    /// <summary>The present thread: paced on CLOCK_MONOTONIC, produce → signal → present per refresh slot.</summary>
    private void Loop(long maxFrames, long durationNs, int warmup)
    {
        var presenter = _presenter!;
        var bridge = _bridge!;
        var waiter = DeadlineWaiter.Sleep;
        PreRoll(presenter, bridge);
        var start = Monotonic.NowNs + (_o.Unpaced ? 0 : StartDelayNs);
        var end = durationNs > 0 ? start + durationNs : long.MaxValue;
        var allocatedAtWarmup = 0L;
        var gcAtWarmup = default(GcSnapshot);
        long produced = 0; // content follows frames produced, not slots: a skipped slot must not break the sequence

        // Slot deadlines on CLOCK_MONOTONIC at the current refresh rate, re-anchored at the last deadline whenever the
        // rate changes (a window's display rate is only known once its surface is on an output, and can change).
        var rate = CurrentRate();
        var anchor = start;
        long index = 0;
        var deadline = start;
        long k;
        _loopStart = start; // for the summary of a loop that fails (RunLoop)
        _rate = rate;
        for (k = 0; k < maxFrames && !_stop; k++)
        {
            _slots = k; // kept current (a field write) so a loop that throws is reported as far as it got
            var now = CurrentRate();
            if (now != rate)
            {
                anchor = deadline;
                index = 1;
                rate = now;
                _rate = now;
                _rateChanges++;
            }

            // Unpaced: no slot grid, every iteration at once; the run ends by time or frame count alone.
            deadline = _o.Unpaced ? Monotonic.NowNs : k == 0 ? start : anchor + rate.TickTimeNs(index);
            index++;
            if (deadline >= end)
            {
                break;
            }

            if (k == warmup)
            {
                gcAtWarmup = GcSnapshot.Take();
                allocatedAtWarmup = GC.GetAllocatedBytesForCurrentThread();
            }

            if (!_o.Unpaced)
            {
                // Unpaced, there is no wait and no readiness gate: the bridge's back-pressure and the presenter's rules alone.
                var woke = waiter.WaitUntil(deadline);
                if (woke - deadline > rate.PeriodNs)
                {
                    _late++;
                }

                if (!WaitUntilReady(presenter, deadline + (long)(rate.PeriodNs / 2)))
                {
                    // Still no image free half a period later (FIFO backed up, minimised window): skip the slot rather
                    // than write a frame only to drop it.
                    _skipped++;
                    continue;
                }
            }

            var t0 = Monotonic.NowNs;

            // Back-pressure: the bridge refuses a write while too many it handed out are unfinished; skip producing.
            if (!bridge.TryAcquireSlot(out var slot))
            {
                _heldBack++;
                continue;
            }

            if (k == _failAt)
            {
                ThrowInjectedFailure(k);
            }

            var outstanding = bridge.Outstanding; // the lead including this write
            _maxOutstanding = outstanding > _maxOutstanding ? outstanding : _maxOutstanding;
            var output = produced++;
            Produce(slot, output);
            var shown = presenter.TryPresent(bridge.CreatePresentRequest(slot, k, deadline));
            if (shown)
            {
                _presented++;
                if (k >= warmup)
                {
                    // Presents in flight right after this one (the presenter's own maximum includes the pre-roll's).
                    var after = presenter.Stats;
                    var inFlight = after.Presented - after.Completed;
                    _maxInFlight = inFlight > _maxInFlight ? inFlight : _maxInFlight;
                }
            }
            else
            {
                _dropped++;
            }

            while (presenter.TryGetFeedback(out _))
            {
            }

            var t1 = Monotonic.NowNs;
            if (k >= warmup)
            {
                _series.AddCpu(t1 - t0);
                var stats = presenter.Stats;
                _series.AddPresent(stats.LastCopyGpuNs, stats.LastPassGpuNs);
            }

            ReadGpuTimes(k >= warmup);
            if (shown && _verifier is not null)
            {
                Verify(output);
            }
        }

        _loopNs = Monotonic.NowNs - start;
        _slots = k;
        _rate = rate;

        if (k > warmup)
        {
            _allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedAtWarmup;
            _gc = GcSnapshot.Take().Since(gcAtWarmup);
            _measuredFrames = k - warmup;
        }

        presenter.WaitIdle(2 * Nanoseconds.PerSecond);
        ReadGpuTimes(true);
    }

    /// <summary>
    /// Demo pacing only (Phase 6's scheduler replaces it): a present completes at a vblank, which the slot grid on
    /// CLOCK_MONOTONIC is not locked to, so waking just before one the oldest present may not be complete yet. Polls
    /// (allocation-free) until a frame slot is free or <paramref name="giveUpNs"/>, so the demo skips a slot instead of
    /// producing a frame only to drop it. Nothing in the bridge or presenter depends on it: <c>--unpaced</c> runs
    /// without it and the Gpu tests' stall loops never call it.
    /// </summary>
    private bool WaitUntilReady(VulkanPresenter presenter, long giveUpNs)
    {
        while (!presenter.IsReadyForPresent())
        {
            if (!WaitForImage(giveUpNs))
            {
                return false;
            }

            _waitedForImage++;
        }

        return true;
    }

    /// <summary>One poll of <see cref="WaitUntilReady"/>: false once <paramref name="giveUpNs"/> has passed or a stop was asked, else waits up to 200 µs.</summary>
    private bool WaitForImage(long giveUpNs)
    {
        var now = Monotonic.NowNs;
        if (now >= giveUpNs || _stop)
        {
            return false;
        }

        DeadlineWaiter.Sleep.WaitUntil(Math.Min(giveUpNs, now + 200_000));
        return true;
    }

    /// <summary>
    /// Runs every path of the per-frame loop once before anything is timed or counted, so no first use can land in the
    /// measured window: producing through <see cref="Produce"/> (pattern or blend, with the GPU timers), the poll for a
    /// free image (both ways out of it), the bridge refusing at its limit (the held-back path, which reads the ring's
    /// counter), a present that catches several releases up, a present the presenter drops for want of a free frame
    /// (which releases the write before it), the feedback and timer reads, and the read-back and verification. Under the
    /// JIT (the test host) a first use allocates once per process: the runtime binds a P/Invoke on its first call and
    /// hands its resolver the library name, a 32-byte "libc" string, which landed in the measured window whenever the
    /// first wait for a free image came after the warm-up (without the pre-roll, a fresh process measured from slot 0
    /// allocates 280 B in its first slots, 608 B with blend). The NativeAOT binary binds nothing at run time; it runs the
    /// same pre-roll. The pre-roll's frames are never verified or counted (the loop starts again at output 0), though
    /// the presenter's own statistics include them; it takes a few milliseconds (about 16 ms measured) and is bounded
    /// (2 s). The drop is forced with a write that takes 3 ms on the GPU, so it happens wherever the presenter caps
    /// presents in flight.
    /// </summary>
    private void PreRoll(VulkanPresenter presenter, CudaVulkanBridge bridge)
    {
        var giveUp = Monotonic.NowNs + (2 * Nanoseconds.PerSecond);
        var slots = bridge.SlotCount;
        var limit = bridge.MaxOutstanding;
        long index = -1_000_000; // request slot indices below the loop's 0, 1, 2, ...
        long output = 0;

        // The poll for a free image, and the pacing wait, which sleep in clock_nanosleep: each twice with 2 ms to go, so
        // the second call reaches the sleep even if compiling the first took longer than its deadline (a first call that
        // finds its deadline passed returns without sleeping, and the sleep would then be first used in the loop).
        for (var i = 0; i < 2; i++)
        {
            _ = DeadlineWaiter.Sleep.WaitUntil(Monotonic.NowNs + (2 * Nanoseconds.PerMillisecond));
            _ = WaitForImage(Monotonic.NowNs + (2 * Nanoseconds.PerMillisecond));
        }

        _ = WaitForImage(0);
        _ = WaitUntilReady(presenter, Monotonic.NowNs);

        // Writes the presenter is not given, until the bridge refuses (the first lap finishes; the rest waits for
        // releases, so the lead reaches the limit).
        BridgeSlot newest = default;
        for (var i = 0; i < slots + limit + 1; i++)
        {
            if (i == slots)
            {
                SpinUntil(() => bridge.Outstanding == 0, giveUp);
            }

            if (!bridge.TryAcquireSlot(out var slot))
            {
                break;
            }

            if (_generator is { Capabilities.EnqueueMayBlock: true } && i >= slots && (output & 1) == 1)
            {
                // A generator whose Enqueue may block (VFG with automatic shot-change detection) would wait here for a
                // release that only a later present on this very thread gives (IFrameGenerator.Enqueue, "Blocking"):
                // write the next real frame instead (drawing its source, as the skipped generated frame would have).
                DrawSource((output + 1) / 2);
                output++;
            }

            Produce(slot, output++);
            newest = slot;
        }

        // Presenting the newest catches the releases up; then everything finishes, and the frame is read back.
        if (newest.Write != 0 && presenter.TryPresent(bridge.CreatePresentRequest(newest, index++, Monotonic.NowNs)))
        {
            SpinUntil(() => bridge.Outstanding == 0, giveUp);
            ReadBackAndCheck(output - 1);
        }

        SpinUntil(() => bridge.Outstanding == 0, giveUp);

        // A drop: a write that takes 3 ms on the GPU is presented, and the next ones are handed over at once, until the
        // presenter refuses one because no frame is free.
        for (var i = 0; i < 2 + presenter.Capabilities.MaxFramesInFlight; i++)
        {
            if (!bridge.TryAcquireSlot(out var slot))
            {
                break;
            }

            if (i == 0)
            {
                _kernels!.EnqueueDelay(_ringStream!, 3 * Nanoseconds.PerMillisecond);
            }

            Produce(slot, output++);
            if (!presenter.TryPresent(bridge.CreatePresentRequest(slot, index++, Monotonic.NowNs)))
            {
                break;
            }
        }

        SpinUntil(() => bridge.Outstanding == 0, giveUp);
        _ = presenter.WaitIdle(Math.Max(1, giveUp - Monotonic.NowNs));
        while (presenter.TryGetFeedback(out _))
        {
        }

        _ = presenter.Stats;
        ReadGpuTimes(false);

        // The loop's first read-back must be newer than the pre-roll's last presented frame to count as fresh.
        if (_verifier is not null)
        {
            _lastReadbackFrame = ((OffscreenTarget)_target!).ReadLatest(_readback!);
        }
    }

    /// <summary>The read-back and the verifier's checks, results ignored (pre-roll only).</summary>
    private void ReadBackAndCheck(long k)
    {
        if (_verifier is null)
        {
            return;
        }

        _ = ((OffscreenTarget)_target!).ReadLatest(_readback!);
        var j = (uint)(k / 2);
        _ = _verifier.IsFrame(_readback!, j);
        _ = _verifier.IsBlend(_readback!, j);
        _ = _verifier.ScoreGenerated(_readback!, 2 * j);
        _ = _verifier.DecodeCounter(_readback!);
    }

    /// <summary>Writes a validation-layer problem to the echo as it arrives (any thread).</summary>
    private void EchoValidation(ValidationMessage message)
    {
        if (message.IsProblem)
        {
            _validationEcho!.WriteLine($"fmp: validation: {message}");
        }
    }

    private static void SpinUntil(Func<bool> condition, long giveUpNs)
    {
        while (!condition() && Monotonic.NowNs < giveUpNs)
        {
            Thread.Sleep(0);
        }
    }

    private static void ThrowInjectedFailure(long slot) =>
        throw new InvalidOperationException($"injected producer failure at slot {slot} (FMP_PATTERN_FAIL_AT), between acquiring the write and signalling it");

    /// <summary>
    /// The refresh rate to pace at: the option, else the target's. A window's is a 60 Hz guess until its surface is on
    /// an output (after the first presents) and the display's own rate from then on; the loop re-anchors when it changes.
    /// </summary>
    private FrameRate CurrentRate()
    {
        if (_o.Rate is { } fixedRate)
        {
            return fixedRate;
        }

        var display = _presenter!.Timing.RefreshRate;
        return display.IsValid ? display : FrameRate.FromHz(60);
    }

    /// <summary>Enqueues output frame <paramref name="k"/> into the acquired ring <paramref name="slot"/>. Allocation-free.</summary>
    private void Produce(in BridgeSlot slot, long k)
    {
        var kernels = _kernels!;
        var ring = _ringStream!;
        if (!_o.Generates)
        {
            ring.Wait(slot.ProducerWait);
            _patternTimer!.Begin(ring);
            kernels.EnqueueTestPattern(ring, slot.ProducerView, (uint)k);
            _patternTimer.End(ring);
            ring.Signal(slot.ProducerSignal);
            return;
        }

        // 2x: output 2j is real frame j, output 2j + 1 is blend(j, j + 1) at t = 0.5, so frame j + 1 is drawn first.
        var j = k / 2;
        if (k == 0)
        {
            DrawSource(0);
        }

        if ((k & 1) == 0)
        {
            var h = (int)(j % HistorySize);
            ring.Wait(slot.ProducerWait);
            ring.Wait(_sourceFrames[h].Ready);
            _copyTimer!.Begin(ring);
            ring.CopyFrameAsync(_sourceFrames[h], slot.ProducerView);
            _copyTimer.End(ring);
            ring.Signal(slot.ProducerSignal);
            _lastReader[h] = slot.ProducerSignal;
            return;
        }

        DrawSource(j + 1);
        var a = (int)(j % HistorySize);
        var b = (int)((j + 1) % HistorySize);
        // The demo calls the generator on its present thread even when its Enqueue may block (VFG): the slot it writes
        // was released long ago at a lead of 1, so the call cannot wait for this thread, but it does hold the present
        // thread for d. The pipeline gives such a generator a thread of its own (IFrameGenerator.Enqueue, "Blocking").
        _generator!.Enqueue(_sourceFrames[a], _sourceFrames[b], 0.5f, slot.ProducerView, slot.ProducerSignal);
        _lastReader[a] = slot.ProducerSignal;
        _lastReader[b] = slot.ProducerSignal;
    }

    /// <summary>
    /// Source frame <paramref name="j"/> into its history buffer, once the buffer's last reader is done. Its content time
    /// is when it was drawn (CLOCK_MONOTONIC), kept strictly increasing: generators match discontinuities by content time
    /// and refuse a pair whose times do not increase.
    /// </summary>
    private void DrawSource(long j)
    {
        var h = (int)(j % HistorySize);
        var source = _sourceStream!;
        source.Wait(_lastReader[h]);
        _sourceContentNs = Math.Max(Monotonic.NowNs, _sourceContentNs + 1);
        var frame = _history[h]!.AsFrame(contentTimeNs: _sourceContentNs, ready: _drawn[h]!.Signal);
        _patternTimer!.Begin(source);

        // With VFG source frame j is pattern frame 2j, so the true middle of each pair (2j + 1) is known.
        _kernels!.EnqueueTestPattern(source, frame, (uint)(_vfg is null ? j : 2 * j));
        _patternTimer.End(source);
        _drawn[h]!.Record(source);
        _sourceFrames[h] = frame;
    }

    private void ReadGpuTimes(bool keep)
    {
        while (_patternTimer!.TryRead(out var ns))
        {
            if (keep)
            {
                _series.AddPattern(ns);
            }
        }

        while (_copyTimer is not null && _copyTimer.TryRead(out var ns))
        {
            if (keep)
            {
                _series.AddCopy(ns);
            }
        }

        while (_blend is not null && _blend.TryReadGpuTime(out var ns))
        {
            if (keep)
            {
                _series.AddBlend(ns);
            }
        }

        while (_vfg is not null && _vfg.TryReadGpuTime(out var ns))
        {
            if (keep)
            {
                _series.AddBlend(ns);
            }
        }
    }

    /// <summary>Reads output frame <paramref name="k"/> back (waits for it) and checks it bit for bit.</summary>
    private void Verify(long k)
    {
        var target = (OffscreenTarget)_target!;
        var pixels = _readback!;
        var frame = target.ReadLatest(pixels);
        var fresh = frame > _lastReadbackFrame;
        _lastReadbackFrame = frame;
        var j = !_o.Generates ? (uint)k : (uint)(k / 2);
        if (_vfg is not null)
        {
            // Real frame j is pattern frame 2j, checked bit-exact; a generated one is scored against frame 2j + 1.
            if (fresh && (k & 1) == 1)
            {
                ScoreGenerated(pixels, 2 * j, k);
                _verified++;
                return;
            }

            j *= 2;
        }

        var ok = fresh && (!_o.Generates || (k & 1) == 0 ? _verifier!.IsFrame(pixels, j) : _verifier!.IsBlend(pixels, j));
        if (ok)
        {
            _verified++;
            return;
        }

        if (_mismatches++ == 0)
        {
            var kind = _o.Generates && (k & 1) == 1 ? $"blend of frames {j} and {j + 1}" : $"frame {j}";
            _firstMismatch = fresh
                ? $"output {k} ({kind}) differs from the reference; its counter reads {_verifier!.DecodeCounter(pixels)}"
                : $"output {k} ({kind}) was not read back (presenter frame {frame} again)";
        }
    }

    /// <summary>Scores generated output <paramref name="k"/> (the middle of pattern frames <paramref name="frame"/> and + 2). Allocation-free.</summary>
    private void ScoreGenerated(byte[] pixels, uint frame, long k)
    {
        var score = _verifier!.ScoreGenerated(pixels, frame);
        if (score.Wrap)
        {
            _generatedWrap++; // the box wraps between the neighbours: there is no middle to compare with
            return;
        }

        _generatedScored++;
        _sumToTruth += score.ToTruth;
        _sumBlendToTruth += score.BlendToTruth;
        if (score.ToTruth >= _worstScore.ToTruth)
        {
            _worstScore = score;
        }

        if (score.IsGenuine)
        {
            _generatedGenuine++;
        }
        else if (_firstBadOutput < 0)
        {
            // Kept as values and described after the loop: the present thread must not allocate, even for a failure.
            _firstBadOutput = k;
            _firstBadFrame = frame;
            _firstBadScore = score;
        }
    }

    private void Teardown(PatternResult result)
    {
        _tornDown = true;
        // The pipeline's order: CUDA imports first (they refer to the presenter's ring), CUDA work, then the presenter
        // (which releases what it was handed), its target, the window, the device, and last the instance, whose
        // destruction is when the validation layer reports leaked objects.
        var errors = new List<Exception>();
        void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        }

        Try(() => _bridge?.Dispose());

        // Bounded: freeing buffers while queued work has not finished would block inside CUDA (a free synchronises the
        // context), so first make sure it has. An import the bridge abandoned parks nothing on the GPU, so normally the
        // natural order holds: bridge, CUDA buffers, presenter. Only if the producer's own queued writes are still
        // waiting (for releases the presenter will never give, e.g. after it failed) does the presenter go first: its
        // letting go of the ring is what lets the reaper release those writes from CUDA.
        var idle = WaitIdle(_ringStream) & WaitIdle(_sourceStream);
        if (!idle && _bridge is { AbandonedImports: > 0 })
        {
            Try(() => _presenter?.Dispose());
            idle = WaitIdle(_ringStream) & WaitIdle(_sourceStream);
        }

        result.AbandonedImports = _bridge?.AbandonedImports ?? 0;
        if (!idle)
        {
            errors.Add(new TimeoutException("CUDA work queued by the loop did not finish within 3 s; its buffers were not freed"));
            _blend = null;
            _vfg = null;
            _generator = null;
            _patternTimer = null;
            _copyTimer = null;
            Array.Clear(_history);
            Array.Clear(_drawn);
            _sourceStream = null;
            _ringStream = null;
            _kernels = null;
        }

        Try(() => _blend?.Dispose());
        Try(() => _vfg?.Dispose());
        Try(() => _patternTimer?.Dispose());
        Try(() => _copyTimer?.Dispose());
        for (var i = 0; i < HistorySize; i++)
        {
            var h = i;
            Try(() => _history[h]?.Dispose());
            Try(() => _drawn[h]?.Dispose());
        }

        Try(() => _sourceStream?.Dispose());
        Try(() => _ringStream?.Dispose());
        Try(() => _kernels?.Dispose());
        Try(() => _presenter?.Dispose());

        // An abandoned import is freed by the reaper once its work is done, which for a write that was never signalled
        // is once the presenter, disposing, has forced its waits: so this is the place to wait for it (bounded).
        result.UnreapedImports = CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(3)) ? 0 : CudaVulkanBridge.PendingReaps;
        Try(() => _target?.Dispose());
        Try(() => _window?.Dispose());
        Try(() => _device?.Dispose());
        if (_instance is not null)
        {
            Try(_instance.Dispose);
            var log = _instance.Validation;
            result.ValidationProblems = log.ProblemCount;
            result.ValidationReport = log.ProblemCount > 0 ? log.Describe() : null;
        }

        Try(() => _cuda?.Dispose());
        result.TeardownErrors = errors;
    }

    /// <summary>Polls (bounded, 3 s) until <paramref name="stream"/> has no work queued; true when idle or absent.</summary>
    private static bool WaitIdle(CudaStream? stream)
    {
        var deadline = Monotonic.NowNs + (3 * Nanoseconds.PerSecond);
        while (stream is not null && !stream.IsIdle)
        {
            if (Monotonic.NowNs > deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text : text[..newline];
    }
}

/// <summary>Per-frame samples of one run, preallocated so the present thread records them without allocating.</summary>
internal sealed class PatternSeries
{
    private readonly long[] _cpu;
    private readonly long[] _pattern;
    private readonly long[] _copy;
    private readonly long[] _blend;
    private readonly long[] _presentCopy;
    private readonly long[] _presentPass;
    private int _cpuCount;
    private int _patternCount;
    private int _copyCount;
    private int _blendCount;
    private int _presentCount;

    public PatternSeries(long frames)
    {
        var n = (int)Math.Min(frames + 8, int.MaxValue);
        _cpu = new long[n];
        _pattern = new long[n];
        _copy = new long[n];
        _blend = new long[n];
        _presentCopy = new long[n];
        _presentPass = new long[n];
    }

    public void AddCpu(long ns) => Add(_cpu, ref _cpuCount, ns);

    public void AddPattern(long ns) => Add(_pattern, ref _patternCount, ns);

    public void AddCopy(long ns) => Add(_copy, ref _copyCount, ns);

    public void AddBlend(long ns) => Add(_blend, ref _blendCount, ns);

    public void AddPresent(long copyNs, long passNs)
    {
        if (_presentCount < _presentCopy.Length && copyNs > 0 && passNs > 0)
        {
            _presentCopy[_presentCount] = copyNs;
            _presentPass[_presentCount++] = passNs;
        }
    }

    public Distribution Cpu => Distribution.Of(_cpu.AsSpan(0, _cpuCount));

    public Distribution Pattern => Distribution.Of(_pattern.AsSpan(0, _patternCount));

    public Distribution Copy => Distribution.Of(_copy.AsSpan(0, _copyCount));

    public Distribution Blend => Distribution.Of(_blend.AsSpan(0, _blendCount));

    public Distribution PresentCopy => Distribution.Of(_presentCopy.AsSpan(0, _presentCount));

    public Distribution PresentPass => Distribution.Of(_presentPass.AsSpan(0, _presentCount));

    private static void Add(long[] series, ref int count, long ns)
    {
        if (count < series.Length)
        {
            series[count++] = ns;
        }
    }
}
