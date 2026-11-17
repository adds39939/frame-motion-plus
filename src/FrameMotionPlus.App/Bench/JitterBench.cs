using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Settings;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.App.Bench;

/// <summary>
/// The design doc's Phase 0 runtime-jitter measurement. A dedicated pacing thread waits for each output slot's
/// decision time with the platform waiter and then does the real per-slot CPU work: it drains synthetic frame arrivals
/// that a second dedicated source thread pushes through the real SPSC ring, runs the real <see cref="FrameScheduler"/>
/// against the real clock, and writes a telemetry record into the real telemetry ring (drained by the main thread, as
/// the telemetry writer will be). In alternating blocks the same thread runs the same wait loop with all of that work
/// removed (timer only), so OS wake-up jitter can be told apart from jitter our managed work adds, under the same
/// machine conditions. Nothing in either loop allocates; samples go to preallocated arrays.
/// </summary>
internal sealed class JitterBench
{
    private const long StartDelayNs = 300 * Nanoseconds.PerMillisecond;
    private const int FrameRingCapacity = 64;
    private const int TelemetryRingCapacity = 4096;
    private const int ClockCostReads = 200_000;

    private static long _clockSink;

    private readonly JitterBenchOptions _options;
    private readonly DeadlineWaiter _waiter;
    private readonly DeadlineWaiter _sourceWaiter = DeadlineWaiter.Sleep;
    private readonly SpscRing<FrameArrival> _frames = new(FrameRingCapacity);
    private readonly TelemetryRing _telemetry = new(TelemetryRingCapacity);
    private readonly JitterSamples? _baseline;
    private readonly JitterSamples _full;
    private readonly long _timerLeadNs;
    private BenchBlock[] _blocks = [];
    private FrameScheduler? _scheduler;
    private long _originNs;
    private long _endNs;
    private long _sourceFrames;
    private long _sourceDropped;
    private long _sourceAllocated = -1;
    private long _telemetryRead;
    private bool _windowOpen;
    private long _windowOpenedAtNs;
    private long _windowClosedAtNs;
    private long _allocatedAtOpen;
    private long _pacingAllocated;
    private GcSnapshot _gcAtOpen;
    private GcSnapshot _windowGc;
    private bool _faultPending;
    private byte[]? _faultSink;

    public JitterBench(JitterBenchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.SlotsPerLoopBound, JitterBenchOptions.MaxSlotsPerLoop, nameof(options));
        _options = options;
        _waiter = options.CreateWaiter();
        var capacity = (int)options.SlotsPerLoopBound;
        _baseline = options.Baseline ? new JitterSamples("timer only", capacity) : null;
        _full = new JitterSamples("full loop", capacity);
        _faultPending = options.Faults.Any;

