using System.Globalization;
using FrameMotionPlus.Pipeline;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp verify &lt;telemetry.csv&gt;</c>: recomputes a playback's verdict from its telemetry CSV alone
/// (<see cref="TelemetryVerifier"/>) and compares it with the live accounting the CSV records. Exit 0 when nothing was
/// missed, nothing is unjudged and the two agree; 1 otherwise.
/// </summary>
internal static class VerifyCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException("fmp verify needs a telemetry CSV: fmp verify <file.csv> [--rate <hz>] [--vrr] [--warmup-s <s>] [--slots <a>-<b>].");
        }

        var path = args[0];
        double? rate = null;
        var warmupS = 2.0;
        var vrr = false;
        (long From, long To)? show = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rate" when i + 1 < args.Length:
                    rate = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--vrr":
                    vrr = true;
                    break;
                case "--warmup-s" when i + 1 < args.Length:
                    warmupS = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--slots" when i + 1 < args.Length:
                    var range = args[++i].Split('-');
                    show = (long.Parse(range[0], CultureInfo.InvariantCulture), long.Parse(range[^1], CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new UsageException($"Unknown option '{args[i]}' for fmp verify.");
            }
        }

        if (!File.Exists(path))
        {
            error.WriteLine($"fmp: verify: no such file: {path}");
            return CommandDispatcher.ExitCheckFailed;
        }

        List<VerifierRow> rows;
        using (var reader = new StreamReader(path))
        {
            rows = TelemetryVerifier.ParseCsv(reader);
        }

        var period = rate is { } hz ? (long)Math.Round(1e9 / hz) : MedianInterval(rows);
        var first = rows.Where(r => r.Kind is VerifierKind.Real or VerifierKind.Generated).Select(r => r.TargetNs).DefaultIfEmpty(0).Min();
        var from = first + (long)(warmupS * 1e9);
        var result = TelemetryVerifier.Verify(rows, period, from, long.MaxValue, vrr);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"fmp verify: {path}: {(vrr ? "variable refresh, shortest refresh period" : "refresh period")} {period / 1e6:0.000} ms, judged from {warmupS:0.#} s after the first new image"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  slots       {result.Judged} judged: {result.Misses} missed (early {result.EarlySlots}, late {result.Count(VerifierFinding.Late)}, not shown {result.Count(VerifierFinding.NotShown)}, " +
            $"refresh taken by another {result.Count(VerifierFinding.RefreshTaken)}, wrong image {result.Count(VerifierFinding.WrongImage)}, out of order {result.Count(VerifierFinding.OutOfOrder)}), " +
            $"{result.Unjudged} unjudged"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  refreshes   {result.Gaps.Count} interval(s) of two refreshes or more ({result.Gaps.Sum(g => g.Refreshes)} refresh(es) without a new present), {result.UnexplainedRefreshes} unexplained"));
        output.WriteLine($"  live        {result.Disagreements} slot(s) where the live accounting disagrees");
        output.WriteLine(Lateness(rows, result, from));
        if (result.Slots.Count > 0)
        {
            output.WriteLine($"  findings    {result.Describe(24)}");
        }

        foreach (var (at, n, explained) in result.Gaps.Take(12))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  gap         {n} refresh(es) before the show at {at} ns{(explained ? "" : " (unexplained)")}"));
        }

        if (show is { } r)
        {
            foreach (var (slot, f) in result.Slots.Where(x => x.Slot >= r.From && x.Slot <= r.To))
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  slot        {slot}: {f}"));
            }
        }

        output.WriteLine(result.Clean ? "  verdict     PASS" : "  verdict     FAIL");
        return result.Clean ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
    }

    /// <summary>
    /// How late the late slots were (first pixel less the slot's time, a histogram) and how they came: in runs of
    /// consecutive late slots (a steady offset: the intervals stay even, only the latency grows) or alone (a visible
    /// judder: one long interval and one short).
    /// </summary>
    private static string Lateness(List<VerifierRow> rows, VerificationResult result, long from)
    {
        var late = result.Slots.Where(x => (x.Finding & VerifierFinding.Late) != 0).Select(x => x.Slot).ToHashSet();
        var bounds = new[] { 0.5, 1, 2, 3, 4, 5, 6, 8, 12, 17, double.MaxValue };
        var counts = new int[bounds.Length];
        int runs = 0, alone = 0, longest = 0, current = 0;
        foreach (var r in rows.Where(r => r.Slot >= 0 && r.Kind is VerifierKind.Real or VerifierKind.Generated && r.TargetNs >= from).OrderBy(r => r.Slot))
        {
            if (!late.Contains(r.Slot))
            {
                runs += current > 1 ? 1 : 0;
                alone += current == 1 ? 1 : 0;
                current = 0;
                continue;
            }

            current++;
            longest = Math.Max(longest, current);
            var ms = (r.ActualNs - r.TargetNs) / 1e6;
            counts[Array.FindIndex(bounds, b => ms < b)]++;
        }

        runs += current > 1 ? 1 : 0;
        alone += current == 1 ? 1 : 0;
        var parts = new List<string>();
        for (var i = 0; i < bounds.Length; i++)
        {
            if (counts[i] > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(i == 0 ? "<" : $"{bounds[i - 1]:0.#}-")}{(bounds[i] == double.MaxValue ? "" : $"{bounds[i]:0.#}")}{(bounds[i] == double.MaxValue ? "+" : "")} ms {counts[i]}"));
            }
        }

        return late.Count == 0
            ? "  lateness    no late slot"
            : string.Create(CultureInfo.InvariantCulture,
                $"  lateness    {late.Count} late slot(s), first pixel after the slot's time: {string.Join(", ", parts)}; {runs} run(s) of consecutive late slots (the longest {longest}), {alone} alone");
    }

    private static long MedianInterval(List<VerifierRow> rows)
    {
        var shows = rows.Where(r => r.ActualNs > 0).Select(r => r.ActualNs).Order().ToArray();
        var d = new List<long>();
        for (var i = 1; i < shows.Length; i++)
        {
            d.Add(shows[i] - shows[i - 1]);
        }

        d.Sort();
        return d.Count == 0 ? 8_333_333 : d[d.Count / 2];
    }
}
