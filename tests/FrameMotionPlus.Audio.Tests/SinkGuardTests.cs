using System.Globalization;
using FrameMotionPlus.Audio.PipeWire;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Audio.Tests.Support;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// Where the sound may go, against null sinks the test creates (nothing of the machine's devices is used): the playback
/// stream is pinned to the sink chosen at the start and plays to no other output device (a recorder linked to it, such as
/// a screen share's sound or a PulseAudio monitor of the stream, is left alone and receives the sound). The named sink removed mid-run: muted, linked
/// to nothing else, and back on it when a sink of that name returns. A per-stream move through the metadata (what the
/// desktop's "move to" does, and what WirePlumber writes to make a stream follow the default): refused. A link made by hand
/// to another sink: muted at once, cut, and made again to the chosen sink. Throughout, a link sampler on the test's own
/// connection checks that the playback node never feeds any node but the chosen sink, and a bystander sink records
/// nothing. Skips without a PipeWire server.
/// </summary>
public sealed class SinkGuardTests
{
    private const long Ms = 1_000_000;

    private sealed class Rig : IDisposable
    {
        private readonly List<string> _events = [];
        private readonly List<uint> _foreign = [];
        private readonly Thread _sampler;
        private volatile bool _sampling = true;

        public Rig(string name)
        {
            Test = PipeWireLoopbackTests.RequireServer();
            Id = $"{Environment.ProcessId}-{name}";
            A = $"fmp-test-a-{Id}";
            B = $"fmp-test-b-{Id}";
            C = $"fmp-test-c-{Id}";
            using (Test.Lock())
            {
                SinkA = Test.CreateNodeLocked("adapter", PipeWireLoopbackTests.NullSink(A));
                SinkB = Test.CreateNodeLocked("adapter", PipeWireLoopbackTests.NullSink(B));
                SinkC = Test.CreateNodeLocked("adapter", PipeWireLoopbackTests.NullSink(C));
                Assert.True(Test.RoundtripLocked(3000));
            }

            PipeWireLoopbackTests.WaitFor(() => Node(A) is not null && Node(B) is not null && Node(C) is not null);
            Assert.True(PipeWireAudioDelay.TryCreate(
                new AudioDelayOptions
                {
                    Source = A,
                    SourceIsSinkMonitor = true,
                    Sink = B,
                    NodePrefix = $"fmp-test-delay-{Id}",
                    SettleNs = 1_000 * Ms,
                    InitialVideoLatencyNs = 40 * Ms,
                    OnEvent = e => { lock (_events) { _events.Add(e); } },
                },
                out var delay,
                out var why), why);
            Delay = delay;
            _ = Delay.StartAndDescribe();
            Delay.SetTargetDelay(40 * Ms);
            Generator = new ClickGenerator(Test, $"fmp-test-clicks-{Id}", A);
            _ = Generator.Connect(capture: false);
            Bystander = new ClickRecorder(Test, $"fmp-test-bystander-{Id}", C);
            _ = Bystander.Connect(capture: true);
            _sampler = new Thread(Sample) { IsBackground = true };
            _sampler.Start();
        }

        public PwConnection Test { get; }

        public string Id { get; }

        public string A { get; }

        public string B { get; }

        public string C { get; }

        public nint SinkA { get; }

        public nint SinkB { get; set; }

        public nint SinkC { get; }

        public PipeWireAudioDelay Delay { get; }

        public ClickGenerator Generator { get; }

        public ClickRecorder Bystander { get; }

        public string Events
        {
            get
            {
                lock (_events)
                {
                    return string.Join("; ", _events);
                }
            }
        }

        /// <summary>Output devices other than B (or nodes not announced) that the playback node was ever seen feeding.</summary>
        public List<uint> Foreign
        {
            get
            {
                lock (_foreign)
                {
                    return [.. _foreign];
                }
            }
        }

        public PwNode? Node(string name)
        {
            using (Test.Lock())
            {
                return Test.FindNodeLocked(name);
            }
        }

        /// <summary>A node the registry announced that is not an output device (a recorder taking a copy).</summary>
        public bool IsRecorder(uint id)
        {
            using (Test.Lock())
            {
                return Test.TryNodeKindLocked(id, out _, out var mediaClass) && !PwNode.IsOutputDevice(mediaClass);
            }
        }

