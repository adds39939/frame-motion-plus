using FrameMotionPlus.Core.Tests.Support;

namespace FrameMotionPlus.Core.Tests.Scheduling;

/// <summary>
/// Sweep of clock modes × source/display ratios × disturbances. Every run asserts liveness and bounds: no causality,
/// order or timestep errors; missed presents bounded per disturbance event and zero in the final steady state;
/// images shown every second outside pauses; generation active again in the final state whenever the design
/// permits it; latency back in its band; D steady (no oscillation, bounded range) at the end.
/// </summary>
public class DisturbanceSweepTests(ITestOutputHelper output)
{
    public static TheoryData<SweepCase> SmallSweep()
    {
        var data = new TheoryData<SweepCase>();
        foreach (var c in DisturbanceSweep.Cases(1))
        {
            data.Add(c);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SmallSweep))]
    public void Every_disturbance_recovers(SweepCase c)
    {
        var violations = DisturbanceSweep.Run(c);
        Assert.True(violations.Count == 0, $"{c}: {string.Join("; ", violations)}");
    }

    /// <summary>The larger sweep: three more seeds and 60 s runs. Run with <c>dotnet test -- xUnit.Explicit=on</c>.</summary>
    [Fact(Explicit = true)]
    public void Large_sweep_every_disturbance_recovers()
    {
        var failures = new List<string>();
        var runs = 0;
        long slots = 0, generated = 0, missed = 0, late = 0, skipped = 0;
        var perDisturbance = new SortedDictionary<string, (int Runs, long Missed, long Late)>(StringComparer.Ordinal);
        foreach (var c in DisturbanceSweep.Cases(2, 3, 4))
        {
            runs++;
            var violations = DisturbanceSweep.Run(c, 60, out var r);
            slots += r.Slots;
            generated += r.Generated;
            missed += r.Missed;
            late += r.LateSlots;
            skipped += r.SkippedSlots;
            perDisturbance.TryGetValue(c.Disturbance, out var agg);
            perDisturbance[c.Disturbance] = (agg.Runs + 1, agg.Missed + r.Missed, agg.Late + r.LateSlots);
            if (violations.Count > 0)
            {
                failures.Add($"{c}: {string.Join("; ", violations)}");
            }
        }

        output.WriteLine($"{runs} runs, {slots} slots simulated, {generated} generated, {missed} missed, {late} late, {skipped} skipped");
        foreach (var (name, agg) in perDisturbance)
        {
            output.WriteLine($"  {name,-18} runs {agg.Runs,3}  missed {agg.Missed,3}  late-slot fallbacks {agg.Late,4}");
        }

        foreach (var f in failures)
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {runs} runs failed:\n{string.Join("\n", failures)}");
    }
}
