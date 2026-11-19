using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The live quality switch's logic (no GPU): <see cref="QualitySwitchControl"/> driving a real <see cref="FrameScheduler"/>
/// against a model generator whose cost is Medium's or High's (VFG at 4K: about 1.4 and 6.2 ms), switched at the commit
/// in queue order as the generator thread does. A generated slot is late when its generation, decided at the decision
/// time, ends after its time V.
/// </summary>
public class QualitySwitchControlTests
{
    private const long Ms = Nanoseconds.PerMillisecond;
    private const long MediumD = 1_400_000;
    private const long HighD = 6_200_000;

    private static long Seed(FrameGenQuality q) => PlaybackSession.InitialGenerationTime(GeneratorKind.Vfg, q);

    [Fact]
    public void A_switch_up_raises_D_first_and_no_slot_is_late_or_skipped_either_way()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        var before = sim.Scheduler.PipelineDelayNs;
        var lateBefore = sim.Late;

        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.Equal(1, control.Commits);
        var up = control.Log[0];
        Assert.Equal(FrameGenQuality.Medium, up.From);
        Assert.Equal(FrameGenQuality.High, up.To);
        Assert.False(up.GaveUpWaiting);

        // D was raised by about High's extra cost before anything was generated at High, at D's slew (50 ms/s).
        Assert.True(up.DelayAtCommitNs - up.DelayAtRequestNs >= 4 * Ms, $"D {up.DelayAtRequestNs} -> {up.DelayAtCommitNs}");
        Assert.InRange(up.CommittedNs - up.RequestedNs, 50 * Ms, 200 * Ms);
        Assert.True(sim.Scheduler.PipelineDelayNs > before + (4 * Ms));

        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.Equal(2, control.Commits);
        var down = control.Log[1];
        Assert.Equal(FrameGenQuality.Medium, down.To);
        Assert.InRange(down.CommittedNs - down.RequestedNs, 0, 5 * Ms); // a cheaper quality is committed at once

