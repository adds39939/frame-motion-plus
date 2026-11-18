using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// A model of a Wayland compositor's FIFO for tests (<see cref="PlaybackHooks.SimulatedDisplay"/>): the present thread runs
/// its window logic against an offscreen target whose first-pixel reports come from here, on the real clock. Refreshes are
/// <c>anchor + k·P</c>; the compositor latches a refresh <see cref="LatchNs"/> before it and shows the oldest present handed
/// over by then (one per refresh, in order). It can miss refreshes (<see cref="SkipsRefresh"/>: nothing is latched there,
/// as KWin does under load), and then give the refresh back the way KWin does: at a later latch with two presents ready it
/// shows the newer and discards the older (its report comes without a time). A present's hand-over is its submission plus
/// <see cref="HandoverLagNs"/>. Reports come <see cref="ReportDelayNs"/> after the first pixel, and a present counts as
/// complete (for the presenter's in-flight cap) once its report has come. Present thread only; allocation-free.
/// </summary>
internal sealed class SimulatedCompositor
{
    private const int Capacity = 256;
    private readonly long[] _handover = new long[Capacity];
    private readonly long[] _report = new long[Capacity];
    private readonly long[] _shown = new long[Capacity];
    private readonly byte[] _state = new byte[Capacity];
    private ulong _oldestPending = 1;
    private ulong _newestSeen;
    private long _anchorNs = long.MinValue;
    private long _nextRefresh;
    private int _owed;
    private int _latchesSinceSkip;

    public SimulatedCompositor(FrameRate rate)
    {
        Rate = rate;
        PeriodNs = rate.PeriodNs;
    }

    public FrameRate Rate { get; }

    public double PeriodNs { get; }

    /// <summary>How long before its refresh the compositor takes a present, ns, by refresh time (default 5 ms).</summary>
    public Func<long, long> LatchNs { get; init; } = static _ => 5 * Nanoseconds.PerMillisecond;

    /// <summary>Refresh time → the compositor misses that refresh (latches nothing for it).</summary>
    public Func<long, bool> SkipsRefresh { get; init; } = static _ => false;

    /// <summary>After a missed refresh, the compositor gives it back by discarding one of two ready presents (KWin); false: it stays behind.</summary>
    public bool GivesBack { get; init; } = true;

    /// <summary>The latch after a missed refresh at which it gives the refresh back (KWin: the second or third).</summary>
    public int GiveBackAtLatch { get; init; } = 2;

    /// <summary>
    /// Variable refresh (a fullscreen window with VRR on): a present is shown <see cref="VrrDelayNs"/> after its hand-over,
    /// but no sooner than the shortest refresh period after the one before (in order, one per refresh).
    /// </summary>
    public bool Vrr { get; init; }

    /// <summary>Variable refresh: hand-over to first pixel, ns (measured here: 0.6 to 0.9 ms).</summary>
    public long VrrDelayNs { get; init; } = 750 * Nanoseconds.PerMicrosecond;

    private long _vrrLastShown = long.MinValue;

    public long ReportDelayNs { get; init; } = Nanoseconds.PerMillisecond;

    /// <summary>First pixel after the refresh (scan-out of the first line), ns.</summary>
    public long FirstPixelNs { get; init; } = 160 * Nanoseconds.PerMicrosecond;

    /// <summary>Refreshes missed, presents discarded, presents shown.</summary>
    public int Skipped { get; private set; }

    public int Discarded { get; private set; }

    public int Shown { get; private set; }

    /// <summary>The first refresh is a period from the first present.</summary>
    public long RefreshTime(long k) => _anchorNs + (long)Math.Round(k * PeriodNs);

    /// <summary>
    /// The target's present-time source: 0 while the report has not come, the first pixel's time, or −1 for a present that
    /// was discarded (its report came without a time).
    /// </summary>
    /// <summary>The target's submission hook: the present is handed over now (its GPU work, the ring write's copy or a blend
    /// of a small test frame, finishes well within a millisecond).</summary>
    public void OnSubmit(ulong presentId)
    {
        var now = Monotonic.NowNs;
        if (_anchorNs == long.MinValue)
        {
            _anchorNs = now - (long)(PeriodNs / 2);
            _nextRefresh = 1;
        }

        Advance(now);
        for (var id = _newestSeen + 1; id <= presentId; id++)
        {
            var i = (int)(id % Capacity);
            _handover[i] = now + HandoverLagNs;
            _state[i] = 0;
            _report[i] = 0;
            _shown[i] = 0;
        }

        _newestSeen = Math.Max(_newestSeen, presentId);
    }

    /// <summary>Added to the submission time as the hand-over (GPU work and the compositor seeing the buffer ready), ns.</summary>
    public long HandoverLagNs { get; init; } = 200 * Nanoseconds.PerMicrosecond;

    /// <summary>
    /// The target's present-time source: 0 while the report has not come, the first pixel's time, or −1 for a present that
    /// was discarded (its report came without a time).
    /// </summary>
    public long TimeOf(ulong presentId)
    {
        var now = Monotonic.NowNs;
        if (presentId > _newestSeen)
        {
            OnSubmit(presentId);
        }

        Advance(now);
        var j = (int)(presentId % Capacity);
        return _state[j] == 0 || now < _report[j] ? 0 : _shown[j];
    }

    private void Advance(long now)
    {
        if (Vrr)
        {
            // Each present in order, its first pixel the delay after its hand-over and a refresh period after the last.
            while (_oldestPending <= _newestSeen)
            {
                var i = (int)(_oldestPending % Capacity);
                var shown = Math.Max(_handover[i] + VrrDelayNs, _vrrLastShown == long.MinValue ? long.MinValue : _vrrLastShown + (long)PeriodNs);
                if (shown - VrrDelayNs > now)
                {
                    return; // decided once its time has come (a newer present cannot change it under FIFO)
                }

                _state[i] = 1;
                _shown[i] = shown;
                _report[i] = shown + ReportDelayNs;
                _vrrLastShown = shown;
                _oldestPending++;
                Shown++;
            }

            return;
        }

        while (true)
        {
            var refresh = RefreshTime(_nextRefresh);
            var latch = refresh - LatchNs(refresh);
            if (latch > now)
            {
                return;
            }

            _nextRefresh++;
            if (SkipsRefresh(refresh))
            {
                Skipped++;
                _owed++;
                _latchesSinceSkip = 0;
                continue;
            }

            // The presents handed over by this latch, oldest first.
            var ready = 0;
            for (var id = _oldestPending; id <= _newestSeen && _handover[(int)(id % Capacity)] <= latch; id++)
            {
                ready++;
            }

            if (ready == 0)
            {
                continue;
            }

            _latchesSinceSkip++;
            if (_owed > 0 && GivesBack && _latchesSinceSkip >= GiveBackAtLatch && ready >= 2)
            {
                var discarded = (int)(_oldestPending % Capacity);
                _state[discarded] = 2;
                _shown[discarded] = -1;
                _report[discarded] = refresh + FirstPixelNs + ReportDelayNs;
                _oldestPending++;
                _owed--;
                Discarded++;
            }

            var shown = (int)(_oldestPending % Capacity);
            _state[shown] = 1;
            _shown[shown] = refresh + FirstPixelNs;
            _report[shown] = refresh + FirstPixelNs + ReportDelayNs;
            _oldestPending++;
            Shown++;
        }
    }
}
