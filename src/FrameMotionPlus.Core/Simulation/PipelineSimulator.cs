using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Simulation;

public sealed record SimulationOptions
{
    public long DurationNs { get; init; } = 60 * Nanoseconds.PerSecond;

    /// <summary>Slots before this target time are excluded from the statistics (start-up and settling).</summary>
    public long StatsStartNs { get; init; } = Nanoseconds.PerSecond;

    public SimulatedSourceOptions Source { get; init; } = new();

    /// <summary>Simulated generation time d per generated frame.</summary>
    public long GenerationTimeNs { get; init; } = 2 * Nanoseconds.PerMillisecond;

    /// <summary>Uniform ± jitter on d.</summary>
    public long GenerationJitterNs { get; init; }

    /// <summary>From this time on, d becomes <see cref="GenerationTimeAfterChangeNs"/> (models a quality or load change).</summary>
    public long GenerationTimeChangeAtNs { get; init; } = long.MaxValue;

    public long GenerationTimeAfterChangeNs { get; init; }

    /// <summary>Until this time, d is <see cref="GenerationTimeSpikeNs"/> instead (a spike that later passes).</summary>
    public long GenerationTimeSpikeUntilNs { get; init; } = long.MinValue;

    public long GenerationTimeSpikeFromNs { get; init; }

    public long GenerationTimeSpikeNs { get; init; }

    /// <summary>GPU time to copy a real frame into the presentation ring.</summary>
    public long CopyTimeNs { get; init; }

    /// <summary>Constant lateness of the scheduling thread's wake-ups versus the requested decision time.</summary>
    public long WakeLatenessNs { get; init; }

    /// <summary>Extra uniform [0, J] lateness on every wake-up.</summary>
    public long WakeJitterNs { get; init; }

    /// <summary>The scheduling thread is blocked from this time for <see cref="StallDurationNs"/>: decisions and
    /// frame deliveries due in that window happen at its end, in a burst.</summary>
    public long StallAtNs { get; init; } = long.MaxValue;

    public long StallDurationNs { get; init; }

    /// <summary>At the first decision at or after this time, present feedback re-anchors the vblank grid by
    /// <see cref="ReanchorShiftNs"/> relative to that decision's target; the simulated display follows the new grid.</summary>
    public long ReanchorAtNs { get; init; } = long.MaxValue;

    public long ReanchorShiftNs { get; init; }

    /// <summary>Frames are handed to the scheduler this long before they are available (decode-ahead). The scheduler
    /// must not use them before their arrival time; the simulator checks that independently.</summary>
    public long DeliverAheadNs { get; init; }

    public ulong Seed { get; init; } = 7;
}

public sealed record SimulationResult
{
    public long Slots { get; init; }

    public long Real { get; init; }

    public long Generated { get; init; }

    public long Repeated { get; init; }

    public long Empty { get; init; }

    /// <summary>Slots whose image was not ready by the target time (GPU late).</summary>
    public long Missed { get; init; }

    public long StarvedSlots { get; init; }

    /// <summary>Fixed refresh: slots that showed a real frame because generation could no longer finish in time.</summary>
    public long LateSlots { get; init; }

    public long HoldSlots { get; init; }

    public long FallbackSlots { get; init; }

    public long DiscontinuitySlots { get; init; }

    /// <summary>Fixed refresh: vblanks passed without a decision. VRR: generated slots skipped as too late.</summary>
    public long SkippedSlots { get; init; }

    /// <summary>Decisions that used a frame before its arrival time (must be 0).</summary>
    public long CausalityViolations { get; init; }

    /// <summary>Consecutive shown images whose content time went backwards (must be 0).</summary>
    public long ContentRegressions { get; init; }

    /// <summary>Decisions whose content time does not follow from their frames' timestamps and t, or whose t is not
    /// strictly inside (0, 1) as a float (must be 0).</summary>
    public long TimestepInconsistencies { get; init; }

    /// <summary>Distinct generated timesteps (rounded to 1e-6), up to 32 of them.</summary>
    public IReadOnlyList<double> DistinctTimesteps { get; init; } = [];

    /// <summary>More than 32 distinct timesteps occurred.</summary>
    public bool DistinctTimestepsOverflow { get; init; }

