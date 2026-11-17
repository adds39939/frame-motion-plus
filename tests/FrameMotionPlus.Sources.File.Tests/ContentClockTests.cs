using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// <see cref="ContentClock"/> on its own (no GPU, no FFmpeg): a 1/90000 time base (MPEG-TS) at 30 fps, 3000 ticks per
/// frame. Isolated collisions are repaired by 1 ns; anything else that does not advance is a discontinuity that continues
/// one frame interval on; a jump forward of more than 10 s is a discontinuity that keeps its time, or with a container
/// whose clock may jump (MPEG-TS) continues one interval on. Times are compared to within a few ns (a frame is
/// 33,333,333.3 ns; the clock scales ticks exactly and truncates).
/// </summary>
public class ContentClockTests
{
    private const long Tick = 3000;
    private const long Interval = 33_333_333;

    private static ContentClock Clock(bool collapseForwardJumps = true) => new(1, 90_000, 0, Interval, 0, collapseForwardJumps);

    private static (long Content, bool Jump, bool Repaired) Next(ContentClock clock, long ticks)
    {
        var content = clock.Next(ticks, Tick, out _, out var jump);
        return (content, jump, clock.LastWasRepaired);
    }

    private static void Near(long expected, long actual) => Assert.InRange(actual, expected - 20, expected + 20);

    [Fact]
    public void An_isolated_collision_is_moved_one_nanosecond_on_and_the_next_frame_is_on_time()
    {
        var clock = Clock();
        Next(clock, 0);
        var b = Next(clock, Tick);
        var dup = Next(clock, Tick);
        var c = Next(clock, 2 * Tick);
        Assert.Equal((b.Content + 1, false, true), dup);
        Assert.False(c.Jump || c.Repaired);
        Near(2 * Interval, c.Content);
        Assert.Equal(1, clock.Repairs);
        Assert.Equal(0, clock.Discontinuities);
    }

    [Fact]
    public void A_step_back_of_less_than_a_frame_is_a_collision()
    {
        var clock = Clock();
        Next(clock, 0);
        var b = Next(clock, 10 * Tick);
        var back = Next(clock, (10 * Tick) - (Tick / 2));
        Assert.Equal((b.Content + 1, false, true), back);
        Assert.Equal(0, clock.Discontinuities);
    }

    [Fact]
    public void A_second_frame_in_a_row_that_does_not_advance_is_a_discontinuity_not_another_repair()
    {
        var clock = Clock();
        Next(clock, 0);
        Next(clock, Tick);
        var first = Next(clock, Tick);
        var second = Next(clock, Tick);
        Assert.True(first.Repaired);
        Assert.Equal((first.Content + Interval, true, false), second);
        var after = Next(clock, 2 * Tick);
        Near(second.Content + Interval, after.Content); // on from the new offset, at the stream's pace
        Assert.Equal(1, clock.Repairs);
        Assert.Equal(1, clock.Discontinuities);
    }

    [Fact]
    public void A_step_back_of_a_frame_or_more_is_a_discontinuity_that_continues_one_interval_on()
    {
        // Concatenated recordings: the second starts again at the same 1.4 s as the first.
        var clock = Clock();
        long last = 0;
        for (var i = 0; i < 30; i++)
        {
            last = Next(clock, 126_000 + (i * Tick)).Content;
        }

        var jump = Next(clock, 126_000);
        Assert.Equal((last + Interval, true, false), jump);
        for (var i = 1; i < 30; i++)
        {
            var next = Next(clock, 126_000 + (i * Tick));
            Assert.False(next.Jump || next.Repaired);
            Near(jump.Content + (i * Interval), next.Content);
        }

        Assert.Equal(0, clock.Repairs);
        Assert.Equal(1, clock.Discontinuities);
    }

    /// <summary>MPEG-TS (a clock that may jump): a gap of more than 10 s is marked and collapsed to one interval.</summary>
    [Fact]
    public void A_jump_forward_beyond_ten_seconds_is_marked_and_collapsed_where_the_clock_may_jump()
    {
        var clock = Clock(collapseForwardJumps: true);
        Next(clock, 0);
        var gap = Next(clock, 5 * 90_000); // a long frame of a variable-rate stream: kept, unmarked
        Assert.Equal((5_000_000_000, false, false), gap);
        var nearly = Next(clock, (5 * 90_000) + (10 * 90_000)); // exactly 10 s on: not beyond the threshold
        Assert.Equal((15_000_000_000, false, false), nearly);
        var jump = Next(clock, (15 * 90_000) + (10 * 90_000) + 1);
        Assert.Equal((nearly.Content + Interval, true, false), jump);
        var after = Next(clock, (25 * 90_000) + 1 + Tick);
        Near(jump.Content + Interval, after.Content);
        Assert.Equal(1, clock.Discontinuities);
    }

    /// <summary>
    /// A continuous timeline (MP4, Matroska): a gap of more than 10 s is still marked (nothing may interpolate across it)
    /// but keeps its time: a 12 s still is content and is held for 12 s.
    /// </summary>
    [Fact]
    public void A_jump_forward_beyond_ten_seconds_is_marked_and_kept_on_a_continuous_timeline()
    {
        var clock = Clock(collapseForwardJumps: false);
        Next(clock, 0);
        var jump = Next(clock, 12 * 90_000);
        Assert.Equal((12_000_000_000, true, false), jump);
        var after = Next(clock, (12 * 90_000) + Tick);
        Assert.False(after.Jump);
        Near(12_000_000_000 + Interval, after.Content);
        var back = Next(clock, Tick); // a jump back is always rebased, whatever the container
        Assert.Equal((after.Content + Interval, true, false), back);
        Assert.Equal(2, clock.Discontinuities);
    }

    [Fact]
    public void A_rebase_from_the_start_restarts_untimed_frames_at_the_start()
    {
        var clock = Clock();
        long media;
        long content = 0;
        for (var i = 0; i < 5; i++)
        {
            content = clock.Next(Av.NoPtsValue, 0, out media, out _);
            Near(i * Interval, media);
        }

        Near(5 * Interval, clock.Peek(Av.NoPtsValue, 0));
        clock.Rebase(fromStart: true);
        Assert.Equal(0, clock.Peek(Av.NoPtsValue, 0));
        clock.Skip(Av.NoPtsValue, 0); // decoded on the way to a seek target
        Near(Interval, clock.Peek(Av.NoPtsValue, 0));
        var next = clock.Next(Av.NoPtsValue, 0, out media, out var jump);
        Near(Interval, media);
        Assert.False(jump);
        Assert.Equal(content + Interval, next); // one interval after the last delivered frame
        Assert.Equal(7, clock.Missing);
    }
}
