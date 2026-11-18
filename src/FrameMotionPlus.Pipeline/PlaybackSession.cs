using System.Globalization;
using System.Runtime;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Settings;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Sources.V4l2;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Playback with frame generation, of a file or a live capture: source → scheduler → generator → presentation ring →
/// presenter, on dedicated threads. See docs/benchmarks/phase4b-pipeline.md for the threading and time model and
/// docs/benchmarks/phase5-capture.md for the live source.
/// <list type="bullet">
/// <item><b>Decode thread</b> (the file source's own): FFmpeg/NVDEC and the conversion into the frame history, ahead of
/// time; or the <b>capture thread</b> (the capture source's own): dequeue, upload, cadence and conversion as frames come.</item>
/// <item><b>Pacing thread</b> (<see cref="PacingLoop"/>): the scheduler and the source's consumer; hands each slot to the
/// two threads below; never blocks on the GPU, the generator or the presenter.</item>
/// <item><b>Generator thread</b> (<see cref="RingWriter"/>): every ring write in order on one stream; VFG blocks here.</item>
/// <item><b>Present thread</b> (<see cref="PresentLoop"/>): every presenter call; one present per refresh; feedback into the
/// refresh grid.</item>
/// <item><b>Control thread</b> (the caller of <see cref="Run"/>): the window's events, telemetry, the stop conditions.</item>
/// </list>
/// <see cref="Open"/> builds everything (a drained point: allocations, CUDA module and VFG loads, the decoder);
/// <see cref="Run"/> pre-rolls, plays and tears down. Nothing is created, loaded or freed in the CUDA context between the
/// two. Single use.
/// </summary>
public sealed class PlaybackSession : IDisposable
{
    private const long StartDelayNs = 100 * Nanoseconds.PerMillisecond;
    private const int RingWriterQueue = 32;
    private const int PresentQueue = 16;

    /// <summary>Frames a capture may have waiting for the pacing thread (it drains them at least every 2 ms).</summary>
    private const int CaptureQueueDepth = 2;

    private readonly PlaybackOptions _o;
    private readonly CudaContext _cuda;
    private readonly PlaybackHooks _hooks;
    private readonly TextWriter? _validationEcho;
    private volatile int _stopReason;
    private bool _opened;
    private bool _ran;
    private bool _tornDown;
    private bool _writerStuck;

    private SdlWindow? _window;
    private VulkanInstance? _instance;
    private VulkanDevice? _device;
    private PresentTarget? _target;
    private VulkanPresenter? _presenter;
    private CudaVulkanBridge? _bridge;
    private ImageKernels? _kernels;
    private CudaStream? _copyStream;
    private CudaStream? _computeStream;
    private CudaStream? _generatorStream;
    private IPlaybackSource? _source;
    private GeneratorHandle? _generator;
    private RingWriter? _writer;
    private PresentLoop? _presentLoop;
    private PacingLoop? _pacing;
    private PlaybackSetup? _setup;
    private long _initialGenerationNs;
    private long _presentLeadNs;
    private int _startupFrames;

    /// <summary>The default present lead of a window, in refreshes (see <see cref="CreateScheduler"/>).</summary>
    public const double DefaultPresentLeadRefreshes = 1.5;
    private readonly TextWriter? _watchdog = Environment.GetEnvironmentVariable("FMP_PLAY_WATCHDOG") == "1" ? Console.Error : null;
    private long _watchdogAt;