        // D came back down (5 ms/s) to where it was.
        Assert.InRange(sim.Scheduler.PipelineDelayNs, before - (Ms / 2), before + (Ms / 2));
        Assert.Equal(lateBefore, sim.Late);
        Assert.Equal(0, sim.Late);
        Assert.Equal(0, sim.Skipped);
        Assert.True(sim.GeneratedAt[FrameGenQuality.High] > 50 && sim.GeneratedAt[FrameGenQuality.Medium] > 100);
    }

    [Fact]
    public void Committing_at_once_without_raising_D_first_would_skip_slots()
    {
        // The control's preparation is what makes the switch clean: the same switch committed in the turn it is asked for
        // (the lead raised before D could follow) skips generated slots.
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        sim.Run(null, 3 * Nanoseconds.PerSecond);
        sim.Scheduler.PrepareGenerationTimeChange(Seed(FrameGenQuality.High));
        sim.Scheduler.CommitGenerationTimeChange();
        sim.Quality = FrameGenQuality.High;
        sim.Run(null, 2 * Nanoseconds.PerSecond);
        Assert.True(sim.Skipped + sim.Late > 0, "expected the naive switch to skip or be late");
    }

    [Fact]
    public void A_fixed_refresh_switch_has_no_late_decision()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.FixedRefresh, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.Equal(2, control.Commits);
        Assert.Equal(0, sim.Scheduler.Counters.LateSlots);
        Assert.Equal(0, sim.Late);
        Assert.Equal(0, sim.Skipped);
    }

    [Fact]
    public void A_press_taken_back_before_the_commit_switches_nothing()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 2 * Nanoseconds.PerSecond);

        // Two presses between two turns: nothing to do.
        control.Toggle();
        control.Toggle();
        sim.Run(control, 100 * Ms);
        Assert.Equal(0, control.Commits);
        Assert.Equal(0, control.Cancels);

        // A press, a turn (preparing: D rising), and a second press before D got there: called off.
        control.Toggle();
        Assert.False(control.Poll(sim.Scheduler, sim.Now, out _, out _));
        Assert.True(control.Pending);
        Assert.True(sim.Scheduler.GenerationTimeChangePending);
        control.Toggle();
        sim.Run(control, 2 * Nanoseconds.PerSecond);
        Assert.Equal(0, control.Commits);
        Assert.Equal(1, control.Cancels);
        Assert.Equal(0, control.Epoch);
        Assert.False(sim.Scheduler.GenerationTimeChangePending);
        Assert.Equal(FrameGenQuality.Medium, control.Committed);
        Assert.Equal(4, control.Requests);
        Assert.Equal(0, sim.Late);
    }

    [Fact]
    public void Rapid_presses_follow_the_latest_wish_and_count_every_commit()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 2 * Nanoseconds.PerSecond);
        for (var i = 0; i < 51; i++)
        {
            control.Toggle();
            sim.Run(control, 40 * Ms);
        }

        sim.Run(control, 2 * Nanoseconds.PerSecond);
        Assert.Equal(FrameGenQuality.High, control.Desired); // 51 presses: an odd number
        Assert.Equal(FrameGenQuality.High, control.Committed);
        Assert.Equal(control.Commits, control.Epoch);
        Assert.Equal(control.Commits, sim.Switches);
        // A press every 40 ms is faster than an up-switch is prepared (D rises for about 100 ms): those are called off by
        // the next press, and the last wish is made once it stands.
        Assert.True(control.Commits >= 1);
        Assert.True(control.Cancels >= 20, $"{control.Cancels} called off");
        Assert.Equal(0, sim.Late);
        Assert.Equal(0, sim.Skipped);
    }

    [Fact]
    public void A_press_before_playback_commits_at_the_first_turn()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        control.Toggle();
        Assert.True(control.Poll(sim.Scheduler, 0, out var to, out var epoch));
        Assert.Equal(FrameGenQuality.High, to);
        Assert.Equal(1, epoch);
        Assert.Equal(Seed(FrameGenQuality.High), sim.Scheduler.Diagnostics.GenerationTimeNs);
    }

    [Fact]
    public void The_estimate_restarts_from_what_the_quality_measured_last_time()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.Equal(Seed(FrameGenQuality.High), control.Log[0].EstimateNs); // never ran: the seed
        Assert.True(sim.Scheduler.GenerationSampleCount >= PipelineDelayTuner.ConfidentSampleCount); // High measured since
        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.InRange(control.Log[1].EstimateNs, MediumD, MediumD + (Ms / 10)); // Medium's own figure, not the 1.6 ms seed
        control.Toggle();
        sim.Run(control, 1 * Nanoseconds.PerSecond);
        Assert.InRange(control.Log[2].EstimateNs, HighD, HighD + (Ms / 10));
    }

    /// <summary>
    /// The other quality's estimate is seeded from its own instance's warm-up at load: the mean run plus a quarter, kept
    /// between half and twice the table's figure; with no warm-up measured, the table.
    /// </summary>
    [Fact]
    public void The_other_quality_is_seeded_from_its_warm_up()
    {
        var table = PlaybackSession.InitialGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High);
        Assert.Equal(6_500 * Nanoseconds.PerMicrosecond, table);

        // 4K High on this machine: 8 warm-up runs in 46 ms (5.75 ms a run): 7.19 ms, above the windowed p99 of 6.996 ms.
        var fourK = PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 46 * Ms, 8);
        Assert.InRange(fourK, 7_100 * Nanoseconds.PerMicrosecond, 7_300 * Nanoseconds.PerMicrosecond);
        Assert.Equal(table / 2, PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 8 * Ms, 8)); // floor
        Assert.Equal(table * 2, PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 800 * Ms, 8)); // a cold first run: ceiling
        Assert.Equal(table, PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 0, 0));
        Assert.Equal(table, PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 46 * Ms, 0));
    }

    /// <summary>
    /// On a GPU where High costs 9.5 ms (half as fast again as the 4K figure), the first up-switch seeded from the warm-up
    /// (8 runs at 7.6 ms) raises D far enough before anything is generated at High: no slot late or skipped.
    /// </summary>
    [Fact]
    public void A_slower_high_seeded_from_its_warm_up_makes_no_slot_late_at_the_first_switch()
    {
        const long slowHigh = 9_500_000;
        var warmed = PlaybackSession.SeededGenerationTime(GeneratorKind.Vfg, FrameGenQuality.High, 8 * 7_600_000, 8);
        Assert.True(warmed >= slowHigh, $"seed {warmed}");
        long SeedFromWarmup(FrameGenQuality q) => q == FrameGenQuality.High ? warmed : Seed(q);
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) })
        {
            HighCost = slowHigh,
        };
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, SeedFromWarmup);
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        control.Toggle();
        sim.Run(control, 3 * Nanoseconds.PerSecond);
        Assert.Equal(1, control.Commits);
        Assert.Equal(warmed, control.Log[0].EstimateNs);
        Assert.Equal(0, sim.Late);
        Assert.Equal(0, sim.Skipped);
        Assert.True(sim.GeneratedAt[FrameGenQuality.High] > 50);

        // The table's 6.5 ms for the same GPU (what was seeded before): the first up-switch is late (the model is deterministic).
        var table = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) })
        {
            HighCost = slowHigh,
        };
        var fixedSeed = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        table.Run(fixedSeed, 3 * Nanoseconds.PerSecond);
        fixedSeed.Toggle();
        table.Run(fixedSeed, 3 * Nanoseconds.PerSecond);
        Assert.True(table.Late + table.Skipped > 0, $"table seed: {table.Late} late, {table.Skipped} skipped");
    }

    [Fact]
    public void Two_different_qualities_are_needed()
    {
        Assert.Throws<ArgumentException>(() => new QualitySwitchControl(FrameGenQuality.High, FrameGenQuality.High, Seed));
        Assert.Throws<ArgumentException>(() => new QualitySwitchControl(FrameGenQuality.Default, FrameGenQuality.High, Seed));
        Assert.Equal(FrameGenQuality.High, QualitySwitchControl.OtherOf(FrameGenQuality.Default));
        Assert.Equal(FrameGenQuality.High, QualitySwitchControl.OtherOf(FrameGenQuality.Low));
        Assert.Equal(FrameGenQuality.Medium, QualitySwitchControl.OtherOf(FrameGenQuality.High));
    }

    [Fact]
    public void Polling_and_toggling_allocate_nothing()
    {
        var sim = new Sim(new SchedulerOptions { ClockMode = ClockMode.Vrr, DisplayRate = FrameRate.FromHz(120), MaxMultiplier = 2, InitialGenerationTimeNs = Seed(FrameGenQuality.Medium) });
        var control = new QualitySwitchControl(FrameGenQuality.Medium, FrameGenQuality.High, Seed);
        sim.Run(control, 2 * Nanoseconds.PerSecond);
        control.Toggle();
        sim.Run(control, 1 * Nanoseconds.PerSecond);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            control.Toggle();
            sim.Run(control, 300 * Ms);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A 30 fps source on time, the scheduler driven at its decision times, and a generator whose cost follows the commits
    /// in queue order: the slots decided after a commit run at the new quality.
    /// </summary>
    private sealed class Sim(SchedulerOptions options)
    {
        private readonly long _interval = (long)FrameRate.FromHz(30).PeriodNs;
        private long _nextFrame;
        private long _frameId;

        public FrameScheduler Scheduler { get; } = new(options);

        public FrameGenQuality Quality { get; set; } = FrameGenQuality.Medium;

        /// <summary>What a High run costs here (a slower GPU or a larger frame than the 4K one: more).</summary>
        public long HighCost { get; init; } = HighD;

        public long Now { get; private set; } = 1;

        public long Late { get; private set; }

        public long Skipped { get; private set; }

        public long Switches { get; private set; }

        public Dictionary<FrameGenQuality, long> GeneratedAt { get; } = new() { [FrameGenQuality.Medium] = 0, [FrameGenQuality.High] = 0 };

        public void Run(QualitySwitchControl? control, long durationNs)
        {
            var end = Now + durationNs;
            while (Now < end)
            {
                while (_nextFrame <= Now)
                {
                    Scheduler.OnFrameArrived(new FrameArrival(_frameId++, _nextFrame, _nextFrame + 200_000));
                    _nextFrame += _interval;
                }

                if (control is not null && control.Poll(Scheduler, Now, out var to, out _))
                {
                    Quality = to;
                    Switches++;
                }

                var next = Scheduler.NextDecisionTime(Now);
                if (next > Now)
                {
                    Now = Math.Min(Math.Min(next, _nextFrame + 200_000), Now + 250_000);
                    continue;
                }

                var d = Scheduler.Schedule(Now);
                if (Scheduler.Counters.Slots > 60 && Now > 2 * Nanoseconds.PerSecond)
                {
                    Skipped += d.SkippedSlots;
                }

                if (d.Kind == SlotKind.Generated)
                {
                    var cost = Quality == FrameGenQuality.High ? HighCost : MediumD;
                    GeneratedAt[Quality]++;
                    if (Now + cost > d.TargetTimeNs)
                    {
                        Late++;
                    }

                    Scheduler.ReportGenerationTime(cost);
                }

                Now += 1_000;
            }
        }
    }
}
