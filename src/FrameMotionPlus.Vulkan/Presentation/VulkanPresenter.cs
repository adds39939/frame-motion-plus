using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan;

public sealed record VulkanPresenterOptions
{
    /// <summary>
    /// Presentation ring depth, 4 to 6. This is the one authority for the slot count: the ring the presenter creates
    /// has exactly this many slots, and producers and bridges take it from <see cref="PresentationRing.SlotCount"/>
    /// (a bridge's own slot count must be 0, meaning "the presenter's", or equal to it).
    /// </summary>
    public int SlotCount { get; init; } = PresentationRingConfig.DefaultSlotCount;

    public int PitchAlignment { get; init; } = PresentationRingConfig.DefaultPitchAlignment;

    /// <summary>Frames (and swapchain images) in flight, 1 or 2. The design caps this at 2.</summary>
    public int MaxFramesInFlight { get; init; } = 2;

    public PresentScaler Scaler { get; init; } = PresentScaler.Bilinear;

    /// <summary>Bound on the CPU waits outside the hot path (reconfigure, shutdown).</summary>
    public long DrainTimeoutNs { get; init; } = 2_000_000_000;

    /// <summary>Timestamp queries around the copy and the pass, reported in <see cref="VulkanPresenter.Stats"/>.</summary>
    public bool MeasureGpuTime { get; init; } = true;
}

public readonly record struct PresenterStats
{
    /// <summary>Requests submitted for display (TryPresent returned true).</summary>
    public long Presented { get; init; }

    /// <summary>
    /// Requests not shown (TryPresent returned false). The newest such write stays presentable until a newer write is
    /// handed to the presenter or a release covers it; older ones are released.
    /// </summary>
    public long Dropped { get; init; }

    /// <summary>Presented requests that re-showed the last write from the staged copy.</summary>
    public long Repeats { get; init; }

    /// <summary>Presented requests for a write that an earlier request had dropped (and the presenter had retained).</summary>
    public long RetainedShown { get; init; }

    /// <summary>Requests for a write older than the last one presented or released (refused, nothing read).</summary>
    public long StaleRequests { get; init; }

    /// <summary>
    /// Requests refused outright, with nothing submitted: a write more than
    /// <see cref="RingProtocol.MaxWriteLead"/> past the last release, one that would leave more than
    /// <see cref="RingProtocol.MaxWaitingReleases"/> releases waiting for unfinished writes, or a frame whose shape is
    /// not the current ring's (a request from a ring that Configure has replaced). Counts TryPresent and Release. A
    /// refused write is neither shown, retained nor released: the next accepted request or release covers it. A
    /// producer that keeps within the bounds never causes one (the CUDA/Vulkan bridge cannot hand out a write that
    /// would), so a non-zero count means a producer ran past them or used a stale request.
    /// </summary>
    public long RejectedRequests { get; init; }

    /// <summary>Present-wait queries made to the driver (each costs tens of microseconds; about one per frame).</summary>
    public long PresentWaitPolls { get; init; }

    /// <summary>Release-only submissions (explicit releases, releases of superseded writes and catch-up strides).</summary>
    public long Releases { get; init; }

    /// <summary>Presents known to have completed (shown, or GPU-complete where the target cannot tell).</summary>
    public long Completed { get; init; }

    /// <summary>Largest number of presents outstanding right after a submit.</summary>
    public long MaxPresentsInFlight { get; init; }

    public long TargetRecreations { get; init; }

    public long FeedbackOverflows { get; init; }

    /// <summary>GPU time of the last measured ring-to-staged copy, ns (0 when not measured).</summary>
    public long LastCopyGpuNs { get; init; }

    /// <summary>GPU time of the last measured scale/transform pass, ns.</summary>
    public long LastPassGpuNs { get; init; }
}

/// <summary>
/// Shows presentation-ring slots on a <see cref="PresentTarget"/>. Per frame, all GPU-side: wait on the slot's
/// <em>written</em> value, copy the ring buffer into an optimal-tiling staged image (a compute dispatch), signal
/// <em>released</em>, then one pass that scales into the aspect-preserving content rectangle (black bars elsewhere),
/// applies the output transform and writes the target image, which is then presented. <see cref="TryPresent"/> never
/// waits for the GPU; neither does a target rebuild after a resize, which is deferred until in-flight frames finish.
/// <para>
/// The presenter creates and owns the ring (in <see cref="Configure"/>; see <see cref="Ring"/>). Present requests name
/// a write by its <em>written</em> value: <c>Frame = ring.ConsumerFrame(n)</c>, <c>Ready = ring.WrittenSignal(n)</c>
/// (an empty <c>Ready</c> falls back to <c>Frame.Ready</c>, which ConsumerFrame sets to the same point).
/// The protocol, including which writes stay presentable after a refused request, is <see cref="RingProtocol"/>.
/// </para>
/// <para>
/// Fixed-refresh FIFO, at most <see cref="VulkanPresenterOptions.MaxFramesInFlight"/> frames in flight: a request that
/// would exceed it, or finds no image, returns false at once. Single-threaded: call every member from the present
/// thread. Dispose the presenter before its target.
/// </para>
/// </summary>
public sealed unsafe class VulkanPresenter : IPresenter
{
    private const int PendingCapacity = 8;

    // Holds more than RingProtocol.MaxWaitingReleases + 1, the most submissions ever unconfirmed (WithinWaitingBound).
    private const int ReleaseHistory = 1024;

    // A present-wait answer is reused until a present is submitted or this much time has passed (500 µs): one driver
    // query per frame for a caller that checks readiness once per refresh, at most one per 500 µs for one that spins.
    private static readonly long RepollTicks = Stopwatch.Frequency / 2000;

    private readonly VulkanDevice _device;
    private readonly VkDeviceApi _api;
    private readonly PresentTarget _target;
    private readonly VulkanPresenterOptions _options;
    private readonly PresentPass _pass;
    private readonly int _framesInFlight;
    private readonly VkCommandPool[] _pools;
    private readonly VkCommandBuffer[] _copyCmd;
    private readonly VkCommandBuffer[] _renderCmd;
    private readonly VkSemaphore[] _acquire;
    private readonly ulong[] _slotFrame;
    private readonly bool[] _slotHadCopy;
    private readonly ulong[] _pendingId = new ulong[PendingCapacity];
    private readonly long[] _pendingSlot = new long[PendingCapacity];
    private readonly long[] _pendingTarget = new long[PendingCapacity];
    private readonly SpscRing<PresentFeedback> _feedback = new(256);
    private readonly ulong[] _releaseValues = new ulong[ReleaseHistory];
    private readonly double _timestampPeriodNs;
    private readonly ulong _timestampMask;
    private VkSemaphore _frameDone;
    private VkQueryPool _queries;

