using System.Globalization;
using System.Text;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;
using static FrameMotionPlus.Core.Tests.Support.SimHarness;

namespace FrameMotionPlus.Core.Tests.Telemetry;

public class TelemetryTests
{
    private static FrameTimelineRecord Sample(long slot) => new()
    {
        SlotIndex = slot,
        Kind = SlotKind.Generated,
        Reason = SlotReason.Fallback,
        FrameA = 41,
        FrameB = 42,
        Timestep = 0.25,
        TargetDisplayNs = 1_000_000_000 + slot,
        ActualDisplayNs = 1_000_000_001 + slot,
        IdealContentNs = 983_333_333,
        ContentNs = 983_333_333,
        PipelineDelayNs = -1,
        Multiplier = 4,
        CaptureNs = 990_000_000,
        ArrivalNs = 991_000_000,
        DecisionNs = 997_000_000,
        GenerationStartNs = 997_000_001,
        GenerationEndNs = 999_000_000,
        PresentSubmitNs = 999_000_000,
        Missed = true,
    };

    [Fact]
    public void Csv_has_a_header_and_one_invariant_line_per_record()
    {
        using var stream = new MemoryStream();
        using (var writer = new TelemetryCsvWriter(stream, leaveOpen: true))
        {
            writer.WriteHeader();
            writer.Write(Sample(7));
            writer.Write(Sample(8));
            Assert.Equal(2, writer.RecordsWritten);
        }

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var header = lines[0].Split(',');
        var row = lines[1].Split(',');
        Assert.Equal(header.Length, row.Length);
        var field = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => row[x.i]);
        Assert.Equal("7", field["slot"]);
        Assert.Equal("generated", field["kind"]);
        Assert.Equal("fallback", field["reason"]);
        Assert.Equal("0.25", field["timestep"]);
        Assert.Equal("-1", field["pipeline_delay_ns"]);
        Assert.Equal("1000000007", field["target_display_ns"]);
        Assert.Equal("1", field["missed"]);
        Assert.Equal(0.25, double.Parse(field["timestep"], CultureInfo.InvariantCulture));
    }

    /// <summary>The kind and reason columns of one CSV row written for <paramref name="record"/>.</summary>
    private static (string Kind, string Reason) KindAndReason(FrameTimelineRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new TelemetryCsvWriter(stream, leaveOpen: true))
        {
            writer.Write(record);
        }

        var header = TelemetryCsvWriter.Header.Split(',');
        var row = Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\n').Split(',');
        Assert.Equal(header.Length, row.Length);
        return (row[Array.IndexOf(header, "kind")], row[Array.IndexOf(header, "reason")]);
    }

    [Fact]
    public void Every_slot_reason_has_its_own_csv_token()
    {
        // Enumerates the enum, so a reason added later without a CSV mapping fails here instead of writing "".
        var tokens = new Dictionary<string, SlotReason>(StringComparer.Ordinal);
        foreach (var reason in Enum.GetValues<SlotReason>())
        {
            var token = KindAndReason(Sample(1) with { Reason = reason }).Reason;
            if (reason == SlotReason.None)
            {
                Assert.Equal("", token); // no reason: the column stays empty
                continue;
            }

            Assert.False(token.Length == 0, $"SlotReason.{reason} writes an empty CSV token");
            Assert.True(tokens.TryAdd(token, reason), $"SlotReason.{reason} and SlotReason.{tokens.GetValueOrDefault(token)} share the token '{token}'");
        }

        Assert.Equal(SlotReason.Late, tokens["late"]);
    }

    [Fact]
    public void Every_slot_kind_has_its_own_csv_token()
    {
        var tokens = new Dictionary<string, SlotKind>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<SlotKind>())
        {
            var token = KindAndReason(Sample(1) with { Kind = kind }).Kind;
            Assert.False(token.Length == 0, $"SlotKind.{kind} writes an empty CSV token");
            Assert.True(tokens.TryAdd(token, kind), $"SlotKind.{kind} and SlotKind.{tokens.GetValueOrDefault(token)} share the token '{token}'");
        }

        Assert.Equal(SlotKind.Empty, tokens["empty"]);
    }

    [Fact]
    public void Simulation_telemetry_round_trips_through_the_ring_and_csv()
    {
        var run = Run(Fixed(120), Sim(FrameRate.FromHz(60), 3));
        var ring = new TelemetryRing(64);
        using var stream = new MemoryStream();
        using (var writer = new TelemetryCsvWriter(stream, leaveOpen: true))
        {
            writer.WriteHeader();
            foreach (var r in run.Records)
            {
                if (ring.Count >= 32)
                {
                    ring.DrainTo(writer);
                }

                Assert.True(ring.TryWrite(r));
            }

            ring.DrainTo(writer);
        }

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(run.Records.Count + 1, lines.Length);
        Assert.Equal(0, ring.Dropped);
    }

    [Fact]
    public void Ring_drops_and_counts_when_the_writer_falls_behind()
    {
        var ring = new TelemetryRing(4);
        for (var i = 0; i < 10; i++)
        {
            ring.Write(Sample(i));
        }

        Assert.Equal(6, ring.Dropped);
        Assert.Equal(4, ring.Count);
        Assert.True(ring.TryRead(out var first));
        Assert.Equal(0, first.SlotIndex);
    }

    [Fact]
    public void Writer_flushes_when_its_buffer_fills()
    {
        using var stream = new MemoryStream();
        using var writer = new TelemetryCsvWriter(stream, leaveOpen: true, bufferSize: 1024);
        for (var i = 0; i < 100; i++)
        {
            writer.Write(Sample(i));
        }

        Assert.True(stream.Length > 0);
        writer.Flush();
        Assert.Equal(100, Encoding.UTF8.GetString(stream.ToArray()).Count(c => c == '\n'));
    }
}