    public double MinTimestep { get; init; }

    public double MaxTimestep { get; init; }

    public long RealLatencySamples { get; init; }

    /// <summary>Added latency of real frames: display time − content time, including frames shown late.</summary>
    public double RealLatencyMeanNs { get; init; }

    public long RealLatencyP99Ns { get; init; }

    public long RealLatencyMinNs { get; init; }

    public long RealLatencyMaxNs { get; init; }

    /// <summary>Display time − content time over every shown image (real and generated), measured by the simulator.</summary>
    public double ShownLatencyMeanNs { get; init; }

    public long ShownLatencyMinNs { get; init; }

    public long ShownLatencyMaxNs { get; init; }

    public double PipelineDelayMeanNs { get; init; }

    public long PipelineDelayMinNs { get; init; }

    public long PipelineDelayMaxNs { get; init; }

    /// <summary>Content-time step between consecutive newly shown images (fixed refresh: ideally one refresh).</summary>
    public long ContentStepMinNs { get; init; }

    public long ContentStepMaxNs { get; init; }

    public double ContentStepStdDevNs { get; init; }

    public double MeanGenerationTimeNs { get; init; }

    public long GeneratorEnqueues { get; init; }

    public long Presents { get; init; }

    public long SourceFramesDelivered { get; init; }

    public long SourceFramesDropped { get; init; }

    public SchedulerDiagnostics Diagnostics { get; init; }

    public SchedulerCounters Counters { get; init; }
}

/// <summary>
/// Event-driven simulation of the scheduling thread: unique frames arrive on a simulated source clock, the real
/// <see cref="FrameScheduler"/> decides each slot, generation runs on a simulated serial GPU queue with time d, and
/// a simulated display shows images at vblanks (fixed refresh) or when presented (VRR). Wake-up lateness, stalls
/// and decode-ahead delivery can be injected. Every decision is checked against the simulator's own record of
/// frame timestamps and arrivals, independently of the scheduler. The loop does not allocate.
/// </summary>
public sealed class PipelineSimulator
{
    private const int FrameInfoCapacity = 4096;
    private const int RingSlots = 4;
    private const int MaxDistinctTimesteps = 32;
    private const long ContentToleranceNs = 1_000;

    private readonly SimulationOptions _options;
    private readonly FrameScheduler _scheduler;
    private readonly IFrameGenerator _generator;
    private readonly IPresenter _presenter;
    private readonly ITelemetrySink? _telemetry;
    private readonly long[] _frameContent = new long[FrameInfoCapacity];
    private readonly long[] _frameArrival = new long[FrameInfoCapacity];
    private readonly double[] _distinct = new double[MaxDistinctTimesteps];

    public PipelineSimulator(
        SimulationOptions options,
        FrameScheduler scheduler,
        IFrameGenerator generator,
        IPresenter presenter,
        ITelemetrySink? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(presenter);
        _options = options;
        _scheduler = scheduler;
        _generator = generator;
        _presenter = presenter;
        _telemetry = telemetry;
    }