    private PresentationRing? _ring;
    private VkImage _staged;
    private VkDeviceMemory _stagedMemory;
    private VkImageView _stagedView;
    private VkImageView _stagedWords;
    private ulong _lastCopied;
    private ulong _lastReleased;
    private ulong _retained;
    private ulong _maxWaitedWrite;

    // The released values of the current ring's submissions that signal it (releases and copies), in submission order
    // (_releaseValues, the last ReleaseHistory); those from _firstUnconfirmed on are not yet known to have run.
    private long _releaseSubmits;
    private long _firstUnconfirmed;

    private OutputRequest _request;
    private bool _configured;
    private VkPipeline _pipeline;

    private ulong _frame;
    private ulong _completedPresent;
    private int _pendingHead;
    private int _pendingCount;

    private long _presented;
    private long _dropped;
    private long _repeats;
    private long _retainedShown;
    private long _stale;
    private long _rejected;
    private int _ringGeneration;
    private long _presentWaitPolls;
    private long _pollStamp;
    private ulong _pollFrame = ulong.MaxValue;
    private long _releases;
    private long _maxInFlight;
    private long _recreations;
    private long _feedbackOverflows;
    private long _lastCopyNs;
    private long _lastPassNs;
    private bool _disposed;

    public VulkanPresenter(VulkanDevice device, PresentTarget target, VulkanPresenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Device != device)
        {
            throw new ArgumentException("The target belongs to a different device.", nameof(target));
        }

