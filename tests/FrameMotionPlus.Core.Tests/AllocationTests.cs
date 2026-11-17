using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Tests.Support;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests;

/// <summary>
/// Allocation measurements run on their own, after every parallel test: nothing else in the process allocates (and
/// so triggers collections) while they read the thread's allocation counter. Together with non-concurrent GC in the
/// test project (see <see cref="GcConfigurationTests"/>) this keeps <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// exact, so the hot-path tests can demand exactly zero bytes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementDefinition
{
    public const string Name = "Allocation measurement (serial)";
}

/// <summary>
/// Hot-path rule: anything that runs per frame or per output slot allocates nothing. Each test warms the
/// path up first (JIT, static initialisation), then measures this thread's allocated bytes.
/// </summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class AllocationTests
{
    /// <summary>Measured windows per test, after the warm-up window.</summary>
    private const int Windows = 3;

    /// <summary>
    /// Bytes this thread allocates over <see cref="Windows"/> calls of <paramref name="window"/> after a warm-up call:
    /// the total, so an allocation in any one window (a list growing now and then, a buffer allocated once at call
    /// 25,000) counts. Several windows widen what is covered beyond the warm-up; none may allocate.
    /// </summary>
    private static long Measure(Action window)
    {
        window();
        long total = 0;
        for (var k = 0; k < Windows; k++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            window();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }

    public static TheoryData<string> Scenarios => new()
    {
        "fixed-60-120",
        "fixed-59.94-120",
        "fixed-30-144",
        "fixed-fallback",
        "fixed-drops-holds",
        "vrr-60-120",
        "vrr-drops-holds",
    };

    private static (SchedulerOptions Options, SimulatedSourceOptions Source, long D) Scenario(string name) => name switch
    {
        "fixed-60-120" => (Fixed(120), new SimulatedSourceOptions { Rate = FrameRate.FromHz(60) }, 2 * Ms),
        "fixed-59.94-120" => (Fixed(120), new SimulatedSourceOptions { Rate = new FrameRate(60000, 1001) }, 2 * Ms),
        "fixed-30-144" => (Fixed(144), new SimulatedSourceOptions { Rate = FrameRate.FromHz(30) }, 2 * Ms),
        "fixed-fallback" => (Fixed(120, initialDNs: 10 * Ms), new SimulatedSourceOptions { Rate = FrameRate.FromHz(30) }, 10 * Ms),
        "fixed-drops-holds" => (Fixed(120), DropsAndHolds(), 2 * Ms),
        "vrr-60-120" => (Vrr(120), new SimulatedSourceOptions { Rate = FrameRate.FromHz(60) }, 2 * Ms),
        "vrr-drops-holds" => (Vrr(120), DropsAndHolds(), 2 * Ms),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static SimulatedSourceOptions DropsAndHolds() => new()
    {
        Rate = FrameRate.FromHz(60),
        DropProbability = 0.05,
        TimestampJitterNs = 0,
        Holds = new[] { new SimulatedHold(12 * Second, 2 * Second), new SimulatedHold(30 * Second, 500 * Ms) },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scheduler_per_slot_and_per_frame_calls_allocate_nothing(string scenario)
    {
        var (options, source, d) = Scenario(scenario);

        // Every window, each with a fresh scheduler warmed up to 5 s, must allocate nothing from 5 s to 60 s.
        long allocated = 0;
        long slots = 0;
        for (var k = 0; k < Windows; k++)
        {
            var scheduler = new FrameScheduler(options);
            var driver = new ManualDriver(scheduler, source, d);
            driver.RunUntil(5 * Second);

            var before = GC.GetAllocatedBytesForCurrentThread();
            driver.RunUntil(60 * Second);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            slots = driver.Slots;
        }

        Assert.True(slots > 3000, $"only {slots} slots");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Simulator_allocations_do_not_grow_with_duration()
    {
        static long Allocated(double seconds)
        {
            var scheduler = new FrameScheduler(Fixed(120));
            using var generator = new NullFrameGenerator();
            using var presenter = new NullPresenter(FrameRate.FromHz(120));
            var sim = Sim(FrameRate.FromHz(60), seconds) with
            {
                Source = DropsAndHolds(),
            };
            var simulator = new PipelineSimulator(sim, scheduler, generator, presenter, new NullSink());
            var before = GC.GetAllocatedBytesForCurrentThread();
            simulator.Run();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm up with a long run so tiered compilation (including OSR of the long simulation loop) has settled.
        Allocated(100);
        var shortRun = Allocated(10);
        var longRun = Allocated(100);
        Assert.InRange(longRun - shortRun, -1024, 1024);
    }

    private static readonly int[] OnePeriod = [1];
    private static readonly int[] TwoPeriods = [2];
    private static readonly int[] ThreePeriods = [3];
    private static readonly int[] TwoAndSix = [2, 6];
    private static readonly int[] ThreeTwo = [3, 2];
    private static readonly int[] OneTwo = [1, 2];
    private static readonly int[] Pause = [40];

    /// <summary>17 pictures over 52 periods (gaps of 3 and 4): a cycle too long to see whole, so the irregular path runs.</summary>
    private static readonly int[] LongCycle = [.. Enumerable.Range(0, 17).Select(i => ((i + 1) * 52 / 17) - (i * 52 / 17))];

    [Fact]
    public void Cadence_tracker_allocates_nothing_per_frame()
    {
        // A stream on which the tracker learns, and that drives every estimator path in every measured window: steady
        // cadences (SDR and PQ), a rate drop that re-seeds after agreeing holds, recurring holds that re-seed, an uneven
        // cycle reported by its whole-cycle mean, a long cycle settled against its own window means, a pause, in-band
        // repeats and the recovery that feeds the waiting intervals.
        var tracker = new CadenceTracker();
        var signal = FrameRate.FromHz(60);
        long capture = 0;
        long reportChanges = 0;
        var windows = 0;
        CadenceTrackerCounters afterWarmUp = default;

        void Feed(int[] gaps, int cycles, double repeatMetric = 0.0, bool pq = false)
        {
            for (var c = 0; c < cycles; c++)
            {
                foreach (var g in gaps)
                {
                    for (var k = 0; k < g; k++)
                    {
                        var reported = tracker.EstimatedIntervalNs;
                        tracker.Process(signal.TickTimeNs(capture++), k == 0 ? 0.05 : repeatMetric, pq);
                        reportChanges += tracker.EstimatedIntervalNs != reported ? 1 : 0;
                    }
                }
            }
        }

        var allocated = Measure(() =>
        {
            for (var pass = 0; pass < 8; pass++)
            {
                Feed(OnePeriod, 60);
                Feed(OnePeriod, 60, pq: true);
                Feed(ThreePeriods, 20); // 60 -> 20 fps: three agreeing holds re-seed
                Feed(OnePeriod, 120);
                Feed(TwoAndSix, 20); // 100 ms gaps between accepted ones: recurring holds re-seed
                Feed(ThreeTwo, 40); // 3:2: whole-cycle mean
                Feed(LongCycle, 6); // irregular window: hysteresis and settle
                Feed(Pause, 1); // 667 ms without a new picture
                Feed(OneTwo, 30);
                Feed(OneTwo, 60, repeatMetric: 0.006); // in-band repeats split the long gaps
                Feed(TwoPeriods, 30); // exact repeats again: the waiting intervals are fed
            }

            if (++windows == 1)
            {
                afterWarmUp = tracker.Counters;
            }
        });

        var ran = tracker.Counters;
        Assert.True(capture > 4 * 10_000, $"only {capture} captures");
        Assert.True(tracker.EstimatedIntervalNs > 0 && reportChanges > 100, $"the estimate never moved ({reportChanges} changes)");
        Assert.True(tracker.AmbiguousFrames > 0);
        var perPath = new (string Name, long Count)[]
        {
            ("agreeing re-seeds", ran.AgreeingReseeds - afterWarmUp.AgreeingReseeds),
            ("recurring re-seeds", ran.RecurringReseeds - afterWarmUp.RecurringReseeds),
            ("whole-cycle reports", ran.CycleMeanReports - afterWarmUp.CycleMeanReports),
            ("settle checks", ran.SettleChecks - afterWarmUp.SettleChecks),
            ("pauses", ran.Pauses - afterWarmUp.Pauses),
            ("waiting intervals fed", ran.WaitingIntervalsFed - afterWarmUp.WaitingIntervalsFed),
        };
        foreach (var (name, count) in perPath)
        {
            // Eight scenario passes per window, three measured windows: every path must have run in each pass.
            Assert.True(count >= 3 * 8, $"{name}: {count} in the measured windows, the stream no longer drives that path");
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Spsc_ring_and_telemetry_ring_allocate_nothing()
    {
        var ring = new SpscRing<FrameArrival>(64);
        var telemetry = new TelemetryRing(64);
        var record = new FrameTimelineRecord { SlotIndex = 1, Timestep = 0.5 };
        var allocated = Measure(() =>
        {
            for (var k = 0; k < 10_000; k++)
            {
                ring.TryEnqueue(new FrameArrival(k, k, k));
                ring.TryDequeue(out _);
                telemetry.TryWrite(record);
                telemetry.TryRead(out _);
            }

            for (var k = 0; k < 100; k++)
            {
                telemetry.TryWrite(record); // overflow path
            }

            while (telemetry.TryRead(out _))
            {
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Rolling_percentile_and_tuner_allocate_nothing()
    {
        var p = new RollingPercentile(256);
        var tuner = new PipelineDelayTuner(256, 0.99, 2 * Ms, Ms / 2, 50 * Ms, 5 * Ms, Ms / 10);
        var random = new DeterministicRandom(3);
        var allocated = Measure(() =>
        {
            for (var k = 0; k < 10_000; k++)
            {
                var v = (long)(random.NextDouble() * 1e7);
                p.Add(v);
                _ = p.Percentile(0.99) + p.PercentileAtOrBelow(0.9, 5_000_000) + (long)p.Mean;
                tuner.AddGenerationSample(v);
                tuner.Step(tuner.GenerationTimeNs + (10 * Ms), 8_333_333);
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Discontinuity_latch_allocates_nothing()
    {
        var latch = new FrameMotionPlus.Core.Abstractions.DiscontinuityLatch();
        var a = new FrameMotionPlus.Core.Frames.GpuFrame { Address = 0x1000, ContentTimeNs = 1 };
        var b = new FrameMotionPlus.Core.Frames.GpuFrame { Address = 0x2000, ContentTimeNs = 2 };
        long seen = 0;
        var allocated = Measure(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                if (i % 7 == 0)
                {
                    latch.Notify();
                }

                seen += latch.Observe((i & 2) == 0 ? a : b, b) ? 1 : 0;
            }
        });
        Assert.True(seen > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Csv_writer_allocates_nothing_per_record()
    {
        using var writer = new TelemetryCsvWriter(Stream.Null);
        var record = new FrameTimelineRecord
        {
            SlotIndex = 123456,
            Kind = SlotKind.Generated,
            Timestep = 0.333333333,
            TargetDisplayNs = 1_234_567_890_123,
            PipelineDelayNs = 16_666_667,
        };
        var allocated = Measure(() =>
        {
            for (var k = 0; k < 10_000; k++)
            {
                writer.Write(record);
            }
        });
        Assert.Equal(0, allocated);
    }

    private sealed class NullSink : ITelemetrySink
    {
        public void Write(in FrameTimelineRecord record)
        {
        }
    }
}