    public SimulationResult Run()
    {
        var source = new SimulatedSource(_options.Source);
        var random = new DeterministicRandom(_options.Seed);
        var clock = new SimulatedClock(Math.Min(0, _options.Source.StartNs - _options.DeliverAheadNs));
        var latency = new Histogram(10 * Nanoseconds.PerMicrosecond, 2 * Nanoseconds.PerSecond);
        var sOptions = _scheduler.Options;
        var fixedRefresh = _scheduler.ClockMode == ClockMode.FixedRefresh;
        var stallEnd = _options.StallAtNs == long.MaxValue ? long.MaxValue : _options.StallAtNs + _options.StallDurationNs;

        var shape = new GpuFrame
        {
            Domain = MemoryDomain.CudaDevice,
            Width = 2560,
            Height = 1440,
            PitchBytes = 2560 * 4,
            SizeBytes = 2560L * 4 * 1440,
            Format = PixelFormat.Rgb10A2,
            Color = ColorMetadata.Hdr10,
        };

        var hasPending = source.TryNext(out var pending);
        var wakeDelay = DrawWakeDelay(random);
        long gpuFreeAt = long.MinValue;
        long slots = 0, real = 0, generated = 0, repeated = 0, empty = 0, missed = 0;
        long starved = 0, late = 0, hold = 0, fallback = 0, discontinuity = 0, skipped = 0;
        var anchorNs = sOptions.VblankAnchorNs;
        var reanchored = false;
        long causality = 0, regressions = 0, inconsistent = 0;
        double delaySum = 0;
        long delayMin = long.MaxValue, delayMax = long.MinValue;
        long lastShownContent = long.MinValue;
        long stepMin = long.MaxValue, stepMax = long.MinValue, stepCount = 0;
        double stepSum = 0, stepSumSq = 0;
        double genTimeSum = 0;
        long genTimeCount = 0;
        var distinctCount = 0;
        var distinctOverflow = false;
        double minT = double.MaxValue, maxT = double.MinValue;
        long shownMin = long.MaxValue, shownMax = long.MinValue, shownCount = 0;
        double shownSum = 0;
        var guard = 0;

        while (true)
        {
            var now = clock.NowNs;
            var nextDelivery = hasPending ? StallAdjusted(pending.ArrivalTimeNs - _options.DeliverAheadNs, stallEnd) : long.MaxValue;
            var requested = _scheduler.NextDecisionTime(now);
            var nextDecision = requested == long.MaxValue ? long.MaxValue : StallAdjusted(requested + wakeDelay, stallEnd);
            var next = Math.Min(nextDelivery, nextDecision);
            if (next >= _options.DurationNs || next == long.MaxValue)
            {
                break;
            }

            clock.AdvanceTo(next);
            now = clock.NowNs;

            if (nextDelivery <= nextDecision)
            {
                var slot = (int)(pending.FrameId % FrameInfoCapacity);
                _frameContent[slot] = pending.ContentTimeNs;
                _frameArrival[slot] = pending.ArrivalTimeNs;
                _scheduler.OnFrameArrived(pending);
                hasPending = source.TryNext(out pending);
                guard = 0;
                continue;
            }

            var decision = _scheduler.Schedule(now);
            wakeDelay = DrawWakeDelay(random);
            if (decision.Kind == SlotKind.Empty && decision.Reason != SlotReason.Startup && ++guard > 1000)
            {
                throw new InvalidOperationException("Scheduler made no progress.");
            }

            var v = decision.TargetTimeNs;
            var ready = now;
            long genStart = 0, genEnd = 0;
            var output = shape with { Address = 0x10_0000UL + (ulong)(decision.SlotIndex % RingSlots), ContentTimeNs = decision.ContentTimeNs };
            var shows = decision.Kind is SlotKind.Real or SlotKind.Generated;
            switch (decision.Kind)
            {
                case SlotKind.Real:
                    ready = now + _options.CopyTimeNs;
                    if (FrameArrival(decision.FrameA) > now)
                    {
                        causality++;
                    }

                    if (decision.ContentTimeNs != FrameContent(decision.FrameA))
                    {
                        inconsistent++;
                    }

                    break;
                case SlotKind.Generated:
                    {
                        var d = GenerationTime(now);
                        d = Math.Max(0, d + random.NextSymmetric(_options.GenerationJitterNs));
                        genStart = Math.Max(now, gpuFreeAt);
                        genEnd = genStart + d;
                        gpuFreeAt = genEnd;
                        ready = genEnd;
                        _scheduler.ReportGenerationTime(d);
                        genTimeSum += d;
                        genTimeCount++;
                        var tsA = FrameContent(decision.FrameA);
                        var tsB = FrameContent(decision.FrameB);
                        var t = (float)decision.Timestep;
                        var expectedContent = tsA + (decision.Timestep * (tsB - tsA));
                        if (!(t > 0f && t < 1f) || Math.Abs(expectedContent - decision.ContentTimeNs) > ContentToleranceNs)
                        {
                            inconsistent++;
                        }

                        if (FrameArrival(decision.FrameA) > now || FrameArrival(decision.FrameB) > now)
                        {
                            causality++;
                        }

                        var prev = shape with { Address = 1UL + (ulong)decision.FrameA, ContentTimeNs = tsA };
                        var curr = shape with { Address = 1UL + (ulong)decision.FrameB, ContentTimeNs = tsB };
                        if (t > 0f && t < 1f)
                        {
                            _generator.Enqueue(prev, curr, t, output, GpuSignal.Timeline(1, (ulong)decision.SlotIndex));
                        }

                        break;
                    }

                default:
                    break;
            }

            if (!reanchored && now >= _options.ReanchorAtNs && decision.Kind != SlotKind.Empty)
            {
                reanchored = true;
                anchorNs = v + _options.ReanchorShiftNs;
                _scheduler.UpdateDisplayTiming(anchorNs, sOptions.DisplayRate);
            }

            var isLate = shows && ready > v;
            var actual = v;
            if (isLate)
            {
                actual = fixedRefresh ? FirstVblankAtOrAfter(sOptions.DisplayRate, anchorNs, ready) : ready;
            }

            if (shows)
            {
                _presenter.TryPresent(new PresentRequest
                {
                    SlotIndex = decision.SlotIndex,
                    Frame = output,
                    Ready = GpuSignal.Timeline(1, (ulong)decision.SlotIndex),
                    TargetTimeNs = v,
                });
                while (_presenter.TryGetFeedback(out _))
                {
                }
            }

            if (v >= _options.StatsStartNs)
            {
                slots++;
                skipped += decision.SkippedSlots;
                switch (decision.Kind)
                {
                    case SlotKind.Real:
                        real++;
                        break;
                    case SlotKind.Generated:
                        generated++;
                        minT = Math.Min(minT, decision.Timestep);
                        maxT = Math.Max(maxT, decision.Timestep);
                        AddDistinct(Math.Round(decision.Timestep, 6), ref distinctCount, ref distinctOverflow);
                        break;
                    case SlotKind.Repeat:
                        repeated++;
                        break;
                    default:
                        empty++;
                        break;
                }

                switch (decision.Reason)
                {
                    case SlotReason.Starved:
                        starved++;
                        break;
                    case SlotReason.Late:
                        late++;
                        break;
                    case SlotReason.Hold:
                        hold++;
                        break;
                    case SlotReason.Fallback:
                        fallback++;
                        break;
                    case SlotReason.Discontinuity:
                        discontinuity++;
                        break;
                    default:
                        break;
                }

                if (isLate)
                {
                    missed++;
                }

                delaySum += decision.PipelineDelayNs;
                delayMin = Math.Min(delayMin, decision.PipelineDelayNs);
                delayMax = Math.Max(delayMax, decision.PipelineDelayNs);

                if (decision.Kind == SlotKind.Real)
                {
                    latency.Add(actual - decision.ContentTimeNs);
                }

                if (shows)
                {
                    var shownLatency = actual - decision.ContentTimeNs;
                    shownMin = Math.Min(shownMin, shownLatency);
                    shownMax = Math.Max(shownMax, shownLatency);
                    shownSum += shownLatency;
                    shownCount++;
                }

                if (shows && lastShownContent != long.MinValue)
                {
                    var step = decision.ContentTimeNs - lastShownContent;
                    if (step < 0)
                    {
                        regressions++;
                    }

                    stepMin = Math.Min(stepMin, step);
                    stepMax = Math.Max(stepMax, step);
                    stepSum += step;
                    stepSumSq += (double)step * step;
                    stepCount++;
                }
            }

            if (shows)
            {
                lastShownContent = decision.ContentTimeNs;
            }

            _telemetry?.Write(new FrameTimelineRecord
            {
                SlotIndex = decision.SlotIndex,
                Kind = decision.Kind,
                Reason = decision.Reason,
                FrameA = decision.FrameA,
                FrameB = decision.FrameB,
                Timestep = decision.Timestep,
                TargetDisplayNs = v,
                ActualDisplayNs = shows ? actual : 0,
                IdealContentNs = decision.IdealContentTimeNs,
                ContentNs = decision.ContentTimeNs,
                PipelineDelayNs = decision.PipelineDelayNs,
                Multiplier = _scheduler.Diagnostics.Multiplier,
                CaptureNs = decision.FrameB >= 0 ? FrameContent(decision.FrameB) : 0,
                ArrivalNs = decision.FrameB >= 0 ? FrameArrival(decision.FrameB) : 0,
                DecisionNs = now,
                GenerationStartNs = genStart,
                GenerationEndNs = genEnd,
                PresentSubmitNs = shows ? ready : 0,
                Missed = isLate,
            });
        }

        var stepMean = stepCount > 0 ? stepSum / stepCount : 0;
        var stepVar = stepCount > 0 ? Math.Max(0, (stepSumSq / stepCount) - (stepMean * stepMean)) : 0;
        var hasDelay = slots > 0 && delayMin != long.MaxValue;
        var distinct = new double[distinctCount];
        Array.Copy(_distinct, distinct, distinctCount);
        Array.Sort(distinct);

        return new SimulationResult
        {
            Slots = slots,
            Real = real,
            Generated = generated,
            Repeated = repeated,
            Empty = empty,
            Missed = missed,
            StarvedSlots = starved,
            LateSlots = late,
            HoldSlots = hold,
            FallbackSlots = fallback,
            DiscontinuitySlots = discontinuity,
            SkippedSlots = skipped,
            CausalityViolations = causality,
            ContentRegressions = regressions,
            TimestepInconsistencies = inconsistent,
            DistinctTimesteps = distinct,
            DistinctTimestepsOverflow = distinctOverflow,
            MinTimestep = generated > 0 ? minT : 0,
            MaxTimestep = generated > 0 ? maxT : 0,
            RealLatencySamples = latency.Count,
            RealLatencyMeanNs = latency.Mean,
            RealLatencyP99Ns = latency.Percentile(0.99),
            RealLatencyMinNs = latency.Min,
            RealLatencyMaxNs = latency.Max,
            ShownLatencyMeanNs = shownCount > 0 ? shownSum / shownCount : 0,
            ShownLatencyMinNs = shownCount > 0 ? shownMin : 0,
            ShownLatencyMaxNs = shownCount > 0 ? shownMax : 0,
            PipelineDelayMeanNs = slots > 0 ? delaySum / slots : 0,
            PipelineDelayMinNs = hasDelay ? delayMin : 0,
            PipelineDelayMaxNs = hasDelay ? delayMax : 0,
            ContentStepMinNs = stepCount > 0 ? stepMin : 0,
            ContentStepMaxNs = stepCount > 0 ? stepMax : 0,
            ContentStepStdDevNs = Math.Sqrt(stepVar),
            MeanGenerationTimeNs = genTimeCount > 0 ? genTimeSum / genTimeCount : 0,
            GeneratorEnqueues = genTimeCount,
            Presents = real + generated,
            SourceFramesDelivered = source.Delivered,
            SourceFramesDropped = source.Dropped,
            Diagnostics = _scheduler.Diagnostics,
            Counters = _scheduler.Counters,
        };
    }

