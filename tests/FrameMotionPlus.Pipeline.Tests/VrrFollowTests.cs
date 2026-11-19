using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// <see cref="VrrFollowDetector"/> on synthetic shows (no GPU): a display that follows the presents, then shows on its
/// 119.88 Hz grid for 2 s, then follows again; the even cadence of 30 → 60 hides the grid from the intervals, the delay from
/// hand-over to first pixel shows it.
/// </summary>
public class VrrFollowTests
{
    private const double Period = 1e9 * 1001 / 120000; // 119.88 Hz
    private const long FollowDelay = 900_000;

    /// <summary>Feeds <paramref name="seconds"/> of slots at <paramref name="slotNs"/>; <paramref name="grid"/> says, by
    /// hand-over time, whether the display shows on its grid (first pixel at the first refresh after hand-over + 1.5 ms).</summary>
    private static VrrFollowSummary Run(double seconds, double slotNs, Func<long, bool> grid, VrrFollowDetector? detector = null, Func<long, long>? delay = null, long originOffset = 0)
    {
        detector ??= new VrrFollowDetector(FollowDelay);
        var origin = 1_000_000_000L + originOffset;
        var count = (long)(seconds * 1e9 / slotNs);
        long last = 0;
        for (long k = 0; k < count; k++)
        {
            var target = origin + (long)(k * slotNs);
            var handover = target - FollowDelay + (long)(((k * 7919) % 13) * 5_000); // up to 60 µs of wake jitter
            long shown;
            if (grid(handover - origin))
            {
                var earliest = handover + 1_500_000;
                shown = (long)(Math.Ceiling(earliest / Period) * Period) + 160_000;
            }
            else
            {
                shown = handover + (delay?.Invoke(handover) ?? FollowDelay);
            }

            shown = Math.Max(shown, last + (long)Period);
            last = shown;
            detector.OnShown(handover, shown, Period, shown - target > Period / 2);
        }

        return detector.Finish(last);
    }