        // The timer-only loop wakes at the same phase before each vblank as the scheduler starts with.
        _timerLeadNs = options.GenerationTimeNs + Nanoseconds.FromMilliseconds(new FmpSettings().Scheduler.MarginMs);
    }

    /// <summary>
    /// Alternating full-loop and timer-only blocks until each kind has <c>DurationNs</c> measured. The first block of
    /// each kind also carries the warm-up. The full loop goes first, so the scheduler is warm before the run's single
    /// GC and allocation window opens. Without a baseline, one full-loop block.
    /// </summary>
    internal static BenchBlock[] PlanBlocks(long startNs, JitterBenchOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!o.Baseline)
        {
            return [new BenchBlock(true, startNs, startNs + o.WarmupNs, startNs + o.WarmupNs + o.DurationNs)];
        }

        var blocks = new List<BenchBlock>();
        var chunkNs = Math.Max(Nanoseconds.PerMillisecond, o.BlockNs - JitterBenchOptions.BlockSettleNs);
        long measuredTimer = 0, measuredFull = 0;
        var t = startNs;
        var full = true;
        while (measuredTimer < o.DurationNs || measuredFull < o.DurationNs)
        {
            var measured = full ? measuredFull : measuredTimer;
            if (measured < o.DurationNs)
            {
                var settle = measured == 0 ? Math.Max(o.WarmupNs, JitterBenchOptions.BlockSettleNs) : JitterBenchOptions.BlockSettleNs;
                var chunk = Math.Min(chunkNs, o.DurationNs - measured);
                blocks.Add(new BenchBlock(full, t, t + settle, t + settle + chunk));
                t += settle + chunk;
                if (full)
                {
                    measuredFull += chunk;
                }
                else
                {
                    measuredTimer += chunk;
                }
            }

            full = !full;
        }

        return [.. blocks];
    }

    public JitterBenchResult Run()
    {
        var clockCost = MeasureClockReadNs();
        var settings = new FmpSettings();
        settings.Scheduler.InitialGenerationTimeMs = Nanoseconds.ToMilliseconds(_options.GenerationTimeNs);

        var gcConfiguration = GcConfiguration.Describe();
        var loadBefore = MachineInfo.LoadAverage();
        var runGcBefore = GcSnapshot.Take();
        var gcScope = GcLatencyScope.Enter();
        DedicatedThread source;
        DedicatedThread pacing;
        GcSnapshot runGc;
        try
        {
            StartThreads(settings, out source, out pacing);

            // The main thread plays the telemetry writer: it drains the ring the pacing thread fills, without allocating.
            while (pacing.IsAlive)
            {
                Thread.Sleep(10);
                DrainTelemetry();
            }

            pacing.Join();
            source.Join();
            DrainTelemetry();
            runGc = GcSnapshot.Take().Since(runGcBefore);
        }
        finally
        {
            gcScope.Dispose();
        }

        var gcModeAfter = System.Runtime.GCSettings.LatencyMode;
        pacing.ThrowIfFailed();
        source.ThrowIfFailed();

        var periodNs = _options.Rate.PeriodNs;
        return new JitterBenchResult
        {
            Options = _options,
            Blocks = _blocks,
            Baseline = _baseline,
            Full = _full,
            BaselineStats = _baseline is null ? null : PhaseStats.From(_baseline, _options.GateNs, periodNs),
            FullStats = PhaseStats.From(_full, _options.GateNs, periodNs),
            PacingThread = pacing.Report,
            SourceThread = source.Report,
            GcConfiguration = gcConfiguration,
            GcModeRequested = gcScope.Requested,
            GcModeObtained = gcScope.Obtained,
            GcModeAfter = gcModeAfter,
            RunGc = runGc,
            MeasuredGc = _windowGc,
            MeasuredWindowNs = _windowClosedAtNs - _windowOpenedAtNs,
            PacingAllocatedBytes = _pacingAllocated,
            SourceFrames = _sourceFrames,
            SourceDropped = _sourceDropped,
            SourceAllocatedBytes = _sourceAllocated,
            TelemetryRecords = _telemetryRead,
            TelemetryDropped = _telemetry.Dropped,
            Counters = _scheduler!.Counters,
            Diagnostics = _scheduler.Diagnostics,
            ClockReadNs = clockCost,
            LoadBefore = loadBefore,
            LoadAfter = MachineInfo.LoadAverage(),
        };
    }

    private void StartThreads(FmpSettings settings, out DedicatedThread source, out DedicatedThread pacing)
    {
        _originNs = Monotonic.NowNs + StartDelayNs;
        _blocks = PlanBlocks(_originNs, _options);
        _endNs = _blocks[^1].StopNs;
        _scheduler = new FrameScheduler(
            settings.Scheduler.ToOptions(settings.Display) with { DisplayRate = _options.Rate, VblankAnchorNs = _originNs });

        source = DedicatedThread.Start("fmp-source", _options.SourceRequest, RunSource);
        pacing = DedicatedThread.Start("fmp-pacing", _options.PacingRequest, RunPacing);
    }

    private void DrainTelemetry()
    {
        while (_telemetry.TryRead(out _))
        {
            _telemetryRead++;
        }
    }

    private void RunPacing()
    {
        var blocks = _blocks;
        for (var b = 0; b < blocks.Length; b++)
        {
            if (blocks[b].Full)
            {
                RunFull(_full, blocks[b], b);
            }
            else
            {
                RunTimerOnly(_baseline!, blocks[b], b);
            }
        }

        if (_windowOpen)
        {
            _pacingAllocated = GcSnapshot.CurrentThreadAllocatedBytes - _allocatedAtOpen;
            _windowGc = GcSnapshot.Take().Since(_gcAtOpen);
            _windowClosedAtNs = Monotonic.NowNs;
        }
    }

    /// <summary>Pacing thread: opens the one GC and allocation window at the first measured slot of the run. It then
    /// spans every later block of both kinds (and the settling between them), to the end of the run.</summary>
    private void OpenWindowOnce()
    {
        if (_windowOpen)
        {
            return;
        }

        _windowOpen = true;
        _windowOpenedAtNs = Monotonic.NowNs;
        _gcAtOpen = GcSnapshot.Take();
        _allocatedAtOpen = GcSnapshot.CurrentThreadAllocatedBytes;
    }

    /// <summary>The full loop minus the scheduler and telemetry: wait, drain the ring (discarding), record.</summary>
    private void RunTimerOnly(JitterSamples samples, BenchBlock block, int blockIndex)
    {
        var rate = _options.Rate;
        var origin = _originNs;
        var lead = _timerLeadNs;
        var waiter = _waiter;
        var frames = _frames;

        // First grid slot whose deadline lies in this block.
        waiter.WaitUntil(block.StartNs);
        var index = rate.TickIndexAtOrBefore(block.StartNs + lead - origin) + 1;
        while (true)
        {
            var deadline = origin + rate.TickTimeNs(index) - lead;
            if (deadline >= block.StopNs)
            {
                break;
            }

            var measured = deadline >= block.MeasuredFromNs;
            if (measured)
            {
                OpenWindowOnce();
            }

            var wake = waiter.WaitUntil(deadline);
            while (frames.TryDequeue(out _))
            {
            }

            // Like the scheduler: a slot whose display time has already passed is skipped, not shown late.
            var now = Monotonic.NowNs;
            long skipped = 0;
            while (origin + rate.TickTimeNs(index) <= now)
            {
                index++;
                skipped++;
            }

            var end = Monotonic.NowNs;
            samples.Add(deadline, wake, end, origin + rate.TickTimeNs(index), 0, 0, skipped, measured, blockIndex);
            index++;
        }
    }

    /// <summary>The realistic per-slot work: arrivals into the scheduler, a decision, a telemetry record.</summary>
    private void RunFull(JitterSamples samples, BenchBlock block, int blockIndex)
    {
        var scheduler = _scheduler!;
        var waiter = _waiter;
        var frames = _frames;
        var telemetry = _telemetry;
        var d = _options.GenerationTimeNs;

        // Frames queued during a timer-only block are stale; the scheduler resumes on fresh arrivals, as after a hold.
        // Its first decision catches up the vblanks of the timer-only block; that falls in the unmeasured settling time.
        waiter.WaitUntil(block.StartNs);
        while (frames.TryDequeue(out _))
        {
        }

        var now = Monotonic.NowNs;
        while (true)
        {
            var deadline = scheduler.NextDecisionTime(now);
            if (deadline >= block.StopNs)
            {
                break;
            }

            var measured = deadline >= block.MeasuredFromNs;
            if (measured)
            {
                OpenWindowOnce();
            }

            var wake = waiter.WaitUntil(deadline);
            while (frames.TryDequeue(out var frame))
            {
                scheduler.OnFrameArrived(frame);
            }

            now = Monotonic.NowNs;
            var decision = scheduler.Schedule(now);
            if (decision.Kind == SlotKind.Generated)
            {
                // Stands in for the CUDA event timing of the generation this decision enqueues.
                scheduler.ReportGenerationTime(d);
            }

            telemetry.TryWrite(new FrameTimelineRecord
            {
                SlotIndex = decision.SlotIndex,
                Kind = decision.Kind,
                Reason = decision.Reason,
                FrameA = decision.FrameA,
                FrameB = decision.FrameB,
                Timestep = decision.Timestep,
                TargetDisplayNs = decision.TargetTimeNs,
                IdealContentNs = decision.IdealContentTimeNs,
                ContentNs = decision.ContentTimeNs,
                PipelineDelayNs = decision.PipelineDelayNs,
                DecisionNs = now,
            });

            var end = Monotonic.NowNs;
            samples.Add(deadline, wake, end, decision.TargetTimeNs, (byte)decision.Kind, (byte)decision.Reason, decision.SkippedSlots, measured, blockIndex);
            if (_faultPending && measured)
            {
                InjectFaults();
            }

            now = Monotonic.NowNs;
        }
    }

    /// <summary>Test hook: a stall (the thread not running), an allocation or a collection on the pacing thread.</summary>
    private void InjectFaults()
    {
        _faultPending = false;
        var faults = _options.Faults;
        if (faults.StallNs > 0)
        {
            Thread.Sleep(TimeSpan.FromTicks(faults.StallNs / 100));
        }

        if (faults.AllocateBytes > 0)
        {
            _faultSink = new byte[faults.AllocateBytes];
        }

        if (faults.Collect)
        {
            GC.Collect();
        }

        GC.KeepAlive(_faultSink);
    }

    /// <summary>Synthetic capture: unique frames on an exact grid, available when the thread wakes for them.</summary>
    private void RunSource()
    {
        var rate = _options.SourceRate;
        var origin = _originNs;
        var stop = _endNs;
        var waiter = _sourceWaiter;
        var frames = _frames;
        long allocatedAtStart = 0;
        long k = 0;
        while (true)
        {
            var content = origin + rate.TickTimeNs(k);
            if (content >= stop)
            {
                break;
            }

            var available = waiter.WaitUntil(content);
            if (!frames.TryEnqueue(new FrameArrival(k, content, available)))
            {
                _sourceDropped++;
            }

            if (k == 0)
            {
                allocatedAtStart = GcSnapshot.CurrentThreadAllocatedBytes;
            }

            k++;
        }

        _sourceAllocated = GcSnapshot.CurrentThreadAllocatedBytes - allocatedAtStart;
        _sourceFrames = k;
    }

    private static long MeasureClockReadNs()
    {
        long sink = 0;
        for (var i = 0; i < 1000; i++)
        {
            sink ^= Monotonic.NowNs;
        }

        var begin = Monotonic.NowNs;
        for (var i = 0; i < ClockCostReads; i++)
        {
            sink ^= Monotonic.NowNs;
        }

        var elapsed = Monotonic.NowNs - begin;
        Volatile.Write(ref _clockSink, sink);
        return elapsed / ClockCostReads;
    }
}
