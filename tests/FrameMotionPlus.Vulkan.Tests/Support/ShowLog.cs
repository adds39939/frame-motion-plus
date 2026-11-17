using System.Diagnostics;
using System.Globalization;

namespace FrameMotionPlus.Vulkan.Tests.Support;

/// <summary>
/// When each present's GPU work (slot copy and output pass) was seen finished and when its feedback reported it shown
/// (test clock), to tell apart, for a window that showed too few frames, a compositor that skipped refreshes from a
/// presenter that fed it too little or reports the wrong refresh rate. A present counts as handed to the compositor
/// once its GPU work is done, not when it was submitted: until then the compositor cannot show it. Completion is
/// polled by the loop, so it is seen at most one loop turn (a few microseconds) late. Preallocated; recording
/// allocates nothing.
/// </summary>
internal sealed class ShowLog(int capacity = 4096)
{
    private readonly long[] _submitSlot = new long[capacity];
    private readonly ulong[] _submitFrame = new ulong[capacity];
    private readonly long[] _doneTime = new long[capacity];
    private readonly long[] _shownSlot = new long[capacity];
    private readonly long[] _shownTime = new long[capacity];
    private int _submits;
    private int _done;
    private int _shows;

    /// <summary>A present of <paramref name="slotIndex"/> was submitted as the presenter's frame <paramref name="frame"/>.</summary>
    public void Submitted(long slotIndex, ulong frame)
    {
        if (_submits < _submitSlot.Length)
        {
            _submitSlot[_submits] = slotIndex;
            _submitFrame[_submits++] = frame;
        }
    }

    /// <summary>The presenter's frames up to <paramref name="framesDone"/> have finished their GPU work (as of now).</summary>
    public void GpuDone(ulong framesDone)
    {
        var now = Stopwatch.GetTimestamp();
        while (_done < _submits && _submitFrame[_done] <= framesDone)
        {
            _doneTime[_done++] = now;
        }
    }

    public void Shown(long slotIndex)
    {
        if (_shows < _shownSlot.Length)
        {
            _shownSlot[_shows] = slotIndex;
            _shownTime[_shows++] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Judges the logged window against a display of <paramref name="refreshHz"/>. Each interval between two shows is
    /// <em>fed</em> when the later present's GPU work had finished before the earlier one was reported shown: the
    /// compositor had it ready at every refresh in between, so refreshes skipped there are the compositor's. The window is
    /// compositor-limited when fewer than 85% but more than 55% of the refreshes showed a frame, at most 5% were skipped
    /// while the presenter had nothing queued, and the intervals do not sit steadily on one value other than a period
    /// (10th to 90th percentile within a quarter period, median off one period by more than 15%). A presenter that
    /// reports a rate above the output's fails either the 55% floor (an output at half the rate or less cannot show
    /// more) or the steadiness test (an output steadily at another rate); so does a compositor that steadily runs the
    /// output at another rate, which feedback cannot tell from it. A compositor that skips refreshes irregularly (or
    /// reports shows in bursts) passes all three.
    /// </summary>
    public ShowVerdict Judge(double refreshHz)
    {
        var period = Stopwatch.Frequency / refreshHz;
        int intervals = 0, fedMisses = 0, starvedMisses = 0;
        var multiples = new int[8];
        var ratios = new List<double>();
        var s = 0;
        for (var k = 0; k + 1 < _shows; k++)
        {
            // The submission of the later shown present (slots increase in both logs); its GPU work must have been seen done.
            while (s < _submits && _submitSlot[s] < _shownSlot[k + 1])
            {
                s++;
            }

            if (s >= _done || _submitSlot[s] != _shownSlot[k + 1])
            {
                continue;
            }

            var ratio = (_shownTime[k + 1] - _shownTime[k]) / period;
            var missed = Math.Max(0, (int)Math.Round(ratio) - 1);
            if (_doneTime[s] < _shownTime[k])
            {
                fedMisses += missed;
            }
            else
            {
                starvedMisses += missed;
            }

            var m = (int)Math.Round(ratio);
            if (m is > 0 and < 8 && Math.Abs(ratio - m) <= 0.1 * m)
            {
                multiples[m]++;
            }

            ratios.Add(ratio);
            intervals++;
        }

        ratios.Sort();
        string Quantile(double q) => ratios.Count == 0 ? "-" : ratios[(int)(q * (ratios.Count - 1))].ToString("F2", CultureInfo.InvariantCulture);

        var seconds = _shows > 1 ? (_shownTime[_shows - 1] - _shownTime[0]) / (double)Stopwatch.Frequency : 0;
        var shownRate = seconds > 0 ? (_shows - 1) / seconds : 0;
        var refreshes = seconds * refreshHz;
        var steady = ratios.Count > 0
            && ratios[(int)(0.9 * (ratios.Count - 1))] - ratios[(int)(0.1 * (ratios.Count - 1))] < 0.25
            && Math.Abs(ratios[(ratios.Count - 1) / 2] - 1) > 0.15;
        var limited = intervals >= 10
            && shownRate < 0.85 * refreshHz
            && shownRate > 0.55 * refreshHz
            && starvedMisses <= 0.05 * refreshes
            && !steady;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{shownRate:F1} frames/s shown on a {refreshHz:F2} Hz display over {intervals} intervals: {fedMisses} refreshes skipped with a present " +
            $"queued, {starvedMisses} with none queued, {(steady ? "steady at another rate" : "not steady at another rate")}; intervals in periods " +
            $"min {Quantile(0)}, 10% {Quantile(0.1)}, median {Quantile(0.5)}, 90% {Quantile(0.9)}, max {Quantile(1)}; " +
            $"near 1: {multiples[1]}, near 2: {multiples[2]}, near 3: {multiples[3]}");
        return new ShowVerdict(limited, shownRate, text);
    }
}

/// <param name="CompositorLimited">The compositor, not the presenter, showed too few frames (see <see cref="ShowLog.Judge"/>).</param>
internal readonly record struct ShowVerdict(bool CompositorLimited, double ShownRate, string Description);