        /// <summary>"id name (media.class)" as this connection's registry knows the node, or "id not announced".</summary>
        public string Describe(uint id)
        {
            using (Test.Lock())
            {
                return Test.TryNodeKindLocked(id, out var name, out var mediaClass) ? $"{id} {name} ({mediaClass ?? "no media.class"})" : $"{id} not announced";
            }
        }

        public List<uint> PlaybackPeers()
        {
            var node = Delay.PlaybackNodeId;
            using (Test.Lock())
            {
                return node == PwDefs.IdAny ? [] : Test.PeersOutOfLocked(node);
            }
        }

        private void Sample()
        {
            while (_sampling)
            {
                var b = Node(B)?.Id;
                foreach (var peer in PlaybackPeers())
                {
                    if (peer != b && !IsRecorder(peer))
                    {
                        lock (_foreign)
                        {
                            _foreign.Add(peer);
                        }
                    }
                }

                Thread.Sleep(20);
            }
        }

        public void Dispose()
        {
            _sampling = false;
            _sampler.Join();
            Delay.Dispose();
            Generator.Dispose();
            Bystander.Dispose();
            using (Test.Lock())
            {
                PwConnection.DestroyProxyLocked(SinkA);
                PwConnection.DestroyProxyLocked(SinkB);
                PwConnection.DestroyProxyLocked(SinkC);
                _ = Test.RoundtripLocked(2000);
            }

            Test.Dispose();
            using var check = PipeWireLoopbackTests.RequireServer();
            using (check.Lock())
            {
                Assert.DoesNotContain(check.NodesLocked(), n => n.Name.Contains(Id, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void The_named_sink_removed_mid_run_mutes_links_nothing_else_and_resumes_when_it_returns()
    {
        using var rig = new Rig("gone");
        using var first = new ClickRecorder(rig.Test, $"fmp-test-rec1-{rig.Id}", rig.B);
        _ = first.Connect(capture: true);
        Thread.Sleep(2500);
        Assert.True(first.Count > 5, $"{first.Count} clicks before the sink went; events: {rig.Events}");

        // B goes.
        using (rig.Test.Lock())
        {
            PwConnection.DestroyProxyLocked(rig.SinkB);
            rig.SinkB = 0; // destroyed: never again (a second pw_proxy_destroy would be a use after free)
            Assert.True(rig.Test.RoundtripLocked(2000));
        }

        Thread.Sleep(2500);
        Assert.Empty(rig.PlaybackPeers());
        Assert.Contains($"the sink {rig.B} is gone", rig.Events, StringComparison.Ordinal);
        Assert.Contains("the sound is muted", rig.Events, StringComparison.Ordinal);
        Assert.True(rig.Delay.Line.Muted, "the line is not muted with its sink gone");

        // A sink of that name comes back: the sound goes there again, and only there.
        using (rig.Test.Lock())
        {
            rig.SinkB = rig.Test.CreateNodeLocked("adapter", PipeWireLoopbackTests.NullSink(rig.B));
            Assert.True(rig.Test.RoundtripLocked(2000));
        }

        PipeWireLoopbackTests.WaitFor(() => rig.Node(rig.B) is not null);
        using var second = new ClickRecorder(rig.Test, $"fmp-test-rec2-{rig.Id}", rig.B);
        _ = second.Connect(capture: true);
        var until = DateTime.UtcNow.AddSeconds(8);
        while (second.Count < 5 && DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
        }

        Assert.True(second.Count >= 5, $"{second.Count} clicks after the sink came back; events: {rig.Events}");
        Assert.Contains($"the sink {rig.B} is back", rig.Events, StringComparison.Ordinal);
        Assert.Contains("the sound resumes", rig.Events, StringComparison.Ordinal);
        Assert.Empty(rig.Foreign);
        Assert.Equal(0, rig.Bystander.Count);
    }

    [Fact]
    public void A_per_stream_move_through_the_metadata_is_refused()
    {
        var pwMetadata = Tools.Which("pw-metadata");
        if (pwMetadata is null)
        {
            Assert.Skip("needs pw-metadata");
        }

        using var rig = new Rig("move");
        Thread.Sleep(1500);
        var node = rig.Delay.PlaybackNodeId.ToString(CultureInfo.InvariantCulture);
        var serial = rig.Node(rig.C)!.Serial.ToString(CultureInfo.InvariantCulture);

        // What the desktop's "move to" writes, and what WirePlumber writes to make a stream follow the default.
        Assert.Equal(0, Tools.Run(pwMetadata!, ["--", node, "target.object", serial]).ExitCode);
        Assert.Equal(0, Tools.Run(pwMetadata!, ["--", node, "target.node", "-1", "Spa:Id"]).ExitCode);
        Thread.Sleep(2500);
        var b = rig.Node(rig.B)!.Id;
        Assert.All(rig.PlaybackPeers(), p => Assert.Equal(b, p));
        Assert.NotEmpty(rig.PlaybackPeers());
        _ = Tools.Run(pwMetadata!, ["-d", "--", node, "target.object"]);
        _ = Tools.Run(pwMetadata!, ["-d", "--", node, "target.node"]);
        Assert.Empty(rig.Foreign);
        Assert.Equal(0, rig.Bystander.Count);
        Assert.Equal(0, rig.Delay.Report().WrongSinkLinks);
    }

    [Fact]
    public void A_link_made_by_hand_to_another_sink_is_muted_cut_and_made_again_to_the_chosen_sink()
    {
        var pwLink = Tools.Which("pw-link");
        if (pwLink is null)
        {
            Assert.Skip("needs pw-link");
        }

        using var rig = new Rig("link");
        Thread.Sleep(1500);
        var output = $"fmp-test-delay-{rig.Id}-playback";
        Assert.Equal(0, Tools.Run(pwLink!, [$"{output}:output_FL", $"{rig.C}:playback_FL"]).ExitCode);
        Thread.Sleep(3000);
        Assert.Contains($"linked to another node than {rig.B}", rig.Events, StringComparison.Ordinal);
        Assert.True(rig.Delay.Report().WrongSinkLinks >= 1);
        var b = rig.Node(rig.B)!.Id;
        Assert.NotEmpty(rig.PlaybackPeers());
        Assert.All(rig.PlaybackPeers(), p => Assert.Equal(b, p));

        // At most what played in the supervisor's reaction time (a look every 50 ms; the clicks are 100 ms apart).
        Assert.True(rig.Bystander.Count <= 1, $"{rig.Bystander.Count} clicks reached the other sink");
    }

    /// <summary>
    /// A recorder linked to the playback stream (what a screen share's sound does: Discord's links a capture stream
    /// targeting the application's playback node): the sound keeps playing to the chosen sink, the recorder receives it,
    /// nothing is muted, cut or counted as a wrong link, and the recorder is reported once.
    /// </summary>
    [Fact]
    public void A_recorder_linked_to_the_playback_stream_is_left_alone_and_the_sound_keeps_playing()
    {
        using var rig = new Rig("recorder");
        using var onB = new ClickRecorder(rig.Test, $"fmp-test-onb-{rig.Id}", rig.B);
        _ = onB.Connect(capture: true);
        Thread.Sleep(1500);
        var output = $"fmp-test-delay-{rig.Id}-playback";
        var restartsBefore = rig.Delay.Report().PlaybackRestarts;
        var clicksBefore = onB.Count;
        using var recorder = new ClickRecorder(rig.Test, $"fmp-test-share-{rig.Id}", output, captureSink: false);
        _ = recorder.Connect(capture: true);
        LinkByHandIfNeeded(rig, output, $"fmp-test-share-{rig.Id}");
        Thread.Sleep(3000);
        Assert.Contains(rig.PlaybackPeers(), rig.IsRecorder);
        Assert.True(recorder.Count >= 10, $"the recorder got {recorder.Count} clicks; events: {rig.Events}");
        Assert.True(onB.Count - clicksBefore >= 20, $"{onB.Count - clicksBefore} clicks on the chosen sink while recorded; events: {rig.Events}");
        var report = rig.Delay.Report();
        Assert.Equal(0, report.WrongSinkLinks);
        Assert.Equal(restartsBefore, report.PlaybackRestarts);
        Assert.False(rig.Delay.Line.Muted, $"muted with a recorder attached; events: {rig.Events}");
        Assert.DoesNotContain("the sound is muted", rig.Events, StringComparison.Ordinal);
        Assert.Contains("recorder(s) take a copy of the sound", rig.Events, StringComparison.Ordinal);
        Assert.Empty(rig.Foreign);
        Assert.Equal(0, rig.Bystander.Count);
    }

    /// <summary>
    /// A PulseAudio-style monitor of the stream (<c>parec --monitor-stream</c>, a sink input's own monitor through
    /// pipewire-pulse): left alone in the same way; the monitor receives the sound.
    /// </summary>
    [Fact]
    public void A_pulseaudio_monitor_of_the_stream_is_left_alone()
    {
        var parec = Tools.Which("parec");
        var pactl = Tools.Which("pactl");
        if (parec is null || pactl is null)
        {
            Assert.Skip("needs parec and pactl");
        }

        using var rig = new Rig("monitor");
        Thread.Sleep(1500);
        var output = $"fmp-test-delay-{rig.Id}-playback";
        var (code, json) = Tools.Run(pactl!, ["-f", "json", "list", "sink-inputs"]);
        Assert.Equal(0, code);
        long index = -1;
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            foreach (var input in doc.RootElement.EnumerateArray())
            {
                if (input.TryGetProperty("properties", out var props) && props.TryGetProperty("node.name", out var name) && name.GetString() == output)
                {
                    index = input.GetProperty("index").GetInt64();
                }
            }
        }

        Assert.True(index >= 0, $"no sink input for {output}");
        var start = new System.Diagnostics.ProcessStartInfo(parec!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { $"--monitor-stream={index}", "--raw", "--format=float32le", "--channels=2", "--rate=48000", "--latency-msec=20" })
        {
            start.ArgumentList.Add(a);
        }

        using var monitor = System.Diagnostics.Process.Start(start)!;
        var peak = 0f;
        var bytes = 0L;
        var reader = new Thread(() =>
        {
            var buffer = new byte[8192];
            var stream = monitor.StandardOutput.BaseStream;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length - (buffer.Length % 4))) > 0)
            {
                bytes += n;
                for (var i = 0; i + 4 <= n; i += 4)
                {
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
                }
            }
        }) { IsBackground = true };
        reader.Start();
        Thread.Sleep(3000);
        var peers = rig.PlaybackPeers();
        var described = string.Join(", ", peers.Select(rig.Describe));
        var announcedAfterMs = -1L;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!rig.PlaybackPeers().Any(rig.IsRecorder) && watch.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(20);
        }

