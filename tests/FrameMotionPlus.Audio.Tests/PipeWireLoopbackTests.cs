using System.Globalization;
using FrameMotionPlus.Audio.PipeWire;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Audio.Tests.Support;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// The real path through a real PipeWire server, on two null sinks this test creates (owned by its connection: they go
/// when it closes, and nothing of the user's devices is used or changed): a click train is played into sink A; the delay
/// line captures A's monitor and plays into sink B; a recorder captures B's monitor. The delay measured from the clicks
/// (the time each is captured from B against the time it was heard at A, both from PipeWire's own stream timing) must
/// equal the target within 2 ms, no click may be lost or doubled, the callbacks allocate nothing, and after disposal no
/// node of the test is left in the graph. The recorder asks for 128-frame cycles, so sink B's graph runs at 128 and sink
/// A's at 256: that is what tells a cycle of latency on the playback side from one on the capture side
/// (<see cref="PipeWire.PwTiming.PlaybackExtraCycles"/>). Skips, never fails, when there is no PipeWire server (CI).
/// </summary>
public sealed class PipeWireLoopbackTests
{
    private const long Ms = 1_000_000;

    internal static PwConnection RequireServer()
    {
        if (!PwConnection.TryConnect("fmp-audio-tests", out var connection, out var reason))
        {
            Assert.Skip($"needs a PipeWire server: {reason}");
        }

        return connection;
    }

    internal static List<KeyValuePair<string, string>> NullSink(string name) =>
    [
        new(PwKeys.FactoryName, "support.null-audio-sink"),
        new(PwKeys.NodeName, name),
        new(PwKeys.NodeDescription, "fmp test sink " + name),
        new(PwKeys.MediaClass, "Audio/Sink"),
        new("audio.position", "[ FL FR ]"),
        new(PwKeys.AudioRate, "48000"),
        new(PwKeys.NodeVirtual, "true"),
        new(PwKeys.PrioritySession, "1"),
        new("object.linger", "false"),
        // Nothing of the test's sinks is saved in WirePlumber's state.
        new("state.restore-props", "false"),
        new("state.restore-target", "false"),
    ];

