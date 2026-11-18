using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// The display's refresh grid as present feedback shows it: the period from the output's refresh rate, the phase from
/// the times presents were reported shown. A report comes some time after the refresh that showed the present (the
/// compositor's event, the driver, the present thread's poll), never before it, so the phase is the lower envelope of
/// the reports: the earliest a report lands relative to the grid. Times are ns on <c>CLOCK_MONOTONIC</c>; the grid is
/// "refreshes as feedback sees them", which is the true vblank plus the smallest feedback delay (feedback alone cannot
/// separate the two). Allocation-free; one thread.
/// </summary>
internal sealed class DisplayGrid
{
    /// <summary>Shows over which the lower envelope is taken before the phase follows it up (later) again.</summary>
    public const int Window = 32;

    private double _period;
    private long _anchor;
    private bool _hasPhase;
    private long _windowMin = long.MaxValue;
    private int _windowCount;

    public DisplayGrid(FrameRate rate)
    {
        Reset(rate);
    }

    public FrameRate Rate { get; private set; }

    public double PeriodNs => _period;

    /// <summary>True once a show has been reported since the last <see cref="Reset"/>.</summary>
    public bool HasPhase => _hasPhase;

    /// <summary>A refresh instant on the grid (meaningful once <see cref="HasPhase"/>).</summary>
    public long AnchorNs => _anchor;

    /// <summary>Phase corrections applied (diagnostics).</summary>
    public long Corrections { get; private set; }

    /// <summary>A new rate (the window moved to another output, or the rate became known): the phase is learnt again.</summary>
    public void Reset(FrameRate rate)
    {
        if (!rate.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "A refresh rate is required.");
        }

        Rate = rate;
        _period = rate.PeriodNs;
        _hasPhase = false;
        _windowMin = long.MaxValue;
        _windowCount = 0;
    }

    /// <summary>Takes a report that a present was shown at (reported at) <paramref name="shownNs"/>.</summary>
    public void AddShow(long shownNs)
    {
        if (!_hasPhase)
        {
            _anchor = shownNs;
            _hasPhase = true;
            return;
        }

        var residual = Residual(shownNs);
        if (residual < 0)
        {
            // Earlier than the grid: the grid was late (or the period is slightly long). Follow at once: no report can
            // come before its refresh.
            _anchor += residual;
            Corrections++;
            _windowMin = long.MaxValue;
            _windowCount = 0;
            return;
        }

        _windowMin = Math.Min(_windowMin, residual);
        if (++_windowCount >= Window)
        {
            // The earliest report of the window is the grid now (follows a period that is slightly short, or a phase that
            // moved later).
            if (_windowMin > 0)
            {
                _anchor += _windowMin;
                Corrections++;
            }

            _windowMin = long.MaxValue;
            _windowCount = 0;
        }
    }

    /// <summary>The refresh index nearest to <paramref name="timeNs"/> (index 0 is <see cref="AnchorNs"/>).</summary>
    public long IndexOf(long timeNs) => (long)Math.Round((timeNs - _anchor) / _period);

    public long TimeOf(long index) => _anchor + (long)Math.Round(index * _period);

    /// <summary>Signed distance of <paramref name="timeNs"/> from its nearest refresh, in [−P/2, P/2).</summary>
    public long Residual(long timeNs) => timeNs - TimeOf(IndexOf(timeNs));

    /// <summary>The first refresh at or after <paramref name="timeNs"/>.</summary>
    public long NextAtOrAfter(long timeNs)
    {
        var index = (long)Math.Ceiling((timeNs - _anchor) / _period);
        var t = TimeOf(index);
        return t < timeNs ? TimeOf(index + 1) : t;
    }

    /// <summary>
    /// The refresh after the one nearest to <paramref name="refreshNs"/>, on the grid as it is now. A chain of predictions
    /// (each present one refresh after the one before it) must step with this rather than add the period: the period is
    /// the output's nominal rate, which differs from the refreshes as <c>CLOCK_MONOTONIC</c> sees them by tens of ppm, so
    /// a chain that adds it drifts by about 0.1 µs a refresh, half a refresh within two minutes at 240 Hz, while the
    /// phase corrections keep the grid itself on the refreshes.
    /// </summary>
    public long NextAfter(long refreshNs) => TimeOf(IndexOf(refreshNs) + 1);

    /// <summary>How many refresh periods <paramref name="laterNs"/> is after <paramref name="earlierNs"/>, rounded.</summary>
    public long PeriodsBetween(long earlierNs, long laterNs) => (long)Math.Round((laterNs - earlierNs) / _period);
}