        if (rig.PlaybackPeers().Any(rig.IsRecorder))
        {
            announcedAfterMs = watch.ElapsedMilliseconds;
        }

        var report = rig.Delay.Report();
        var muted = rig.Delay.Line.Muted;
        monitor.Kill();
        monitor.WaitForExit(2000);
        reader.Join(2000);
        Assert.True(peers.Any(rig.IsRecorder), $"no recorder among the playback stream's peers after 3 s: {described}; one announced {announcedAfterMs} ms later (-1: not within 5 s); muted {muted}, wrong links {report.WrongSinkLinks}; events: {rig.Events}");
        Assert.True(peak > ClickGenerator.Height / 2, $"the monitor's peak {peak} over {bytes} B; events: {rig.Events}");
        Assert.Equal(0, report.WrongSinkLinks);
        Assert.False(muted, $"muted with a monitor attached; events: {rig.Events}");
        Assert.Empty(rig.Foreign);
        Assert.Equal(0, rig.Bystander.Count);
    }

    /// <summary>
    /// A recorder and a link by hand to another sink at once: the other sink is still muted at once and cut (the recorder
    /// does not excuse it), and the sound comes back on the chosen sink only.
    /// </summary>
    [Fact]
    public void A_recorder_does_not_excuse_a_link_to_another_sink()
    {
        var pwLink = Tools.Which("pw-link");
        if (pwLink is null)
        {
            Assert.Skip("needs pw-link");
        }

        using var rig = new Rig("both");
        Thread.Sleep(1500);
        var output = $"fmp-test-delay-{rig.Id}-playback";
        using var recorder = new ClickRecorder(rig.Test, $"fmp-test-share-{rig.Id}", output, captureSink: false);
        _ = recorder.Connect(capture: true);
        LinkByHandIfNeeded(rig, output, $"fmp-test-share-{rig.Id}");
        Thread.Sleep(1000);
        Assert.Equal(0, rig.Delay.Report().WrongSinkLinks);
        Assert.Equal(0, Tools.Run(pwLink!, [$"{output}:output_FL", $"{rig.C}:playback_FL"]).ExitCode);
        Thread.Sleep(3000);
        Assert.Contains($"linked to another node than {rig.B}", rig.Events, StringComparison.Ordinal);
        Assert.True(rig.Delay.Report().WrongSinkLinks >= 1);
        var b = rig.Node(rig.B)!.Id;
        Assert.Contains(b, rig.PlaybackPeers());
        Assert.All(rig.PlaybackPeers(), p => Assert.True(p == b || rig.IsRecorder(p), $"{p} fed"));
        Assert.True(rig.Bystander.Count <= 1, $"{rig.Bystander.Count} clicks reached the other sink");
    }