    private long GenerationTime(long now)
    {
        if (now >= _options.GenerationTimeSpikeFromNs && now < _options.GenerationTimeSpikeUntilNs)
        {
            return _options.GenerationTimeSpikeNs;
        }

        return now >= _options.GenerationTimeChangeAtNs ? _options.GenerationTimeAfterChangeNs : _options.GenerationTimeNs;
    }

    private long DrawWakeDelay(DeterministicRandom random) =>
        _options.WakeLatenessNs + (_options.WakeJitterNs > 0 ? (long)(random.NextDouble() * _options.WakeJitterNs) : 0);

    private long StallAdjusted(long timeNs, long stallEnd) =>
        timeNs >= _options.StallAtNs && timeNs < stallEnd ? stallEnd : timeNs;

    private void AddDistinct(double t, ref int count, ref bool overflow)
    {
        for (var i = 0; i < count; i++)
        {
            if (_distinct[i] == t)
            {
                return;
            }
        }

        if (count < _distinct.Length)
        {
            _distinct[count++] = t;
        }
        else
        {
            overflow = true;
        }
    }

    private long FrameContent(long frameId) => frameId < 0 ? 0 : _frameContent[frameId % FrameInfoCapacity];

    private long FrameArrival(long frameId) => frameId < 0 ? 0 : _frameArrival[frameId % FrameInfoCapacity];

    private static long FirstVblankAtOrAfter(FrameRate rate, long anchorNs, long timeNs)
    {
        var index = rate.TickIndexAtOrBefore(timeNs - anchorNs - 1) + 1;
        return anchorNs + rate.TickTimeNs(index);
    }
}