        _options = options ?? new VulkanPresenterOptions();
        if (_options.MaxFramesInFlight is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxFramesInFlight, "1 or 2 frames in flight.");
        }

        _device = device;
        _api = device.Api;
        _target = target;
        _framesInFlight = _options.MaxFramesInFlight;
        _pools = new VkCommandPool[_framesInFlight];
        _copyCmd = new VkCommandBuffer[_framesInFlight];
        _renderCmd = new VkCommandBuffer[_framesInFlight];
        _acquire = new VkSemaphore[_framesInFlight];
        _slotFrame = new ulong[_framesInFlight];
        _slotHadCopy = new bool[_framesInFlight];
        _timestampPeriodNs = device.Capabilities.TimestampPeriodNs;
        var bits = device.Capabilities.TimestampValidBits;
        _timestampMask = bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;

        _pass = new PresentPass(device);
        try
        {
            CreateFrameResources();
            target.Bind(_frameDone);
        }
        catch
        {
            DestroyAll();
            throw;
        }
    }

    public PresenterCapabilities Capabilities => new()
    {
        Features = _target.SupportsHdr10 ? PresenterFeatures.Hdr10 : PresenterFeatures.None,

        // 0: the target has no present feedback, so presents may queue behind the screen beyond the frame slots.
        MaxFramesInFlight = _target.CanCapQueuedPresents ? _framesInFlight : 0,
        Domain = MemoryDomain.Vulkan,
    };

    /// <summary>
    /// The display's timing. While <see cref="IsTimingKnown"/> is false the refresh rate is only a guess (a window is
    /// not on any output until it has shown a frame); a scheduler should not latch it before then.
    /// </summary>
    public DisplayTiming Timing => new() { RefreshRate = _target.RefreshRate };

    /// <summary>True once <see cref="Timing"/> describes the output the frames are actually shown on.</summary>
    public bool IsTimingKnown => _target.RefreshRateKnown;

    /// <summary>The ring frames must be written into, created by <see cref="Configure"/>. Null before.</summary>
    public PresentationRing? Ring => _ring;

    /// <summary>
    /// Increments whenever <see cref="Configure"/> replaces the ring; producers must import the new one. Like
    /// <see cref="LastReleasedWrite"/>, readable from any thread (volatile; written only by the present thread).
    /// </summary>
    public int RingGeneration => Volatile.Read(ref _ringGeneration);

    /// <summary>Tests: presents submitted so far (the frame counter the frame-done timeline counts to). Present thread.</summary>
    internal ulong SubmittedFrames => _frame;

    /// <summary>Tests: presents whose GPU work (slot copy and output pass) has finished. Present thread.</summary>
    internal ulong GpuFramesDone => _frameDone.IsNull ? 0 : FrameDoneValue();

    public PresentTarget Target => _target;

    public OutputTransform ActiveTransform { get; private set; }

    /// <summary>The write most recently copied for display (0 = none); it can be presented again as a repeat.</summary>
    public ulong LastPresentedWrite => _lastCopied;

    /// <summary>
    /// The highest <em>released</em> value submitted. Unlike the rest of the presenter this may be read from any thread
    /// (a producer's back-pressure): it is written with <see cref="Volatile.Write(ref ulong, ulong)"/> on the present
    /// thread and read with <see cref="Volatile.Read(ref readonly ulong)"/>, allocation-free. It only grows within a ring
    /// generation, so a read from another thread is at most behind, never ahead.
    /// </summary>
    public ulong LastReleasedWrite => Volatile.Read(ref _lastReleased);

    /// <summary>
    /// The write a refused request left presentable (0 = none): newer than <see cref="LastReleasedWrite"/>, released
    /// once a newer write is handed to the presenter or a release covers it.
    /// </summary>
    public ulong RetainedWrite => _retained;

    public PresenterStats Stats => new()
    {
        Presented = _presented,
        Dropped = _dropped,
        Repeats = _repeats,
        RetainedShown = _retainedShown,
        StaleRequests = _stale,
        RejectedRequests = _rejected,
        PresentWaitPolls = _presentWaitPolls,
        Releases = _releases,
        Completed = (long)_completedPresent,
        MaxPresentsInFlight = _maxInFlight,
        TargetRecreations = _recreations,
        FeedbackOverflows = _feedbackOverflows,
        LastCopyGpuNs = _lastCopyNs,
        LastPassGpuNs = _lastPassNs,
    };

    /// <summary>Test hook: when set, every copy batch also waits for this timeline value (see the release-ordering test).</summary>
    internal VkSemaphore CopyGate { get; set; }

    internal ulong CopyGateValue { get; set; }

    /// <summary>
    /// Sets the ring shape (<see cref="PresenterConfig.Width"/>, <see cref="PresenterConfig.Height"/>,
    /// <see cref="PresenterConfig.Format"/>: what will be presented) and the wanted output colour. A changed shape
    /// releases the retained write, drains in-flight work (bounded by <see cref="VulkanPresenterOptions.DrainTimeoutNs"/>),
    /// replaces the ring and bumps <see cref="RingGeneration"/>. A 10-bit ring asks the target for HDR10 unless
    /// <see cref="PresenterConfig.OutputColor"/> names an SDR transfer. Not for the hot path.
    /// </summary>
    public void Configure(in PresenterConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (config.EnableIntegratedFrameGeneration)
        {
            throw new NotSupportedException(
                "Smooth Motion is chosen when the instance is created (VulkanLayerPolicy.NvidiaSmoothMotion), not per configuration.");
        }

        if (config.ClockMode != ClockMode.FixedRefresh)
        {
            throw new NotSupportedException("Only fixed-refresh presentation exists so far; the VRR clock mode is Phase 6.");
        }

        var ringConfig = PresentationRing.Normalize(new PresentationRingConfig
        {
            SlotCount = _options.SlotCount,
            Width = config.Width,
            Height = config.Height,
            Format = config.Format,
            PitchAlignment = _options.PitchAlignment,
        });
        var tenBit = config.Format.Family() == PixelFormatFamily.TenBit;
        var sdrRequested = config.OutputColor.Transfer is TransferFunction.Bt709 or TransferFunction.Srgb or TransferFunction.Linear;
        var request = new OutputRequest(PreferHdr10: tenBit && !sdrRequested, TenBitContent: tenBit);
        var shapeChanged = _ring is null || _ring.Width != config.Width || _ring.Height != config.Height || _ring.Format != config.Format;
        var requestChanged = !_configured || request != _request;
        if (!shapeChanged && !requestChanged)
        {
            return;
        }

        if (shapeChanged && _ring is not null && _retained > _lastReleased)
        {
            // Let the old ring's producer finish: nothing on that ring will be shown any more.
            ReleaseUpTo(_ring, _retained);
        }

        DrainOrThrow();
        if (shapeChanged)
        {
            DestroyRing();
            _ring = PresentationRing.Create(_device, ringConfig);
            CreateStagedImage(_ring);
            _pass.BindSource(_stagedView);
            _pass.BindCopy(_ring, _stagedWords);
            _lastCopied = 0;
            Volatile.Write(ref _lastReleased, 0);
            _retained = 0;
            _maxWaitedWrite = 0;
            _releaseSubmits = 0;
            _firstUnconfirmed = 0;
            Volatile.Write(ref _ringGeneration, _ringGeneration + 1);
        }

        if (requestChanged)
        {
            _target.WaitForPresents(_options.DrainTimeoutNs);
            CompleteAllPending();
            _request = request;
            _target.Configure(request);
        }

        _configured = true;
        RefreshPipeline();
    }

    /// <summary>
    /// Non-blocking: would a <see cref="TryPresent"/> now be accepted? False when the frame slot is still busy, the
    /// present-wait cap is reached, the slot's present fence has not signalled, the target has no pixels (minimised)
    /// or is occluded, or a pending target rebuild still waits for in-flight frames. A request can still be refused
    /// after true when the swapchain has no free image (the compositor still holds them) or turns out to be out of
    /// date; those are only known when an image is acquired, and acquiring one here would hold it for a present the
    /// caller may never make. With present feedback that is rare; without it (<see cref="PresentFeedbackMode.None"/>)
    /// it is the usual refusal, so a producer that writes only after true still writes frames that are refused.
    /// <para>The present-wait answer is shared with the following TryPresent and TryGetFeedback (see RepollTicks).</para>
    /// </summary>
    public bool IsReadyForPresent()
    {
        if (!_configured || _disposed)
        {
            return false;
        }

        var done = FrameDoneValue();
        PollCompletions(done);
        if (_target.NeedsRecreate())
        {
            return !_target.WantsZeroSize && !_target.IsOccluded && CanRecreateTarget(done);
        }

        if (_target.IsUnavailable || _target.IsOccluded)
        {
            return false;
        }

        var frame = _frame + 1;
        var f = (int)(frame % (ulong)_framesInFlight);
        return (_slotFrame[f] == 0 || done >= _slotFrame[f])
            && (!_target.SupportsPresentWait || frame <= (ulong)_framesInFlight || _completedPresent >= frame - (ulong)_framesInFlight)
            && _target.IsFrameSlotReady(f);
    }

    public bool TryPresent(in PresentRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ring = _ring ?? throw new InvalidOperationException("Configure the presenter before presenting.");
        var ready = request.Ready.IsNone ? request.Frame.Ready : request.Ready;
        if (!CheckRequest(ring, request, ready))
        {
            _rejected++;
            return false;
        }

        var n = ready.Value;
        var repeat = n == _lastCopied;
        if (!repeat && n <= _lastReleased)
        {
            _stale++;
            return false;
        }

        // One read of the frame counter for both the completions and the gates below, so they agree.
        var done = FrameDoneValue();
        PollCompletions(done);
        if (_target.NeedsRecreate())
        {
            if (_target.WantsZeroSize || !CanRecreateTarget(done))
            {
                return Drop(ring, n, repeat);
            }

            RecreateTarget();
        }

        if (_target.IsUnavailable || _target.IsOccluded)
        {
            return Drop(ring, n, repeat);
        }

        var frame = _frame + 1;
        var f = (int)(frame % (ulong)_framesInFlight);
        if (_slotFrame[f] != 0 && done < _slotFrame[f])
        {
            return Drop(ring, n, repeat);
        }

        if (_target.SupportsPresentWait && frame > (ulong)_framesInFlight && _completedPresent < frame - (ulong)_framesInFlight)
        {
            return Drop(ring, n, repeat);
        }

        CollectTimestamps(f);
        var acquired = _target.TryAcquire(frame, f, _acquire[f], out var image);
        if (acquired != AcquireOutcome.Acquired)
        {
            return Drop(ring, n, repeat);
        }

        if (!repeat)
        {
            // The copy waits for written ≥ n, and the producer's write n waits for released ≥ n − N: make sure that
            // release is submitted first, or both queues would wait for each other.
            CatchUpReleases(ring, n);
        }

        VulkanException.Check(_api.vkResetCommandPool(_pools[f], VkCommandPoolResetFlags.None), "vkResetCommandPool");
        if (!repeat)
        {
            RecordCopy(ring, f, n);
        }

        RecordRender(ring, f, image, frame, copied: !repeat);
        Submit(ring, f, image, frame, n, copy: !repeat);

        // From here the GPU will signal frameDone = frame (and released = n): record that before anything can throw.
        _frame = frame;
        _slotFrame[f] = frame;
        _slotHadCopy[f] = !repeat && _options.MeasureGpuTime;
        if (repeat)
        {
            _repeats++;
        }
        else
        {
            if (n == _retained)
            {
                _retainedShown++;
            }

            _lastCopied = n;
            NoteReleased(n);
            _maxWaitedWrite = Math.Max(_maxWaitedWrite, n);
        }

        try
        {
            // A present that reports suboptimal or out of date still consumed its semaphores; the target marks itself
            // for recreation and a later request rebuilds it.
            _ = _target.Present(image, f, frame);
        }
        catch
        {
            // That present will never be reported; do not let it hold the present-wait cap.
            _completedPresent = Math.Max(_completedPresent, frame);
            throw;
        }

        AddPending(frame, request.SlotIndex, request.TargetTimeNs);
        _maxInFlight = Math.Max(_maxInFlight, (long)(frame - _completedPresent));
        _presented++;

        // The present just queued cannot have been shown yet: the answer polled before it stays good for this frame.
        if (_pollFrame == frame - 1)
        {
            _pollFrame = frame;
        }

        return true;
    }

    /// <summary>
    /// <see cref="IPresenter.Release"/>: the request's write and every earlier one will not be presented; releases them
    /// (GPU-side, once written). Like TryPresent, a request for a write more than <see cref="RingProtocol.MaxWriteLead"/>
    /// past the last release, one that would leave more than <see cref="RingProtocol.MaxWaitingReleases"/> releases
    /// waiting for unfinished writes, or one from a replaced ring (shape not the current ring's), is ignored and counted
    /// in <see cref="PresenterStats.RejectedRequests"/>.
    /// </summary>
    public void Release(in PresentRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ring = _ring ?? throw new InvalidOperationException("Configure the presenter first.");
        var ready = request.Ready.IsNone ? request.Frame.Ready : request.Ready;
        if (!CheckRequest(ring, request, ready))
        {
            _rejected++;
            return;
        }

        ReleaseUpTo(ring, ready.Value);
    }

    /// <summary>
    /// Releases every write up to <paramref name="write"/> without presenting it (e.g. writes that will never be
    /// requested, or a retained write the pipeline gives up on). GPU-side, after each write completes; no-op for writes
    /// already released. The releases go out in steps of at most N. A write more than
    /// <see cref="RingProtocol.MaxWriteLead"/> past the last release, or one that would leave more than
    /// <see cref="RingProtocol.MaxWaitingReleases"/> releases waiting for unfinished writes, throws
    /// <see cref="ArgumentOutOfRangeException"/> before anything is submitted.
    /// </summary>
    public void ReleaseThrough(ulong write)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ring = _ring ?? throw new InvalidOperationException("Configure the presenter first.");
        if (write > _lastReleased + (ulong)RingProtocol.MaxWriteLead(ring.SlotCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(write),
                write,
                $"More than {RingProtocol.MaxWriteLead(ring.SlotCount)} writes past the last release ({_lastReleased}): more release submissions " +
                $"than one call may make (RingProtocol.MaxLeadInSlots); release in steps of at most that many writes.");
        }

        if (!WithinWaitingBound(ring, write))
        {
            throw new ArgumentOutOfRangeException(
                nameof(write),
                write,
                $"Releasing through write {write} would leave more than {RingProtocol.MaxWaitingReleases} releases waiting for writes the " +
                $"producer has not finished (released {ring.ReadReleasedValue()}, last release submitted {_lastReleased}); " +
                "release again once more of its writes have finished.");
        }

        ReleaseUpTo(ring, write);
    }

    /// <summary>
    /// Non-blocking. Returns queued feedback without asking the driver; only when the queue is empty does it look for
    /// newly completed presents, sharing the present-wait answer of the readiness check and present before it.
    /// </summary>
    public bool TryGetFeedback(out PresentFeedback feedback)
    {
        if (_feedback.TryDequeue(out feedback))
        {
            return true;
        }

        if (_disposed || _pendingCount == 0)
        {
            return false;
        }

        PollCompletions(FrameDoneValue());
        return _feedback.TryDequeue(out feedback);
    }

    /// <summary>Waits (bounded) for all submitted work: frames and releases. False on timeout. Not for the hot path.</summary>
    public bool WaitIdle(long timeoutNs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Drain(timeoutNs);
    }

    /// <summary>
    /// Releases the retained write, waits (bounded) for submitted work and destroys everything. If the producer never
    /// delivers a write the presenter waits on, it host-signals <em>written</em> to unblock its own queue; the producer
    /// must have stopped by then.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_ring is not null && _retained > _lastReleased)
            {
                ReleaseUpTo(_ring, _retained);
            }

            if (!Drain(_options.DrainTimeoutNs) && _ring is not null && _maxWaitedWrite > 0)
            {
                _ring.ForceWritten(_maxWaitedWrite);
                _ = Drain(_options.DrainTimeoutNs);
            }

            _target.WaitForPresents(_options.DrainTimeoutNs);
        }
        finally
        {
            _disposed = true;
            DestroyAll();
        }
    }

    /// <summary>
    /// False for a request to refuse outright: a frame whose shape is not the current ring's (a request from a ring
    /// that Configure replaced; the old ring's handles may even have been reused by the new one), a write more than
    /// <see cref="RingProtocol.MaxWriteLead"/> past the last release (catching up would take more release submissions than
    /// one call may make), or one that would leave more than <see cref="RingProtocol.MaxWaitingReleases"/> releases
    /// waiting for unfinished writes.
    /// Throws for requests that are wrong for the current ring in any other way.
    /// </summary>
    private bool CheckRequest(PresentationRing ring, in PresentRequest request, in GpuSignal ready)
    {
        var frame = request.Frame;
        if (frame.Width != ring.Width || frame.Height != ring.Height || frame.PitchBytes != ring.PitchBytes || frame.Format != ring.Format)
        {
            return false;
        }

        if (ready.Kind != GpuSignalKind.TimelineSemaphore || ready.Handle != ring.WrittenSemaphoreHandle || ready.Value == 0)
        {
            ThrowBadReady();
        }

        if (frame.Domain != MemoryDomain.Vulkan || ring.FindSlot(frame.Address) != ring.SlotOf(ready.Value))
        {
            ThrowBadFrame();
        }

        return ready.Value <= _lastReleased + (ulong)RingProtocol.MaxWriteLead(ring.SlotCount) && WithinWaitingBound(ring, ready.Value);
    }

    /// <summary>
    /// Whether releasing through <paramref name="write"/> (in steps of N, the last one possibly a copy) leaves at most
    /// <see cref="RingProtocol.MaxWaitingReleases"/> submissions signalling <em>released</em> that have not run, counting
    /// the ones already submitted. The ring's counter is read only when the count of those not yet confirmed would pass
    /// the bound: for a far catch-up, a stalled producer, or once per about 500 ordinary frames. Allocation-free.
    /// </summary>
    private bool WithinWaitingBound(PresentationRing ring, ulong write)
    {
        if (write <= _lastReleased)
        {
            return true;
        }

        var slots = (ulong)ring.SlotCount;
        var added = (long)((write - _lastReleased + slots - 1) / slots);
        if (_releaseSubmits - _firstUnconfirmed + added <= RingProtocol.MaxWaitingReleases)
        {
            return true;
        }

        // Released values only grow in submission order: the ones that have run are a prefix.
        var released = ring.ReadReleasedValue();
        while (_firstUnconfirmed < _releaseSubmits && _releaseValues[_firstUnconfirmed % ReleaseHistory] <= released)
        {
            _firstUnconfirmed++;
        }

        return _releaseSubmits - _firstUnconfirmed + added <= RingProtocol.MaxWaitingReleases;
    }

    private static void ThrowBadReady() =>
        throw new ArgumentException(
            "PresentRequest.Ready (or, when it is empty, Frame.Ready) must be the ring's written timeline at write n ≥ 1 (ring.WrittenSignal(n)).");

    private static void ThrowBadFrame() =>
        throw new ArgumentException("PresentRequest.Frame must be the ring slot of write n (ring.ConsumerFrame(n)).");

    /// <summary>
    /// A request that will not be shown now. Its write stays presentable when it is the newest handed to the presenter
    /// (retained, so a retry can show it); everything older is released, so the producer is held up by at most that
    /// one write.
    /// </summary>
    private bool Drop(PresentationRing ring, ulong n, bool repeat)
    {
        if (!repeat)
        {
            if (n >= _retained)
            {
                ReleaseUpTo(ring, n - 1);
                _retained = n;
            }
            else
            {
                // An older write than the retained one: nothing needs it any more.
                ReleaseUpTo(ring, n);
            }
        }

        _dropped++;
        return false;
    }

    /// <summary>
    /// Submits releases until <em>released</em> reaches <paramref name="value"/>, in steps of at most N past the last
    /// release. A release of v waits for written ≥ v and the producer's write v waits for released ≥ v − N, so one
    /// larger step could wait on itself.
    /// </summary>
    private void ReleaseUpTo(PresentationRing ring, ulong value)
    {
        CatchUpReleases(ring, value);
        if (value > _lastReleased)
        {
            SubmitRelease(ring, value);
        }
    }

    /// <summary>Makes sure the release write <paramref name="value"/> waits for (released ≥ value − N) is submitted.</summary>
    private void CatchUpReleases(PresentationRing ring, ulong value)
    {
        var slots = (ulong)ring.SlotCount;
        while (value > _lastReleased + slots)
        {
            SubmitRelease(ring, _lastReleased + slots);
        }
    }

    private void SubmitRelease(PresentationRing ring, ulong n)
    {
        var wait = new VkSemaphoreSubmitInfo { semaphore = ring.Written, value = n, stageMask = VkPipelineStageFlags2.AllCommands };
        var signal = new VkSemaphoreSubmitInfo { semaphore = ring.Released, value = n, stageMask = VkPipelineStageFlags2.AllCommands };
        var submit = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = 1,
            pWaitSemaphoreInfos = &wait,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signal,
        };
        VulkanException.Check(_api.vkQueueSubmit2(_device.Queue, 1, &submit, default), "vkQueueSubmit2");
        _releases++;
        NoteReleased(n);
        _maxWaitedWrite = Math.Max(_maxWaitedWrite, n);
    }

    private void NoteReleased(ulong n)
    {
        _releaseValues[_releaseSubmits++ % ReleaseHistory] = n;
        Volatile.Write(ref _lastReleased, n);
        if (_retained <= n)
        {
            _retained = 0;
        }
    }

    private void RecordCopy(PresentationRing ring, int f, ulong n)
    {
        var cmd = _copyCmd[f];
        var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        VulkanException.Check(_api.vkBeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
        var q = (uint)(f * 4);
        if (_options.MeasureGpuTime)
        {
            _api.vkCmdResetQueryPool(cmd, _queries, q, 2);
            _api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.TopOfPipe, _queries, q);
        }

        var slot = ring.SlotOf(n);
        var buffer = ring.Buffer(slot);
        var barriers = stackalloc VkBufferMemoryBarrier2[1];

        // The staged image's previous contents are discarded; the previous frame's pass may still be sampling it.
        var imageBarrier = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.FragmentShader,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.ShaderStorageWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _staged,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };

        // The producer is another API: take the slot over from the external queue family before reading it.
        barriers[0] = new VkBufferMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.None,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.ShaderStorageRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL,
            dstQueueFamilyIndex = _device.QueueFamily,
            buffer = buffer,
            offset = 0,
            size = VK_WHOLE_SIZE,
        };
        var acquire = new VkDependencyInfo
        {
            bufferMemoryBarrierCount = 1,
            pBufferMemoryBarriers = barriers,
            imageMemoryBarrierCount = 1,
            pImageMemoryBarriers = &imageBarrier,
        };
        _api.vkCmdPipelineBarrier2(cmd, &acquire);

        _api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Compute, _pass.CopyPipeline);
        var set = _pass.CopySet(slot);
        _api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, _pass.CopyLayout, 0, 1, &set, 0, null);
        var push = new CopyPushConstants { PitchWords = (uint)(ring.PitchBytes / 4), Width = (uint)ring.Width, Height = (uint)ring.Height };
        _api.vkCmdPushConstants(cmd, _pass.CopyLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(CopyPushConstants), &push);
        _api.vkCmdDispatch(cmd, (uint)((ring.Width + 15) / 16), (uint)((ring.Height + 15) / 16), 1);

        // And hand it back, so the producer's next write after 'released' sees a consistent owner.
        barriers[0] = new VkBufferMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.ShaderStorageRead,
            dstStageMask = VkPipelineStageFlags2.None,
            dstAccessMask = VkAccessFlags2.None,
            srcQueueFamilyIndex = _device.QueueFamily,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL,
            buffer = buffer,
            offset = 0,
            size = VK_WHOLE_SIZE,
        };
        var release = new VkDependencyInfo { bufferMemoryBarrierCount = 1, pBufferMemoryBarriers = barriers };
        _api.vkCmdPipelineBarrier2(cmd, &release);

        if (_options.MeasureGpuTime)
        {
            _api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.AllCommands, _queries, q + 1);
        }

        VulkanException.Check(_api.vkEndCommandBuffer(cmd), "vkEndCommandBuffer");
    }

    private void RecordRender(PresentationRing ring, int f, int image, ulong frame, bool copied)
    {
        var cmd = _renderCmd[f];
        var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        VulkanException.Check(_api.vkBeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
        var q = (uint)((f * 4) + 2);
        if (_options.MeasureGpuTime)
        {
            _api.vkCmdResetQueryPool(cmd, _queries, q, 2);
            _api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.TopOfPipe, _queries, q);
        }

        var imageBarriers = stackalloc VkImageMemoryBarrier2[2];
        var count = 0u;
        if (copied)
        {
            imageBarriers[count++] = new VkImageMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.ComputeShader,
                srcAccessMask = VkAccessFlags2.ShaderStorageWrite,
                dstStageMask = VkPipelineStageFlags2.FragmentShader,
                dstAccessMask = VkAccessFlags2.ShaderSampledRead,
                oldLayout = VkImageLayout.General,
                newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                image = _staged,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
        }

        // The target image's previous contents are discarded. The source stages cover both the acquire semaphore
        // wait (swapchain) and the previous frame's readback copy (offscreen) on this image.
        imageBarriers[count++] = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ColorAttachmentOutput | VkPipelineStageFlags2.Copy,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.ColorAttachmentOptimal,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _target.Images[image],
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        var dep = new VkDependencyInfo { imageMemoryBarrierCount = count, pImageMemoryBarriers = imageBarriers };
        _api.vkCmdPipelineBarrier2(cmd, &dep);

        var width = _target.Width;
        var height = _target.Height;
        var attachment = new VkRenderingAttachmentInfo
        {
            imageView = _target.Views[image],
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
        };
        attachment.clearValue.color = new VkClearColorValue(0f, 0f, 0f, 1f);
        var rendering = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, (uint)width, (uint)height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &attachment,
        };
        _api.vkCmdBeginRendering(cmd, &rendering);

        var rect = ContentRect.Fit(ring.Width, ring.Height, width, height);
        var viewport = new VkViewport(rect.X, rect.Y, rect.Width, rect.Height, 0f, 1f);
        var scissor = new VkRect2D(rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height);
        _api.vkCmdSetViewport(cmd, 0, 1, &viewport);
        _api.vkCmdSetScissor(cmd, 0, 1, &scissor);
        _api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _pipeline);
        var set = _pass.DescriptorSet;
        _api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pass.Layout, 0, 1, &set, 0, null);
        var push = new PresentPushConstants
        {
            RectX = rect.X,
            RectY = rect.Y,
            RectWidth = rect.Width,
            RectHeight = rect.Height,
            Identity = rect.Width == ring.Width && rect.Height == ring.Height ? 1 : 0,
        };
        _api.vkCmdPushConstants(cmd, _pass.Layout, VkShaderStageFlags.Fragment, 0, (uint)sizeof(PresentPushConstants), &push);
        _api.vkCmdDraw(cmd, 3, 1, 0, 0);
        _api.vkCmdEndRendering(cmd);

        if (_options.MeasureGpuTime)
        {
            _api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.AllCommands, _queries, q + 1);
        }

        _target.RecordEpilogue(cmd, image, frame);
        VulkanException.Check(_api.vkEndCommandBuffer(cmd), "vkEndCommandBuffer");
    }

    private void Submit(PresentationRing ring, int f, int image, ulong frame, ulong n, bool copy)
    {
        var batches = stackalloc VkSubmitInfo2[2];
        var batchCount = 0u;

        var copyWaits = stackalloc VkSemaphoreSubmitInfo[2];
        var copyWaitCount = 0u;
        copyWaits[copyWaitCount++] = new VkSemaphoreSubmitInfo { semaphore = ring.Written, value = n, stageMask = VkPipelineStageFlags2.ComputeShader };
        if (!CopyGate.IsNull)
        {
            copyWaits[copyWaitCount++] = new VkSemaphoreSubmitInfo { semaphore = CopyGate, value = CopyGateValue, stageMask = VkPipelineStageFlags2.ComputeShader };
        }

        var signalReleased = new VkSemaphoreSubmitInfo { semaphore = ring.Released, value = n, stageMask = VkPipelineStageFlags2.AllCommands };
        var copyCmd = new VkCommandBufferSubmitInfo { commandBuffer = _copyCmd[f] };
        if (copy)
        {
            // Invariant: 'released' = n is signalled by the batch that holds the copy, after all of its commands
            // (ALL_COMMANDS), never earlier; the producer overwrites the slot as soon as it sees it. A batch of its own,
            // so the release is not held back by the pass.
            batches[batchCount++] = new VkSubmitInfo2
            {
                waitSemaphoreInfoCount = copyWaitCount,
                pWaitSemaphoreInfos = copyWaits,
                commandBufferInfoCount = 1,
                pCommandBufferInfos = &copyCmd,
                signalSemaphoreInfoCount = 1,
                pSignalSemaphoreInfos = &signalReleased,
            };
        }

        var waitAcquire = new VkSemaphoreSubmitInfo { semaphore = _acquire[f], stageMask = VkPipelineStageFlags2.ColorAttachmentOutput };
        var signals = stackalloc VkSemaphoreSubmitInfo[2];
        var signalCount = 0u;
        signals[signalCount++] = new VkSemaphoreSubmitInfo { semaphore = _frameDone, value = frame, stageMask = VkPipelineStageFlags2.AllCommands };
        var renderDone = _target.RenderDoneSemaphore(image);
        if (!renderDone.IsNull)
        {
            signals[signalCount++] = new VkSemaphoreSubmitInfo { semaphore = renderDone, stageMask = VkPipelineStageFlags2.AllCommands };
        }

        var renderCmd = new VkCommandBufferSubmitInfo { commandBuffer = _renderCmd[f] };
        var acquire = _target.UsesAcquireSemaphore;
        batches[batchCount++] = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = acquire ? 1u : 0u,
            pWaitSemaphoreInfos = acquire ? &waitAcquire : null,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &renderCmd,
            signalSemaphoreInfoCount = signalCount,
            pSignalSemaphoreInfos = signals,
        };
        VulkanException.Check(_api.vkQueueSubmit2(_device.Queue, batchCount, batches, default), "vkQueueSubmit2");
    }

    private void CollectTimestamps(int f)
    {
        if (!_options.MeasureGpuTime || _slotFrame[f] == 0)
        {
            return;
        }

        var data = stackalloc ulong[2];
        if (_api.vkGetQueryPoolResults(_queries, (uint)((f * 4) + 2), 2, 2 * sizeof(ulong), data, sizeof(ulong), VkQueryResultFlags.Bit64) == VkResult.Success)
        {
            _lastPassNs = (long)(((data[1] - data[0]) & _timestampMask) * _timestampPeriodNs);
        }

        if (_slotHadCopy[f]
            && _api.vkGetQueryPoolResults(_queries, (uint)(f * 4), 2, 2 * sizeof(ulong), data, sizeof(ulong), VkQueryResultFlags.Bit64) == VkResult.Success)
        {
            _lastCopyNs = (long)(((data[1] - data[0]) & _timestampMask) * _timestampPeriodNs);
        }
    }

    private void PollCompletions(ulong done)
    {
        if (_pendingCount == 0)
        {
            return;
        }

        if (!_target.SupportsPresentWait)
        {
            while (_pendingCount > 0 && done >= _pendingId[_pendingHead])
            {
                CompleteOldestPending();
            }

            return;
        }

        // One driver query, and none while the last answer is still good: nothing presented since and recent. A stale
        // answer can only say "not shown yet" (completion is monotonic), so it may refuse a request, never break the cap.
        var now = Stopwatch.GetTimestamp();
        if (_pollFrame == _frame && now - _pollStamp < RepollTicks)
        {
            return;
        }

        // Ask about the present the next request's cap depends on (completion of id implies every earlier one).
        var oldest = _pendingId[_pendingHead];
        var newest = _pendingId[(_pendingHead + _pendingCount - 1) % PendingCapacity];
        var gate = _frame + 1 > (ulong)_framesInFlight ? _frame + 1 - (ulong)_framesInFlight : oldest;
        var id = Math.Clamp(gate, oldest, newest);
        _pollFrame = _frame;
        _pollStamp = now;
        _presentWaitPolls++;
        if (_target.PollPresentComplete(id))
        {
            while (_pendingCount > 0 && _pendingId[_pendingHead] <= id)
            {
                CompleteOldestPending();
            }
        }
    }

    private void AddPending(ulong id, long slotIndex, long targetNs)
    {
        if (_pendingCount == PendingCapacity)
        {
            CompleteOldestPending();
        }

        var i = (_pendingHead + _pendingCount) % PendingCapacity;
        _pendingId[i] = id;
        _pendingSlot[i] = slotIndex;
        _pendingTarget[i] = targetNs;
        _pendingCount++;
    }

    private void CompleteOldestPending()
    {
        var feedback = new PresentFeedback
        {
            SlotIndex = _pendingSlot[_pendingHead],
            TargetTimeNs = _pendingTarget[_pendingHead],
            ActualTimeNs = 0, // first-pixel time needs VK_EXT_present_timing (Phase 6)
        };
        _completedPresent = Math.Max(_completedPresent, _pendingId[_pendingHead]);
        _target.OnPresentCompleted();
        if (!_feedback.TryEnqueue(feedback))
        {
            _feedbackOverflows++;
        }

        _pendingHead = (_pendingHead + 1) % PendingCapacity;
        _pendingCount--;
    }

    private void CompleteAllPending()
    {
        while (_pendingCount > 0)
        {
            CompleteOldestPending();
        }
    }

    private ulong FrameDoneValue()
    {
        ulong value;
        VulkanException.Check(_api.vkGetSemaphoreCounterValue(_frameDone, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    private bool Drain(long timeoutNs)
    {
        var semaphores = stackalloc VkSemaphore[2];
        var values = stackalloc ulong[2];
        var count = 0u;
        if (_frame > 0)
        {
            semaphores[count] = _frameDone;
            values[count++] = _frame;
        }

        if (_ring is not null && _lastReleased > 0)
        {
            semaphores[count] = _ring.Released;
            values[count++] = _lastReleased;
        }

        if (count == 0)
        {
            return true;
        }

        var info = new VkSemaphoreWaitInfo { semaphoreCount = count, pSemaphores = semaphores, pValues = values };
        var result = _api.vkWaitSemaphores(&info, (ulong)Math.Max(0, timeoutNs));
        VulkanException.Check(result, "vkWaitSemaphores");
        return result == VkResult.Success;
    }

    private void DrainOrThrow()
    {
        if (!Drain(_options.DrainTimeoutNs))
        {
            throw new TimeoutException("In-flight presents did not finish; is the producer still signalling 'written'?");
        }
    }

    /// <summary>
    /// Can the target be rebuilt now without waiting? Every frame must have finished on the GPU and every present
    /// released its semaphores; a target whose fallback needs an idle queue also needs every release batch finished.
    /// While not, requests are refused (and the newest retained) instead of blocking the present thread.
    /// </summary>
    private bool CanRecreateTarget(ulong done)
    {
        if (done < _frame || !_target.PresentsComplete())
        {
            return false;
        }

        return !_target.NeedsIdleQueueForPresents || _ring is null || _ring.ReadReleasedValue() >= _lastReleased;
    }

    private void RecreateTarget()
    {
        // Nothing to wait for any more (CanRecreateTarget); this only collects the signalled fences.
        _target.WaitForPresents(_options.DrainTimeoutNs);
        CompleteAllPending();
        _target.Recreate();
        _recreations++;
        RefreshPipeline();
    }

    private void RefreshPipeline()
    {
        if (_ring is null || _target.ImageCount == 0)
        {
            return;
        }

        ActiveTransform = PresentPass.SelectTransform(_ring.Format, _target.ColorSpace);
        _pipeline = _pass.Pipeline(_target.Format, ActiveTransform, _options.Scaler);
    }

    private void CreateFrameResources()
    {
        var timelineType = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var timelineInfo = new VkSemaphoreCreateInfo { pNext = &timelineType };
        VkSemaphore frameDone;
        VulkanException.Check(_api.vkCreateSemaphore(&timelineInfo, &frameDone), "vkCreateSemaphore");
        _frameDone = frameDone;

        if (_options.MeasureGpuTime)
        {
            var queryInfo = new VkQueryPoolCreateInfo { queryType = VkQueryType.Timestamp, queryCount = (uint)(_framesInFlight * 4) };
            VkQueryPool pool;
            VulkanException.Check(_api.vkCreateQueryPool(&queryInfo, &pool), "vkCreateQueryPool");
            _queries = pool;
        }

        var buffers = stackalloc VkCommandBuffer[2];
        for (var i = 0; i < _framesInFlight; i++)
        {
            var poolInfo = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.Transient, queueFamilyIndex = _device.QueueFamily };
            VkCommandPool commandPool;
            VulkanException.Check(_api.vkCreateCommandPool(&poolInfo, &commandPool), "vkCreateCommandPool");
            _pools[i] = commandPool;
            var allocInfo = new VkCommandBufferAllocateInfo { commandPool = commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 2 };
            VulkanException.Check(_api.vkAllocateCommandBuffers(&allocInfo, buffers), "vkAllocateCommandBuffers");
            _copyCmd[i] = buffers[0];
            _renderCmd[i] = buffers[1];

            var binaryInfo = new VkSemaphoreCreateInfo();
            VkSemaphore acquire;
            VulkanException.Check(_api.vkCreateSemaphore(&binaryInfo, &acquire), "vkCreateSemaphore");
            _acquire[i] = acquire;
        }
    }

    private void CreateStagedImage(PresentationRing ring)
    {
        // Sampled in the ring's format, written by the copy through an R32_UINT view (same 32-bit size class).
        var formats = stackalloc VkFormat[2] { ring.VkFormat, VkFormat.R32Uint };
        var formatList = new VkImageFormatListCreateInfo { viewFormatCount = 2, pViewFormats = formats };
        var info = new VkImageCreateInfo
        {
            pNext = &formatList,
            flags = VkImageCreateFlags.MutableFormat | VkImageCreateFlags.ExtendedUsage,
            imageType = VkImageType.Image2D,
            format = ring.VkFormat,
            extent = new VkExtent3D((uint)ring.Width, (uint)ring.Height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkImage image;
        VulkanException.Check(_api.vkCreateImage(&info, &image), "vkCreateImage");
        _staged = image;
        VkMemoryRequirements reqs;
        _api.vkGetImageMemoryRequirements(image, &reqs);
        var alloc = new VkMemoryAllocateInfo
        {
            allocationSize = reqs.size,
            memoryTypeIndex = _device.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal, VkMemoryPropertyFlags.HostVisible),
        };
        VkDeviceMemory memory;
        VulkanException.Check(_api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
        _stagedMemory = memory;
        VulkanException.Check(_api.vkBindImageMemory(image, memory, 0), "vkBindImageMemory");
        var viewInfo = new VkImageViewCreateInfo
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = ring.VkFormat,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        var sampledUsage = new VkImageViewUsageCreateInfo { usage = VkImageUsageFlags.Sampled };
        viewInfo.pNext = &sampledUsage;
        VkImageView view;
        VulkanException.Check(_api.vkCreateImageView(&viewInfo, &view), "vkCreateImageView");
        _stagedView = view;
        var storageUsage = new VkImageViewUsageCreateInfo { usage = VkImageUsageFlags.Storage };
        viewInfo.pNext = &storageUsage;
        viewInfo.format = VkFormat.R32Uint;
        VulkanException.Check(_api.vkCreateImageView(&viewInfo, &view), "vkCreateImageView");
        _stagedWords = view;
    }

    private void DestroyRing()
    {
        if (!_stagedView.IsNull)
        {
            _api.vkDestroyImageView(_stagedView);
            _stagedView = default;
        }

        if (!_stagedWords.IsNull)
        {
            _api.vkDestroyImageView(_stagedWords);
            _stagedWords = default;
        }

        if (!_staged.IsNull)
        {
            _api.vkDestroyImage(_staged);
            _staged = default;
        }

        if (!_stagedMemory.IsNull)
        {
            _api.vkFreeMemory(_stagedMemory);
            _stagedMemory = default;
        }

        _ring?.Dispose();
        _ring = null;
    }

    private void DestroyAll()
    {
        DestroyRing();
        for (var i = 0; i < _framesInFlight; i++)
        {
            if (!_pools[i].IsNull)
            {
                _api.vkDestroyCommandPool(_pools[i]);
            }

            if (!_acquire[i].IsNull)
            {
                _api.vkDestroySemaphore(_acquire[i]);
            }
        }

        if (!_queries.IsNull)
        {
            _api.vkDestroyQueryPool(_queries);
            _queries = default;
        }

        if (!_frameDone.IsNull)
        {
            _api.vkDestroySemaphore(_frameDone);
            _frameDone = default;
        }

        _pass.Dispose();
    }
}