    public PlaybackSession(PlaybackOptions options, CudaContext cuda, PlaybackHooks? hooks = null, TextWriter? validationEcho = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cuda);
        options.Check();
        _o = options;
        _cuda = cuda;
        _hooks = hooks ?? new PlaybackHooks();
        _validationEcho = validationEcho;
    }

    /// <summary>What <see cref="Open"/> resolved (null before).</summary>
    public PlaybackSetup? Setup => _setup;

    /// <summary>The offscreen target (offscreen sessions), for tests that read frames back.</summary>
    public OffscreenTarget? OffscreenTarget => _target as OffscreenTarget;

    /// <summary>The file source once open (tests: its statistics; seeks); null for a capture.</summary>
    public FilePlaybackSource? Source => _source as FilePlaybackSource;

    /// <summary>The source once open, file or capture.</summary>
    public IPlaybackSource? PlaybackSource => _source;

    /// <summary>The scheduler once playback has started (diagnostics; read it on the pacing thread or after the run).</summary>
    public FrameScheduler? Scheduler => _pacing?.Scheduler;

    /// <summary>The pacing loop's time base: virtual or CLOCK_MONOTONIC.</summary>
    public long Now => _pacing?.Now ?? Monotonic.NowNs;

    /// <summary>Asks playback to stop (any thread, also a signal handler); the run then tears down and reports.</summary>
    public void RequestStop(StopReason reason = StopReason.User) =>
        Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)StopReason.None);

    /// <summary>Continues the file from <paramref name="mediaTimeNs"/> (any thread): a discontinuity, never interpolated across.</summary>
    public void Seek(long mediaTimeNs)
    {
        var source = _source as FilePlaybackSource ?? throw new InvalidOperationException("The session is not playing a file.");
        source.Seek(mediaTimeNs);
    }

    /// <summary>Builds the whole pipeline. Throws <see cref="PlaybackUnavailableException"/> (one line) when a GPU, FFmpeg,
    /// the VFX SDK (for VFG), a display or the capture device is missing, <see cref="FileSourceException"/> for a file that
    /// cannot be played, <see cref="CaptureFormatException"/> when the device does not offer what was asked.</summary>
    public PlaybackSetup Open()
    {
        ObjectDisposedException.ThrowIf(_tornDown, this);
        if (_opened)
        {
            return _setup!;
        }

        _opened = true;
        try
        {
            return OpenCore();
        }
        catch
        {
            Teardown(null);
            throw;
        }
    }

    private PlaybackSetup OpenCore()
    {
        Require(OperatingSystem.IsLinux(), "needs Linux: CUDA/Vulkan interop shares memory through opaque file descriptors (Windows is Phase 10)");
        var capture = _o.Capture;
        if (capture is null)
        {
            Require(FileFrameSource.IsAvailable(out var ffmpeg), $"needs the FFmpeg 9 shared libraries: {ffmpeg}");
        }
        else
        {
            Require(CaptureDevices.IsAvailable(out var v4l2), v4l2 ?? "capture is unavailable");
        }

        string? sdk = null;
        if (_o.Generator == GeneratorKind.Vfg && _hooks.GeneratorFactory is null)
        {
            var settingsDirectory = _o.Settings?.Generator.VfxSdkDirectory;
            Require(VfxSdk.TryLoad(out var why, _o.VfxSdkDirectory, settingsDirectory), $"needs the NVIDIA VFX SDK with Video Frame Generation for VFG: {why}");
            sdk = $"VFX SDK {VfxSdk.Version} from {VfxSdk.Location}";
        }

        // Process-wide, before anything touches the Vulkan loader (SDL creating a Vulkan window included).
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        Require(VulkanLoader.TryInitialize(out var loaderError), $"needs the Vulkan loader: {loaderError}");
        if (_o.Display == DisplayKind.Window)
        {
            Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
                "needs a Wayland session for a window (WAYLAND_DISPLAY is not set); use --offscreen");
            Require(SdlWindow.IsLibraryAvailable(), "needs SDL3 (libSDL3.so.0) for a window; use --offscreen");
            try
            {
                _window = SdlWindow.Create(new SdlWindowOptions
                {
                    Title = capture is null ? $"fmp play - {Path.GetFileName(_o.Path)}" : $"fmp capture - {_o.Path}",
                    Width = _o.WindowWidth,
                    Height = _o.WindowHeight,
                });
            }
            catch (InvalidOperationException e)
            {
                throw new PlaybackUnavailableException($"cannot open a window: {e.Message}; use --offscreen", e);
            }
        }

        var cudaDevice = _cuda.Device;
        try
        {
            _instance = VulkanInstance.Create(new VulkanInstanceOptions
            {
                ApplicationName = "fmp play",
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
            throw new PlaybackUnavailableException($"needs a Vulkan device on the CUDA GPU ({GpuIdentity.UuidOf(cudaDevice)}): {FirstLine(e.Message)}", e);
        }
        catch (VulkanException e)
        {
            throw new PlaybackUnavailableException($"needs a working Vulkan driver: {FirstLine(e.Message)}", e);
        }

        // The design's three streams: copy (FFmpeg/NVDEC, the source's alone), compute (the conversion) and the
        // high-priority generator stream, on which every ring write goes (VFG's NVVFX_CUDA_STREAM).
        _cuda.MakeCurrent();
        _kernels = ImageKernels.Load(_cuda);
        _copyStream = CudaStream.Create(_cuda);
        _computeStream = CudaStream.Create(_cuda);
        _generatorStream = CudaStream.Create(_cuda, CudaStreamPriority.High);
        _source = capture is null
            ? new FilePlaybackSource(_cuda, _o.Path, FilePlaybackSource.OptionsFor(_o, _copyStream, _computeStream, _kernels))
            : OpenCapture(capture);
        var info = _source.Frames.Info;
        var width = info.Width;
        var height = info.Height;
        var format = info.Format;

        if (_window is not null)
        {
            _target = new SwapchainTarget(_device, _window);
        }
        else
        {
            _target = new OffscreenTarget(_device, new OffscreenTargetOptions
            {
                Width = _o.OffscreenWidth > 0 ? _o.OffscreenWidth : width,
                Height = _o.OffscreenHeight > 0 ? _o.OffscreenHeight : height,
                Readback = _hooks.OffscreenReadback,
                RefreshRate = _o.OffscreenRate,
            });
        }

        _presenter = new VulkanPresenter(_device, _target, new VulkanPresenterOptions { MeasureGpuTime = true });

        // The generator first: VFG's load and warm-up synchronise the context, so they belong here, before anything runs.
        var config = new FrameGenConfig { Width = width, Height = height, Format = format, Quality = _o.Quality, Color = info.Color };
        VfgLoadReport? load = null;
        if (_hooks.GeneratorFactory is { } factory)
        {
            _generator = factory(_cuda, _kernels, _generatorStream);
            _generator.Generator.Configure(config);
        }
        else
        {
            switch (_o.Generator)
            {
                case GeneratorKind.Vfg:
                    {
                        var vfg = new VfgGenerator(_cuda, new VfgOptions { Stream = _generatorStream, AutomaticShotChangeDetection = true });
                        try
                        {
                            vfg.Configure(config);
                        }
                        catch
                        {
                            vfg.Dispose();
                            throw;
                        }

                        load = vfg.LastLoad;
                        _generator = new GeneratorHandle(vfg, vfg.TryReadGpuTime, $"VFG {QualityName(_o.Quality)}");
                        break;
                    }

                case GeneratorKind.Blend:
                    {
                        var blend = new BlendGenerator(_cuda, _kernels, _generatorStream);
                        ((IFrameGenerator)blend).Configure(config);
                        _generator = new GeneratorHandle(blend, blend.TryReadGpuTime, "blend");
                        break;
                    }

                case GeneratorKind.Repeat:
                    {
                        var repeat = new RepeatGenerator(_cuda, _generatorStream);
                        ((IFrameGenerator)repeat).Configure(config);
                        _generator = new GeneratorHandle(repeat, repeat.TryReadGpuTime, "repeat");
                        break;
                    }

                default:
                    break;
            }
        }

        if (_generator is { } made && _hooks.WrapGenerator is { } wrap)
        {
            _generator = wrap(made, _generatorStream, _kernels);
        }

        _initialGenerationNs = InitialGenerationTime(_o.Generator, _o.Quality);
        var operations = OperationsPerWrite(_generator is null ? GeneratorKind.None : _o.Generator, _o.Quality);
        var slots = new VulkanPresenterOptions().SlotCount;
        var maxLead = Math.Min(slots + 1, CudaVulkanBridgeOptions.StreamQueueOperations / operations);
        _bridge = new CudaVulkanBridge(_cuda, _presenter, new CudaVulkanBridgeOptions
        {
            OperationsPerWrite = operations,
            MaxLead = maxLead,
            OutputColor = info.Color,
        });
        _bridge.Configure(new BridgeConfig { Width = width, Height = height, Format = format });

        var records = Math.Max(_o.TelemetryCapacity, 1024);
        _writer = new RingWriter(_cuda, _generatorStream, _generator, format, RingWriterQueue, records) { BeforeJob = _hooks.BeforeWrite };
        _presentLoop = new PresentLoop(
            _presenter,
            _o.Display,
            _o.Clock,
            _o.Display == DisplayKind.Window ? _presenter.Timing.RefreshRate : _o.OffscreenRate,
            _o.LatchLeadNs,
            _o.StallTimeoutNs,
            PresentQueue,
            records,
            _o.PresentLeadNs,
            DefaultPresentLeadRefreshes,
            _o.AdaptiveLead)
        {
            BeforeJob = _hooks.BeforePresent,
            OnPresented = _hooks.OnPresented,
            DropWhen = _hooks.DropPresent,
        };

        var ring = _bridge.Ring!;
        _setup = new PlaybackSetup
        {
            Stream = (_source as FilePlaybackSource)?.StreamInfo,
            Source = new SourceDescription
            {
                Width = width,
                Height = height,
                Format = format,
                Color = info.Color,
                NominalRate = _source.NominalRate,
                IsLive = _source.IsLive,
                Text = _source.Describe(),
            },
            Capture = CaptureSetupOf(_source, capture),
            Generator = _generator is null ? GeneratorKind.None : _o.Generator,
            GeneratorName = _generator?.Name ?? "none (real frames only)",
            Quality = new FrameGenQualityName(QualityName(_o.Quality)),
            VfgLoad = load,
            VfxSdk = sdk,
            GeneratorMayBlock = _generator?.Generator.Capabilities.EnqueueMayBlock ?? false,
            Display = _target is SwapchainTarget swapchain
                ? $"window {_window!.PixelWidth}x{_window.PixelHeight} px ({_window.VideoDriver}), swapchain {swapchain.SwapchainFormatName} / {swapchain.SwapchainColorSpaceName}"
                : string.Create(CultureInfo.InvariantCulture, $"offscreen {_target.Width}x{_target.Height} {((OffscreenTarget)_target).PixelFormat} at a nominal {_o.OffscreenRate} Hz, {(_o.Clock == PlaybackClock.Virtual ? "virtual clock" : "real clock")}"),
            FeedbackMode = _target is SwapchainTarget feedback
                ? feedback.ActiveFeedback + (feedback.PresentTimingUnavailableReason is { } noTiming
                    ? $"; no first-pixel times ({noTiming}): shows are timed by their reports"
                    : $"; first-pixel times from VK_EXT_present_timing ({feedback.PresentTimingStage}, CLOCK_MONOTONIC)")
                : "offscreen (a present completes with its GPU work)",
            RingSlots = ring.SlotCount,
            RingPitchBytes = ring.PitchBytes,
            OperationsPerWrite = _bridge.OperationsPerWrite,
            MaxLead = _bridge.MaxOutstanding,
            PresentsInFlightCap = _presenter.Capabilities.MaxFramesInFlight,
            Transform = _presenter.ActiveTransform.ToString(),
            DeviceName = cudaDevice.Name,
            VulkanDriver = _device.PhysicalDevice.DriverVersionText,
        };
        return _setup;
    }

    /// <summary>
    /// Opens the capture device with the session's streams and kernels (a drained point). A missing or busy device is
    /// <see cref="PlaybackUnavailableException"/> (one line); a mode it does not offer, <see cref="CaptureFormatException"/>.
    /// </summary>
    private CapturePlaybackSource OpenCapture(CaptureSourceOptions capture)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlaybackUnavailableException("capture needs Linux (V4L2)");
        }

        try
        {
            return new CapturePlaybackSource(_cuda, capture with
            {
                Device = _o.Path,
                CopyStream = _copyStream,
                ComputeStream = _computeStream,
                Kernels = _kernels,
                QueueDepth = CaptureQueueDepth,
                HistoryDepth = _o.HistoryDepth,
            });
        }
        catch (CaptureUnavailableException e)
        {
            throw new PlaybackUnavailableException(e.Message, e);
        }
    }

    private static CaptureSetup? CaptureSetupOf(IPlaybackSource source, CaptureSourceOptions? options)
    {
        if (!OperatingSystem.IsLinux() || source is not CapturePlaybackSource capture)
        {
            return null;
        }

        var s = capture.Source;
        return new CaptureSetup
        {
            Device = s.DeviceInfo,
            Format = s.Format,
            Color = s.Color,
            BufferMode = s.BufferMode,
            RefusedBufferModes = s.RefusedBufferModes,
            Buffers = s.BufferCount,
            SourceChangeEvents = s.SourceChangeEvents,
            Input = s.InputDescription,
            Cadence = options?.Cadence ?? false,
            CadenceOptions = s.Cadence.Options,
        };
    }

    /// <summary>The capture's summary once it has stopped (or as it stands, when it could not be stopped).</summary>
    private static CaptureSummary? CaptureSummaryOf(IPlaybackSource? source) =>
        OperatingSystem.IsLinux() && source is CapturePlaybackSource capture ? capture.Summarise() : null;

    /// <summary>Pre-rolls, plays until a stop and tears down. Returns what was measured.</summary>
    public PlaybackReport Run()
    {
        if (!_opened)
        {
            _ = Open();
        }

        ObjectDisposedException.ThrowIf(_tornDown || _ran, this);
        _ran = true;
        var collector = new TelemetryCollector(_o.TelemetryCapacity, _o.WarmupNs);
        var report = new PlaybackReport { Options = _o, Setup = _setup!, ValidationEnabled = _o.Validate, Statistics = collector.Statistics };
        report.CsvPath = _o.CsvPath;
        TelemetryCsvWriter? csv = null;
        DedicatedThread? writerThread = null, presentThread = null, pacingThread = null;
        var gcScope = GcLatencyScope.Enter(GCLatencyMode.SustainedLowLatency);
        report.GcMode = $"{GcConfiguration.Describe()}, playback latency mode {gcScope.Obtained}";
        var gcAtStart = GcSnapshot.Take();
        try
        {
            if (_o.CsvPath is { } path)
            {
                csv = new TelemetryCsvWriter(File.Create(path));
                csv.WriteHeader();
            }

            writerThread = DedicatedThread.Start("fmp-generator", _o.Threads, _writer!.Run);
            presentThread = DedicatedThread.Start("fmp-present", _o.Threads, _presentLoop!.Run);
            report.WriterThread = writerThread.Report.Summary;
            report.PresentThread = presentThread.Report.Summary;

            PreRoll(report);
            var (scheduler, startNs) = CreateScheduler(report);

            // The verifier's rows, sized before playback (one per slot and filler; 30 minutes when the run has no limit).
            var seconds = _o.DurationNs > 0 ? _o.DurationNs / 1e9 : 1800;
            collector.ReserveVerifierRows((int)Math.Min(4_000_000, (seconds * report.DisplayRate.Hz * 1.25) + 8192));
            var origin = startNs + StartDelayNs;
            var nominal = _source!.NominalRate.IsValid ? (long)_source.NominalRate.PeriodNs : Nanoseconds.PerSecond / 30;
            var feedAhead = Math.Max(4 * nominal, 50 * Nanoseconds.PerMillisecond) + report.MarginNs;
            _pacing = new PacingLoop(
                scheduler,
                _source,
                _bridge!,
                _generator?.Generator,
                _writer,
                _presentLoop,
                (_o.Capture is null ? _o.DecodeAhead : CaptureQueueDepth) + _o.HistoryDepth,
                Math.Max(_o.TelemetryCapacity, 1024),
                _o.Clock,
                startNs,
                feedAhead,
                _o.StallTimeoutNs,
                _o.DurationNs,
                _o.MaxSlots,
                _o.WarmupNs,
                _presentLeadNs,
                _startupFrames,
                StartLead(report.DisplayRate))
            {
                OnTurn = _hooks.PacingTurn,
                OtherMeters = [_writer.Meter, _presentLoop.Meter],
            };
            _source.Start(origin);
            _presentLoop.EnableFillers();
            pacingThread = DedicatedThread.Start("fmp-pacing", _o.Threads, _pacing.Run);
            report.PacingThread = pacingThread.Report.Summary;

            Control(report, collector, csv, pacingThread, writerThread, presentThread);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            report.Failure ??= e;
            report.StopReason = StopReason.Failure;
        }
        finally
        {
            StopThreads(report, pacingThread, writerThread, presentThread);
            if (_pacing is not null && _writer is not null && _presentLoop is not null)
            {
                collector.Drain(_pacing, _writer, _presentLoop);
            }

            collector.Flush();
            Verify(report, collector);
            DrainCsv(collector, csv, report);
            csv?.Dispose();
            Collect(report, collector);
            report.RunGc = GcSnapshot.Take().Since(gcAtStart);
            gcScope.Dispose();
            Teardown(report);
        }

        return report;
    }

    public void Dispose()
    {
        if (!_tornDown)
        {
            Teardown(null);
        }
    }

    /// <summary>
    /// Black frames through the whole path before playback: they put a window on its output (so its refresh rate becomes
    /// known), let the present thread learn the refresh grid from feedback, run every per-frame path once on every thread
    /// (no first-use work in the measured window) and start the GPU clock. Bounded.
    /// </summary>
    private void PreRoll(PlaybackReport report)
    {
        var window = _o.Display == DisplayKind.Window;
        var giveUp = Monotonic.NowNs + (3 * Nanoseconds.PerSecond);
        var frames = 0L;
        var presentLoop = _presentLoop!;
        var bridge = _bridge!;
        while (true)
        {
            PumpWindow();
            if (_stopReason != (int)StopReason.None)
            {
                break;
            }

            var done = window ? presentLoop.GridReady && frames >= 24 : frames >= 6;
            if (done || Monotonic.NowNs > giveUp)
            {
                break;
            }

            // At most two pre-roll presents waiting: the present thread shows them as fast as the display takes them.
            if (presentLoop.Jobs.Count >= 2 || !bridge.TryAcquireSlot(out var slot))
            {
                Thread.Sleep(1);
                continue;
            }

            var index = -1_000_000 + frames;
            _ = _writer!.Jobs.TryEnqueue(new WriteJob { SlotIndex = index, Kind = WriteKind.Fill, Slot = slot });
            _writer.Wake();
            _ = presentLoop.Jobs.TryEnqueue(new PresentJob
            {
                SlotIndex = index,
                Kind = PresentKind.PreRoll,
                Request = new PresentRequest { SlotIndex = index, Frame = slot.ConsumerView, TargetTimeNs = Monotonic.NowNs },
                Write = slot.Write,
                TargetNs = Monotonic.NowNs,
                SlotKind = SlotKind.Real,
                FrameA = -1,
                FrameB = -1,
            });
            presentLoop.Wake();
            frames++;
        }

        // Every pre-roll write finishes before playback starts (bounded).
        var drainBy = Monotonic.NowNs + (2 * Nanoseconds.PerSecond);
        while (bridge.Outstanding > 0 && Monotonic.NowNs < drainBy)
        {
            PumpWindow();
            Thread.Sleep(1);
        }

        if (window)
        {
            // Let the last pre-roll presents be shown so the grid is current, and the window's rate settled.
            var settle = Monotonic.NowNs + (100 * Nanoseconds.PerMillisecond);
            while (Monotonic.NowNs < settle && _stopReason == (int)StopReason.None)
            {
                PumpWindow();
                Thread.Sleep(2);
            }
        }

        report.DisplayRateKnown = !window || (_presenter!.IsTimingKnown && presentLoop.GridReady);
    }

    /// <summary>Window events outside the control loop (the pre-roll): a close or Escape stops the run there too.</summary>
    private void PumpWindow()
    {
        if (_window is not null && (_window.PumpEvents() & (WindowEvents.CloseRequested | WindowEvents.EscapePressed)) != 0)
        {
            RequestStop(StopReason.User);
        }
    }

    private (FrameScheduler Scheduler, long StartNs) CreateScheduler(PlaybackReport report)
    {
        var settings = _o.Settings ?? new FmpSettings();
        var window = _o.Display == DisplayKind.Window;
        var now = Monotonic.NowNs;
        FrameRate rate;
        long anchor;
        long presentLead;
        var grid = _presentLoop!.Grid;
        if (window && grid.AnchorNs != long.MinValue)
        {
            (anchor, rate, presentLead) = grid;
        }
        else
        {
            rate = window ? _presenter!.Timing.RefreshRate : _o.OffscreenRate;
            anchor = now;
            presentLead = window ? _presentLoop.LeadNs : 0;
        }

        // The present lead (the present thread's; it adapts to the output from here on): windowed, 1.5 refreshes to start
        // with (measured: a lead of one refresh puts each queued present at the compositor's own latch of the refresh before
        // and it skips refreshes; 2 to 3 ms is too late for its latch), or --present-lead-ms.
        var baseOptions = settings.Scheduler.ToOptions(settings.Display);
        var asked = _o.OutputHz is { } hz && _source!.NominalRate.IsValid ? Math.Clamp((int)Math.Round(hz / _source.NominalRate.Hz), 1, 8) : _o.Multiplier;
        var multiplier = _generator is null ? 1 : Math.Min(asked, Math.Max(1, _generator.Generator.Capabilities.MaxMultiplier));
        var options = baseOptions with
        {
            ClockMode = ClockMode.FixedRefresh,
            DisplayRate = rate,
            MaxMultiplier = multiplier,
            InitialGenerationTimeNs = _initialGenerationNs,

            // The scheduler's V is when a slot's present must be queued: the refresh minus the present lead.
            VblankAnchorNs = anchor - presentLead,
            StartupFrames = Math.Min(baseOptions.StartupFrames, _o.HistoryDepth - 2),
        };
        _startupFrames = options.StartupFrames;
        report.DisplayRate = rate;
        report.Multiplier = multiplier;
        report.PresentLeadNs = presentLead;
        report.MarginNs = options.MarginNs;
        report.InitialGenerationTimeNs = options.InitialGenerationTimeNs;
        _presentLeadNs = presentLead;
        return (new FrameScheduler(options), anchor);
    }

    /// <summary>The control thread's loop: window events, telemetry, the warm-up boundary and the stop conditions.</summary>
    private void Control(PlaybackReport report, TelemetryCollector collector, TelemetryCsvWriter? csv, DedicatedThread pacing, DedicatedThread writer, DedicatedThread present)
    {
        var meter = new ThreadMeter();
        var fullscreenDone = !_o.Fullscreen;
        var measuring = false;
        while (true)
        {
            meter.Tick();
            if (_window is not null)
            {
                var events = _window.PumpEvents();
                if ((events & (WindowEvents.CloseRequested | WindowEvents.EscapePressed)) != 0)
                {
                    RequestStop(StopReason.User);
                }

                if (!fullscreenDone && _presenter!.IsTimingKnown)
                {
                    fullscreenDone = true;
                    _ = _window.SetFullscreen(true);
                }
            }

            collector.Drain(_pacing!, _writer!, _presentLoop!);
            DrainCsv(collector, csv, report);
            if (!measuring && _pacing!.MeasureFromNs != long.MinValue)
            {
                // The pacing thread started the window (and the other threads' meters) on its own clock.
                measuring = true;
                meter.Begin();
            }

            if (!pacing.IsAlive)
            {
                RequestStop(pacing.Failure is null ? _pacing!.Ended : StopReason.Failure);
                report.Failure ??= pacing.Failure;
            }

            if (!writer.IsAlive || !present.IsAlive)
            {
                report.Failure ??= writer.Failure ?? present.Failure ?? new InvalidOperationException("A pipeline thread ended on its own.");
                RequestStop(StopReason.Failure);
            }

            if (_source!.Failure is { } sourceFailure)
            {
                report.Failure ??= sourceFailure;
                RequestStop(StopReason.Failure);
            }

            if (_stopReason != (int)StopReason.None)
            {
                break;
            }

            if (_watchdog is not null && Monotonic.NowNs >= _watchdogAt)
            {
                _watchdogAt = Monotonic.NowNs + Nanoseconds.PerSecond;
                Watch(_watchdog);
            }

            Thread.Sleep(4);
        }

        report.StopReason = (StopReason)_stopReason;
        if (_pacing!.MeasureFromNs != long.MinValue)
        {
            meter.End();
            meter.Tick();
            _pacing.Meter.End();
            _writer!.Meter.End();
            _presentLoop!.Meter.End();
            report.MeasuredGc = GcSnapshot.Take().Since(_pacing.GcAtMeasure);
            report.Measured = true;
            report.Control = new ThreadCost(meter.AllocatedBytes, meter.CpuNs, meter.Ticks);
        }
    }

    private static void DrainCsv(TelemetryCollector collector, TelemetryCsvWriter? csv, PlaybackReport report)
    {
        while (collector.Records.TryRead(out var record))
        {
            csv?.Write(record);
            report.TelemetryRecords++;
        }

        report.TelemetryDropped = collector.Records.Dropped;
    }

    /// <summary>Stops the threads in pipeline order (bounded): the pacing thread first, then the generator thread once it
    /// has enqueued every write handed to it (each one must be signalled), then the present thread.</summary>
    private void StopThreads(PlaybackReport report, DedicatedThread? pacing, DedicatedThread? writer, DedicatedThread? present)
    {
        // At least 5 s: a generator thread held up for a while (a long GPU stall, a slow call) still finishes its queue.
        var timeout = TimeSpan.FromTicks(Math.Max(_o.StallTimeoutNs, 5 * Nanoseconds.PerSecond) / 100);
        _pacing?.RequestStop();
        if (pacing is not null && !pacing.Join(timeout))
        {
            report.TeardownErrors.Add(new TimeoutException("The pacing thread did not stop."));
        }

        _writer?.RequestStop();
        if (writer is not null && !writer.Join(timeout))
        {
            // Still inside a call (a generator that never returns): what it uses is left allocated at teardown.
            _writerStuck = true;
            report.TeardownErrors.Add(new TimeoutException("The generator thread did not stop."));
        }

        _presentLoop?.RequestStop();
        if (present is not null && !present.Join(timeout))
        {
            report.TeardownErrors.Add(new TimeoutException("The present thread did not stop."));
        }

        if (pacing?.Failure is { } p && !ReferenceEquals(p, report.Failure))
        {
            report.Failure ??= p;
        }

        if (writer?.Failure is { } w)
        {
            report.Failure ??= w;
        }

        if (present?.Failure is { } f)
        {
            report.Failure ??= f;
        }
    }

    /// <summary>
    /// The end of a run: refreshes without a new present that no missed slot explains are counted as the pipeline's; then
    /// the telemetry verifier recomputes the verdict from the telemetry alone (windowed runs with first-pixel times: the
    /// facts it judges from) and every slot's verdict is compared with the live accounting's.
    /// </summary>
    private void Verify(PlaybackReport report, TelemetryCollector collector)
    {
        var liveUnexplained = collector.ExplainGaps(collector.MeasureFromNs, collector.Statistics.LastMeasuredNs);
        if (_o.Display != DisplayKind.Window || _o.Clock != PlaybackClock.Real || _presenter is not { ReportsPresentTimes: true } || collector.PeriodNs <= 0
            || collector.MeasureFromNs == long.MaxValue)
        {
            report.VerificationSkipped = "the verifier judges windowed runs with first-pixel times";
            return;
        }

        var stats = collector.Statistics;
        var result = TelemetryVerifier.Verify(collector.VerifierRows, collector.PeriodNs, collector.MeasureFromNs, stats.LastMeasuredNs);
        if (result.UnexplainedRefreshes != liveUnexplained)
        {
            result.Disagreements += Math.Abs(result.UnexplainedRefreshes - liveUnexplained);
        }

        // Rows or gap candidates that did not fit their buffers: the two could not be compared on those.
        result.Disagreements += (int)Math.Min(int.MaxValue, collector.VerifierRowsDropped + collector.GapsDropped);

        report.Verification = result;
        report.VerifierRowsDropped = collector.VerifierRowsDropped;
    }

    private void Collect(PlaybackReport report, TelemetryCollector collector)
    {
        var s = collector.Statistics;
        report.PlaybackNs = _pacing is { PlaybackStartNs: not long.MinValue } pacing ? Math.Max(0, pacing.Now - pacing.PlaybackStartNs) : 0;
        report.WarmupNs = _o.WarmupNs;
        if (_pacing is { } p)
        {
            report.Counters = p.Scheduler.Counters;
            report.Diagnostics = p.Scheduler.Diagnostics;
            report.FramesRead = p.FramesRead;
            report.Discontinuities = p.Discontinuities;
            report.SkippedBackPressure = p.SkippedBackPressure;
            report.SkippedPresenterBacklog = p.SkippedPresenterBacklog;
            report.MaxOutstanding = p.MaxOutstanding;
            report.DisplayUpdates = p.DisplayUpdatesApplied;
            report.PresentLeadNs = p.PresentLeadNs;
            report.ContentOffsetNs = p.ContentOffsetNs;
            report.Pacing = Cost(p.Meter);
        }

        if (_writer is { } w)
        {
            report.Copies = w.Copies;
            report.GeneratedWrites = w.Generated;
            report.Fallbacks = w.Fallbacks;
            report.GeneratorFaults = w.Faults;
            report.GeneratorFaulted = w.GeneratorFaulted;
            report.FirstGeneratorFault = w.FirstFault is { } fault ? $"{fault.GetType().Name}: {FirstLine(fault.Message)}" : null;
            report.Writer = Cost(w.Meter);
        }

        if (_presentLoop is { } pl)
        {
            report.Shown = pl.Shown;
            report.TimedShows = pl.TimedShows;
            report.Fillers = pl.Fillers;
            report.DroppedLate = pl.DroppedLate;
            report.DroppedRefused = pl.DroppedRefused;
            report.PresentRefusals = pl.Refusals;
            report.LateShows = pl.LateShows;
            report.MaxPresentsInFlight = Math.Max(pl.MaxInFlight, s.MaxPresentsInFlight);
            report.DisplayRateChanges = pl.RateChanges;
            report.Presenter = Cost(pl.Meter);
            report.LeadRaises = pl.LeadRaises;
            report.PeakLeadNs = pl.PeakLeadNs;
            report.FinalLeadNs = pl.LeadNs;
        }

        report.FirstShownFrame = collector.FirstShownFrame;

        if (_presenter is { } presenter)
        {
            report.PresenterStats = presenter.Stats;

            // The presenter's own count (submitted and not completed): the present thread's lags by the feedback it has
            // not drained yet, so it can read one more than are actually queued.
            report.MaxPresentsInFlight = presenter.Stats.MaxPresentsInFlight;
            if (_o.Display == DisplayKind.Window && presenter.IsTimingKnown)
            {
                report.DisplayRate = presenter.Timing.RefreshRate;
            }
        }

        if (_source is FilePlaybackSource file)
        {
            report.SourceStatistics = file.Source.Statistics;
        }

        report.Capture = CaptureSummaryOf(_source);

        report.OutOfOrderSignals = _bridge?.OutOfOrderSignals ?? 0;
        if (_watchdog is not null && _presentLoop is not null)
        {
            _watchdog.WriteLine($"watchdog: present-thread stalls (1 feedback, 2 ready, 3 present, 4 wait, 5 turn): {_presentLoop.Stalls}");
        }
    }

    private static ThreadCost Cost(ThreadMeter meter) => new(meter.AllocatedBytes, meter.CpuNs, meter.Ticks);

    /// <summary>
    /// Frees everything in the order the components require: the source is stopped (its decode thread may wait inside
    /// NVDEC on release points: every write that read a frame is signalled by now), the generator stream drained, the
    /// source freed while the ring's semaphores (its frames' release points) are still alive, then the bridge (drains and
    /// frees its imports; the presenter is not presenting any more), the generator (VFG's destroy synchronises the
    /// context: a drained point), the CUDA objects, the presenter, its target, the window and the Vulkan device and
    /// instance (whose destruction is when the validation layer reports leaks). Never throws; errors are reported.
    /// </summary>
    /// <summary>How far ahead of now the first frame is put (two refreshes: past the scheduler's first decision).</summary>
    private static long StartLead(FrameRate rate) => rate.IsValid ? (long)Math.Round(2 * rate.PeriodNs) : 20 * Nanoseconds.PerMillisecond;

    private void Teardown(PlaybackReport? report)
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        var errors = report?.TeardownErrors ?? [];
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

        // Work on the generator stream that never finishes (a wait for a point nothing will reach, a generator that does
        // not return) blocks every call that synchronises the context: cuMemFree of the source's frames, cuModuleUnload of
        // the kernels, the generator's destroy, the decode thread inside NVDEC, and the Vulkan device's idle wait behind a
        // present that waits for such a write. Wait for it (bounded); if it does not finish, or the generator thread never
        // stopped, nothing is freed: everything GPU-side is abandoned (reported), so Run returns and the process can exit.
        var idle = !_writerStuck && WaitIdle(_generatorStream);
        if (!idle)
        {
            Abandon(report, errors);
            return;
        }

        if (_source is { } source)
        {
            Try(source.Stop);
            if (report is not null && CaptureSummaryOf(source) is { } stopped)
            {
                // Taken again now that the capture thread has stopped: its last counters and samples.
                report.Capture = stopped;
            }
        }

        if (_source is { } toFree)
        {
            Try(toFree.Dispose);
            if (report is not null)
            {
                report.SourceLeaked = toFree is FilePlaybackSource f ? f.Source.Leaked : OperatingSystem.IsLinux() && toFree is CapturePlaybackSource c && c.Source.Leaked;
            }
        }

        Try(() => _bridge?.Dispose());
        if (report is not null)
        {
            report.AbandonedImports = _bridge?.AbandonedImports ?? 0;
        }

        Try(() => _generator?.Generator.Dispose());
        Try(() => _writer?.Dispose());
        Try(() => _presentLoop?.Dispose());
        Try(() => _generatorStream?.Dispose());
        Try(() => _computeStream?.Dispose());
        Try(() => _copyStream?.Dispose());
        Try(() => _kernels?.Dispose());
        Try(() => _presenter?.Dispose());
        var unreaped = CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(3)) ? 0 : CudaVulkanBridge.PendingReaps;
        Try(() => _target?.Dispose());
        Try(() => _window?.Dispose());
        Try(() => _device?.Dispose());
        if (_instance is not null)
        {
            Try(_instance.Dispose);
            if (report is not null)
            {
                var log = _instance.Validation;
                report.ValidationProblems = log.ProblemCount;
                report.ValidationReport = log.ProblemCount > 0 ? log.Describe() : null;
            }
        }

        if (report is not null)
        {
            report.UnreapedImports = unreaped;
        }
    }

    /// <summary>
    /// Teardown when GPU work does not finish: nothing that synchronises the context or waits for the device is called (the
    /// source is not stopped or freed, nor the generator, the ring import, the streams, the kernels, the presenter, the
    /// window or the device); the threads that could still be inside such work (the decode thread, a generator thread that
    /// did not stop) are background threads. What was left is reported; <c>fmp play</c> prints its summary and exits
    /// explicitly.
    /// </summary>
    private void Abandon(PlaybackReport? report, List<Exception> errors)
    {
        var what = _writerStuck
            ? "the generator thread did not stop (inside a call that does not return)"
            : "work on the generator stream did not finish within 3 s";
        errors.Add(new TimeoutException($"Teardown abandoned: {what}; nothing GPU-side was freed (process exit reclaims it)."));
        try
        {
            _presentLoop?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        // A capture device is let go of even so (no GPU wait is involved): another program can use it at once.
        try
        {
            _source?.Abandon();
            if (report is not null && CaptureSummaryOf(_source) is { } summary)
            {
                report.Capture = summary;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors.Add(e);
        }

        if (report is not null)
        {
            report.GpuAbandoned = true;
            var capture = OperatingSystem.IsLinux() && _source is CapturePlaybackSource c
                ? c.Source.DeviceReleased ? "the capture source's GPU buffers (the device itself was released)" : "the capture source and its device (its thread did not stop, so the device was left open until the process exits)"
                : "the file source (decoder, frame pool, decode thread)";
            report.Abandoned = $"{what}: left allocated: {capture}, the generator" +
                ", the generator thread's events, the ring's CUDA import, the CUDA streams and kernels, the presenter, the window" +
                " and the Vulkan device";
            if (_instance is not null)
            {
                report.ValidationProblems = _instance.Validation.ProblemCount;
            }
        }

        _source = null;
        _generator = null;
        _writer = null;
        _bridge = null;
        _generatorStream = null;
        _computeStream = null;
        _copyStream = null;
        _kernels = null;
        _presenter = null;
        _target = null;
        _window = null;
        _device = null;
        _instance = null;
    }

    /// <summary>Diagnostics (<c>FMP_PLAY_WATCHDOG=1</c>): where each loop is, once a second, on stderr. Allocates.</summary>
    private void Watch(TextWriter w)
    {
        var ring = _bridge?.Ring;
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"watchdog: pacing phase {_pacing?.Phase} held {_pacing?.HeldCount} fed {_pacing?.FedCount} slots {_pacing?.SlotsSinceStart} now {_pacing?.Now}; " +
            $"writer jobs {_writer?.Jobs.Count} copies {_writer?.Copies} generated {_writer?.Generated}; present jobs {_presentLoop?.Jobs.Count} pending {_presentLoop?.HasPending} " +
            $"in flight {_presentLoop?.InFlightCount} shown {_presentLoop?.Shown} refusals {_presentLoop?.Refusals}; bridge handed out {_bridge?.LastAcquiredWrite} outstanding {_bridge?.Outstanding}; " +
            $"ring written {ring?.ReadWrittenValue()} released {ring?.ReadReleasedValue()}; presenter last copied {_presenter?.LastPresentedWrite} released {_presenter?.LastReleasedWrite} " +
            $"retained {_presenter?.RetainedWrite} presented {_presenter?.Stats.Presented} completed {_presenter?.Stats.Completed}; source queue {(_source as FilePlaybackSource)?.Source.QueueDepth} " +
            $"decoded {(_source as FilePlaybackSource)?.Source.Statistics.FramesDecoded} free {(_source as FilePlaybackSource)?.Source.History.FreeSlots} eos {_source?.IsEndOfStream}"));
    }

    private static bool WaitIdle(CudaStream? stream)
    {
        var deadline = Monotonic.NowNs + (3 * Nanoseconds.PerSecond);
        while (stream is not null && !stream.IsDisposed && !stream.IsIdle)
        {
            if (Monotonic.NowNs > deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    private void EchoValidation(ValidationMessage message)
    {
        if (message.IsProblem)
        {
            _validationEcho!.WriteLine($"fmp: validation: {message}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new PlaybackUnavailableException(message);
        }
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text : text[..newline];
    }

    public static string QualityName(FrameGenQuality quality) => quality switch
    {
        FrameGenQuality.Low => "Low",
        FrameGenQuality.High => "High",
        _ => "Medium",
    };

    /// <summary>The generation time assumed until the first one is measured (about p99 of d, docs/benchmarks/phase3-vfg.md).</summary>
    public static long InitialGenerationTime(GeneratorKind kind, FrameGenQuality quality) => kind switch
    {
        GeneratorKind.Vfg => quality switch
        {
            FrameGenQuality.Low => 700 * Nanoseconds.PerMicrosecond,
            FrameGenQuality.High => 6_500 * Nanoseconds.PerMicrosecond,
            _ => 1_600 * Nanoseconds.PerMicrosecond,
        },
        GeneratorKind.Blend or GeneratorKind.Repeat => 200 * Nanoseconds.PerMicrosecond,
        _ => 200 * Nanoseconds.PerMicrosecond,
    };

    /// <summary>
    /// CUDA operations one ring write enqueues on the generator stream, for the bridge's budget (a stream holds about
    /// 1,024; docs/benchmarks/phase2-interop.md): VFG's run (measured: Low 28, Medium 33, High 170) plus three waits, two
    /// span records, the signal and the GPU clock's mark, rounded up; a copy or blend: 8, declared 16.
    /// </summary>
    public static int OperationsPerWrite(GeneratorKind kind, FrameGenQuality quality) => kind switch
    {
        GeneratorKind.Vfg => quality switch
        {
            FrameGenQuality.Low => 40,
            FrameGenQuality.High => 192,
            _ => 48,
        },
        _ => 16,
    };
}