    /// <summary>The guard's rule, without a server: chosen sink, other output devices, recorders, nodes not announced.</summary>
    [Fact]
    public void The_playback_peers_are_classified_by_what_they_are()
    {
        const uint chosen = 40;
        static PipeWireAudioDelay.PlaybackRoute Of(params (uint Id, bool Known, string Name, string? MediaClass)[] peers) => PipeWireAudioDelay.ClassifyPlaybackPeers(peers, chosen);

        var alone = Of((40, true, "headset", "Audio/Sink"));
        Assert.True(alone.ToChosen);
        Assert.Null(alone.Foreign);

        var shared = Of((40, true, "headset", "Audio/Sink"), (77, true, "discord_capture", "Stream/Input/Audio"));
        Assert.True(shared.ToChosen);
        Assert.Null(shared.Foreign);
        Assert.Single(shared.Recorders);

        Assert.NotNull(Of((40, true, "headset", "Audio/Sink"), (41, true, "speakers", "Audio/Sink")).Foreign);
        Assert.NotNull(Of((40, true, "headset", "Audio/Sink"), (42, true, "hdmi", "Audio/Duplex")).Foreign);
        Assert.NotNull(Of((40, true, "headset", "Audio/Sink"), (43, true, "eq", "Audio/Sink/Virtual")).Foreign);
        Assert.NotNull(Of((40, true, "headset", "Audio/Sink"), (44, false, "node 44", null)).Foreign);
        Assert.NotNull(Of((40, true, "headset", "Audio/Sink"), (77, true, "rec", "Stream/Input/Audio"), (41, true, "speakers", "Audio/Sink")).Foreign);

        var recorderOnly = Of((77, true, "rec", "Stream/Input/Audio"));
        Assert.False(recorderOnly.ToChosen);
        Assert.Null(recorderOnly.Foreign);

        // The chosen sink gone (PW_ID_ANY): a sink of the same id is not the chosen one.
        Assert.NotNull(PipeWireAudioDelay.ClassifyPlaybackPeers([(40, true, "headset", "Audio/Sink")], PwDefs.IdAny).Foreign);
    }

    /// <summary>
    /// The recorder targets the playback node; when the session manager does not link a capture stream to a stream, link
    /// its first channel by hand (what Discord's client does itself).
    /// </summary>
    private static void LinkByHandIfNeeded(Rig rig, string output, string recorder)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && !rig.PlaybackPeers().Exists(rig.IsRecorder))
        {
            Thread.Sleep(50);
        }

        if (!rig.PlaybackPeers().Exists(rig.IsRecorder) && Tools.Which("pw-link") is { } pwLink)
        {
            _ = Tools.Run(pwLink, [$"{output}:output_FL", $"{recorder}:input_FL"]);
            _ = Tools.Run(pwLink, [$"{output}:output_FR", $"{recorder}:input_FR"]);
        }
    }
}