    [Theory]
    [InlineData(60.0)]
    [InlineData(35.0)]
    public void Clicks_through_the_delay_line_arrive_at_the_target_delay_with_none_lost(double targetMs)
    {
        using var test = RequireServer();
        var id = $"{Environment.ProcessId}-{targetMs.ToString(CultureInfo.InvariantCulture)}";
        var a = $"fmp-test-a-{id}";
        var b = $"fmp-test-b-{id}";
        nint sinkA, sinkB;
        using (test.Lock())
        {
            sinkA = test.CreateNodeLocked("adapter", NullSink(a));
            sinkB = test.CreateNodeLocked("adapter", NullSink(b));
            Assert.True(test.RoundtripLocked(3000));
        }

        try
        {
            WaitFor(() =>
            {
                using (test.Lock())
                {
                    return test.FindNodeLocked(a) is not null && test.FindNodeLocked(b) is not null;
                }
            });

            var events = new List<string>();
            Assert.True(PipeWireAudioDelay.TryCreate(
                new AudioDelayOptions
                {
                    Source = a,
                    SourceIsSinkMonitor = true,
                    Sink = b,
                    NodePrefix = $"fmp-test-delay-{id}",
                    SettleNs = 1_000 * Ms,
                    InitialVideoLatencyNs = (long)(targetMs * Ms),
                    OnEvent = e => { lock (events) { events.Add(e); } },
                },
                out var delay,
                out var why), why);
            using var generator = new ClickGenerator(test, $"fmp-test-clicks-{id}", a);
            using var recorder = new ClickRecorder(test, $"fmp-test-recorder-{id}", b);
            AudioReport report;
            using (delay)
            {
                var setup = delay.StartAndDescribe();
                delay.SetTargetDelay((long)(targetMs * Ms));
                Assert.True(setup.Streaming, "the delay line's streams did not start");
                Assert.Equal(b, setup.Sink);
                _ = recorder.Connect(capture: true);
                _ = generator.Connect(capture: false);

                // 7 s: the generator's clicks start after 0.4 s; the line settles within a second or two.
                Thread.Sleep(7000);
                generator.Enabled = false;
                Thread.Sleep(300);
                delay.Stop();
                Thread.Sleep(50);
            }

            report = delay.Report();
            Assert.True(report.TornDown);
            Assert.Null(report.Fault);
            Assert.Equal(0, report.CallbackAllocatedBytes);

            // Pair the clicks: every click played after the settle (from 3 s on) arrives exactly once, at the target delay.
            var played = Enumerable.Range(0, generator.Count).Select(generator.Click).ToList();
            var received = Enumerable.Range(0, recorder.Count).Select(recorder.CapturedNs).ToList();
            Assert.True(played.Count >= 60, $"{played.Count} clicks played");
            var settled = played.Where(c => c.HeardNs > played[0].HeardNs + (3_000 * Ms)).ToList();
            var delays = new List<double>();
            foreach (var (frame, heard) in settled)
            {
                var matches = received.Where(r => Math.Abs(r - heard - (targetMs * Ms)) < 20 * Ms).ToList();
                Assert.True(matches.Count == 1, $"click at frame {frame}: {matches.Count} arrivals near {targetMs} ms (delays of the nearest: " +
                    $"{string.Join(", ", received.OrderBy(r => Math.Abs(r - heard - (targetMs * Ms))).Take(3).Select(r => ((r - heard) / 1e6).ToString("0.000", CultureInfo.InvariantCulture)))} ms)");
                delays.Add(matches[0] - heard);
            }

            var s = report.Statistics;
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"click delays {delays.Min() / 1e6:0.000} to {delays.Max() / 1e6:0.000} ms (mean {delays.Average() / 1e6:0.000}) for target {targetMs} ms; " +
                $"line says mean {s.DelayMean / 1e6:0.000} ms; capture latency {report.CaptureLatencyMeanNs / 1e6:0.000} ms, sink {report.SinkLatencyMeanNs / 1e6:0.000} ms; " +
                $"underruns {s.Underruns}, jumps {s.Jumps}, resyncs {s.Resyncs}, gaps {s.CaptureGaps}; events: {string.Join("; ", events)}");
            Assert.True(delays.All(d => Math.Abs(d - (targetMs * Ms)) < 2 * Ms), summary);
            Assert.Equal(0, s.Underruns + s.Overruns + s.Resyncs);
            TestContext.Current.SendDiagnosticMessage(summary);
        }
        finally
        {
            using (test.Lock())
            {
                PwConnection.DestroyProxyLocked(sinkA);
                PwConnection.DestroyProxyLocked(sinkB);
                _ = test.RoundtripLocked(2000);
            }
        }

        // Nothing of the test is left: its sinks, the line's streams, the generator and the recorder are all gone.
        using var check = RequireServer();
        using (check.Lock())
        {
            Assert.DoesNotContain(check.NodesLocked(), n => n.Name.Contains(id, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_source_that_does_not_exist_is_refused_with_one_line()
    {
        using var test = RequireServer();
        Assert.False(PipeWireAudioDelay.TryCreate(new AudioDelayOptions { Source = "no-such-node-fmp" }, out var delay, out var why));
        Assert.Null(delay);
        Assert.Contains("no PipeWire audio source named no-such-node-fmp", why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_snapshot_lists_sources_and_sinks()
    {
        using var test = RequireServer();
        Assert.True(AudioDevices.TrySnapshot(out var snapshot, out var why), why);
        Assert.NotNull(snapshot.LibraryVersion);
        Assert.All(snapshot.Sinks, s => Assert.False(string.IsNullOrEmpty(s.Name)));
        Assert.True(snapshot.Sinks.Count(s => s.IsDefault) <= 1);
    }

    internal static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Thread.Sleep(20);
        }
    }
}