    [Fact]
    public void Follow_then_two_seconds_on_the_grid_then_follow_is_one_episode_of_about_two_seconds()
    {
        var s = Run(8, 1e9 / 60, t => t >= 3_000_000_000 && t < 5_000_000_000); // 30.000 → 60: the phase drifts
        Assert.Equal(1, s.Episodes);
        Assert.InRange(s.TotalNs / 1e9, 1.5, 2.3);
        Assert.Equal(s.TotalNs, s.LongestNs);
        Assert.InRange(s.Log[0].StartNs / 1e9, 3.9, 4.3); // 1 s after the origin, 3 s in
        Assert.True(s.LateInside > 10, $"{s.LateInside} late inside");
        Assert.Equal(0, s.LateOutside);
        Assert.Equal("lost 1x for a total of " + (s.TotalNs / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s (the longest " + (s.LongestNs / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)", s.Describe());
    }

    [Fact]
    public void A_following_display_with_a_varying_delay_and_single_late_shows_has_no_episode()
    {
        // 0.4 to 3.1 ms after hand-over (the owner's two-hour run), and a 6 ms stall every 2 s.
        var s = Run(20, 1e9 * 1001 / 60000, _ => false, delay: h => (h / 16_000_000) % 120 == 0 ? 6_000_000 : 400_000 + (long)(unchecked((ulong)h * 0x9E3779B97F4A7C15UL) % 2_700_000UL));
        Assert.Equal(0, s.Episodes);
        Assert.Equal("never lost", s.Describe());
    }

    [Fact]
    public void An_exact_multiple_on_the_grid_is_seen_when_its_phase_makes_shows_late_after_hand_over()
    {
        // 29.97 → 59.94 on 119.88 Hz: two refreshes per slot exactly, the phase fixed (each first pixel about 7.4 ms after
        // its hand-over); on the grid all the way.
        var s = Run(6, 2 * Period, _ => true, originOffset: 3_000_000);
        Assert.Equal(1, s.Episodes);
        Assert.True(s.TotalNs > 4_500_000_000, $"{s.TotalNs / 1e9:0.0} s");
    }

    [Fact]
    public void Episodes_flapping_are_each_counted_and_the_longest_kept()
    {
        var s = Run(14, 1e9 / 60, t => (t >= 2_000_000_000 && t < 3_500_000_000) || (t >= 7_000_000_000 && t < 10_000_000_000));
        Assert.Equal(2, s.Episodes);
        Assert.InRange(s.LongestNs / 1e9, 2.4, 3.3);
        Assert.Equal(2, s.Log.Count);
    }

    /// <summary>The present loop's adaptive lead, as <c>UpdateVrrLead</c> keeps it: the median of the last 256 delays from
    /// hand-over to first pixel, recomputed every 64 shows, moved at most 0.1 ms at a time, within 0.3 to 2 ms.</summary>
    private sealed class LeadModel(long start)
    {
        private readonly long[] _delays = new long[256];
        private int _count;
        private int _head;

        public long LeadNs { get; private set; } = start;

        public void Add(long delayNs)
        {
            _delays[_head] = delayNs;
            _head = (_head + 1) % _delays.Length;
            _count = Math.Min(_count + 1, _delays.Length);
            if (_head % 64 == 0)
            {
                var sorted = _delays.Take(_count).Order().ToArray();
                var median = sorted[sorted.Length / 2];
                LeadNs = Math.Clamp(Math.Clamp(median, LeadNs - 100_000, LeadNs + 100_000), 300_000, 2_000_000);
            }
        }
    }

    /// <summary>DP-3 at 240.016 Hz (cutoff max(1 ms, a quarter refresh) = 1.04 ms), 29.97 → 59.94 slots; the display follows
    /// with a delay that <paramref name="delay"/> gives, or shows on its grid while <paramref name="grid"/>.</summary>
    private static VrrFollowSummary RunDp3(double seconds, Func<long, long> delay, Func<long, bool> grid, bool adaptive)
    {
        const double period = 1e9 / 240.016;
        var slot = 1e9 * 1001 / 60000;
        var detector = new VrrFollowDetector(930_000);
        var lead = new LeadModel(930_000);
        var origin = 1_000_000_000L;
        long last = 0;
        for (long k = 0; k < (long)(seconds * 1e9 / slot); k++)
        {
            var target = origin + (long)(k * slot);
            var handover = target - lead.LeadNs;
            var shown = grid(handover - origin)
                ? (long)(Math.Ceiling((handover + 900_000) / period) * period) + 120_000
                : handover + delay(handover - origin);
            shown = Math.Max(shown, last + (long)period);
            last = shown;
            detector.OnShown(handover, shown, period, shown - target > period / 2, adaptive ? lead.LeadNs : 0);
            lead.Add(shown - handover);
        }

        return detector.Finish(last);
    }

    [Fact]
    public void A_steady_rise_of_the_delay_on_a_following_display_is_no_loss_against_the_adaptive_lead()
    {
        // 0.93 ms rising to 2.3 ms over 30 s (the TV's ceiling saw 1.3 to 1.7 ms against the probe's 0.93): against the
        // probe's fixed delay the cutoff (1.04 ms on DP-3) is crossed and a loss announced; against the lead it is not.
        static long Rising(long t) => 930_000 + Math.Min(1_370_000, t * 1_370_000 / 30_000_000_000L) + ((t / 16_683_000) % 7 * 30_000);
        Assert.True(RunDp3(40, Rising, _ => false, adaptive: false).Episodes >= 1);
        Assert.Equal(0, RunDp3(40, Rising, _ => false, adaptive: true).Episodes);

        // A real loss is still one episode, with the lead driven up by it (to its 2 ms bound) while it lasts: on the grid
        // from 5 to 25 s, following again after.
        var lost = RunDp3(35, _ => 930_000, t => t >= 5_000_000_000 && t < 25_000_000_000, adaptive: true);
        Assert.Equal(1, lost.Episodes);
        Assert.InRange(lost.TotalNs / 1e9, 18, 21);
    }

    [Fact]
    public void An_episode_open_at_the_end_is_closed_there()
    {
        var detector = new VrrFollowDetector(FollowDelay);
        var s = Run(5, 1e9 / 60, t => t >= 2_000_000_000, detector);
        Assert.Equal(1, s.Episodes);
        Assert.InRange(s.TotalNs / 1e9, 2.2, 3.1);
        Assert.False(detector.IsLost);
    }
}
