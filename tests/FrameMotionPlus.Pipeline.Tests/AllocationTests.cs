using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The hot path allocates nothing: over the measured window (after the pre-roll and the warm-up, which run every per-slot
/// path first), the pacing, generator, present and control threads allocate exactly 0 bytes and the process makes no
/// collection. Each thread's count is its own (<see cref="ThreadMeter"/>); an allocation injected into any one thread's
/// loop shows up in that thread's count and fails the run, so the measurement cannot pass by measuring nothing.
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class AllocationTests
{
    private static PlaybackOptions Options(GeneratorKind generator) => new()
    {
        Path = Clips.Get("c60-long"),
        Generator = generator,
        Multiplier = 4,
        Display = DisplayKind.Offscreen,
        Clock = PlaybackClock.Real,
        OffscreenRate = Core.Time.FrameRate.FromHz(240),
        DurationNs = 4_000_000_000,
        WarmupNs = 1_500_000_000,
        Loop = true,
    };

    [Theory]
    [InlineData(GeneratorKind.Blend)]
    [InlineData(GeneratorKind.None)]
    public void The_pipeline_threads_allocate_nothing_in_steady_state(GeneratorKind generator)
    {
        var cuda = Hardware.RequireGpu();
        var report = Playback.Run(cuda, Options(generator));
        AssertClean(report);
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void With_vfg_the_pipeline_threads_allocate_nothing_in_steady_state()
    {
        var cuda = Hardware.RequireVfx();
        var report = Playback.Run(cuda, Options(GeneratorKind.Vfg) with { Multiplier = 2 });
        AssertClean(report);
        Assert.True(report.GeneratedWrites > 100);
    }

    /// <summary>A 64-byte allocation per loop turn of one thread is caught in that thread's count and fails the run.</summary>
    [Theory]
    [InlineData("pacing")]
    [InlineData("generator")]
    [InlineData("present")]
    public void An_allocation_on_a_pipeline_thread_is_caught(string thread)
    {
        var cuda = Hardware.RequireGpu();
        var sink = new object[1];
        var hooks = new PlaybackHooks
        {
            PacingTurn = thread == "pacing" ? () => sink[0] = new byte[64] : null,
            BeforeWrite = thread == "generator" ? _ => sink[0] = new byte[64] : null,
            BeforePresent = thread == "present" ? _ => sink[0] = new byte[64] : null,
        };
        var report = Playback.Run(cuda, Options(GeneratorKind.Blend) with { DurationNs = 3_000_000_000, WarmupNs = 1_000_000_000 }, hooks);
        var cost = thread switch
        {
            "pacing" => report.Pacing,
            "generator" => report.Writer,
            _ => report.Presenter,
        };
        Assert.True(cost.AllocatedBytes >= 64 * 100, $"the {thread} thread's count is {cost.AllocatedBytes} B");
        Assert.Contains(report.Failures(), f => f.Contains($"the {thread} thread allocated", StringComparison.Ordinal));
        foreach (var (name, other) in report.Threads)
        {
            if (name != thread && name != "control")
            {
                Assert.Equal(0, other.AllocatedBytes);
            }
        }
    }

    private static void AssertClean(PlaybackReport report)
    {
        Assert.Empty(report.Failures());
        Assert.True(report.Measured);
        Assert.True(report.Statistics.Slots > 500, $"only {report.Statistics.Slots} slots measured");
        foreach (var (name, cost) in report.Threads)
        {
            Assert.True(cost.AllocatedBytes == 0, $"the {name} thread allocated {cost.AllocatedBytes} B");
            Assert.True(cost.Turns > 0, $"the {name} thread did not run in the measured window");
        }

        Assert.Equal(0, report.SourceStatistics.DecodeThreadAllocatedBytes);
        Assert.Equal(0, report.MeasuredGc.Gen0Collections);
        Assert.Equal(0, report.MeasuredGc.Gen1Collections);
        Assert.Equal(0, report.MeasuredGc.Gen2Collections);
    }
}
