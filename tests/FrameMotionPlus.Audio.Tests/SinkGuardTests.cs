using System.Globalization;
using FrameMotionPlus.Audio.PipeWire;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Audio.Tests.Support;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// Where the sound may go, against null sinks the test creates (nothing of the machine's devices is used): the playback
/// stream is pinned to the sink chosen at the start and plays nowhere else. The named sink removed mid-run: muted, linked
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

        /// <summary>Nodes other than B that the playback node was ever seen feeding.</summary>
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
                    if (peer != b)
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
}
