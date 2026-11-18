using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>What the present thread does with a slot whose present can no longer make its own refresh.</summary>
internal enum CatchUpAction : byte
{
    /// <summary>On time: submit it.</summary>
    Submit = 0,

    /// <summary>A new real frame one refresh late: show it anyway (a refresh late), and give up a later repeat or generated slot instead.</summary>
    SubmitLate = 1,

    /// <summary>Give it up (a write's slot is released).</summary>
    Drop = 2,

    /// <summary>A repeat of the real frame just shown late: its refresh shows that image anyway, so giving it up loses nothing.</summary>
    DropAbsorbed = 3,
}

/// <summary>
/// Catching up after the FIFO fell behind (a present shown late, or a slot that came late): every refresh that is lost must
/// be given back by not presenting something, and what is given up is chosen so a new real frame is never the routine
/// victim: a repeat first (nothing new is lost), then a generated frame; a real frame is shown a refresh late instead, once.
/// Pure.
/// </summary>
internal static class CatchUpPolicy
{
    /// <param name="kind">The present's kind (a ring write or a repeat of the last image).</param>
    /// <param name="slotKind">The scheduler's decision for the slot (real or generated, for a write).</param>
    /// <param name="lateBy">Refreshes it would land after its own refresh if submitted now (0: on time).</param>
    /// <param name="afterLateReal">The present just before it was a real frame submitted a refresh late (it repeats that one).</param>
    /// <param name="lateRealOutstanding">A real frame was submitted late and the FIFO has not been back on time since.</param>
    public static CatchUpAction Decide(PresentKind kind, SlotKind slotKind, long lateBy, bool afterLateReal, bool lateRealOutstanding)
    {
        if (lateBy <= 0 || kind == PresentKind.PreRoll)
        {
            return CatchUpAction.Submit;
        }

        if (kind == PresentKind.Repeat)
        {
            return afterLateReal ? CatchUpAction.DropAbsorbed : CatchUpAction.Drop;
        }

        // A real frame a single refresh late is shown late rather than lost, once: the next repeat or generated slot pays for
        // it. Later than that, or again before the FIFO was back on time, it has to go too (only real frames may remain).
        return slotKind == SlotKind.Real && lateBy == 1 && !lateRealOutstanding ? CatchUpAction.SubmitLate : CatchUpAction.Drop;
    }
}

/// <summary>
/// Whose each missed slot is, from host-clock facts only (when a present was submitted, when the present thread saw its
/// content written, when its first pixel went out). The rule (the plan's): a miss is the pipeline's unless the timestamps
/// show the present was handed over in time for its refresh and the display showed it late. "In time" is measured on this
/// output: the hand-over lead a present needs is the largest of the smallest lead that has made its refresh and the
/// largest that has not, over the last ten seconds, plus a margin; with no present that made its refresh in that time
/// there is no evidence, and the miss is the pipeline's. A late show leaves the FIFO behind by as many refreshes; the slots
/// given up to catch up are the late show's consequence and carry its attribution (and a slot submitted on time clears
/// it). During a change of the output's refresh rate (the window moved) misses are counted on their own. Present thread;
/// allocation-free.
/// </summary>
internal sealed class MissAccounting
{
    public const int Capacity = 4096;
    public const long DefaultWindowNs = 10 * Nanoseconds.PerSecond;
    public const long DefaultMarginNs = 250 * Nanoseconds.PerMicrosecond;

    private readonly long[] _at = new long[Capacity];
    private readonly long[] _lead = new long[Capacity];
    private readonly bool[] _late = new bool[Capacity];
    private readonly long _windowNs;
    private readonly long _marginNs;
    private int _head;
    private int _count;
    private long _rateChangeUntilNs = long.MinValue;
    private int _debt;
    private MissKind _debtCause;

    public MissAccounting(long windowNs = DefaultWindowNs, long marginNs = DefaultMarginNs)
    {
        _windowNs = windowNs;
        _marginNs = marginNs;
    }

    /// <summary>Refreshes the FIFO still has to give back after a late show.</summary>
    public int Debt => _debt;

    /// <summary>Whose the refreshes still owed are (meaningful while <see cref="Debt"/> &gt; 0).</summary>
    public MissKind DebtCause => _debtCause;

    public bool InRateChange(long nowNs) => nowNs < _rateChangeUntilNs;

    /// <summary>
    /// The output's refresh rate changed (the window moved to another output): misses for <paramref name="durationNs"/>
    /// count as the change's; what was learnt about the old output's deadline no longer applies, nor does what was owed.
    /// </summary>
    public void BeginRateChange(long nowNs, long durationNs)
    {
        _rateChangeUntilNs = nowNs + durationNs;
        _count = 0;
        _debt = 0;
    }

    /// <summary>A timed show that made the refresh it was meant for: evidence that its hand-over lead is enough.</summary>
    public void RecordOnTime(long nowNs, long handoverLeadNs)
    {
        if (!InRateChange(nowNs))
        {
            Record(nowNs, handoverLeadNs, late: false);
        }
    }

    /// <summary>
    /// The hand-over lead a present needed, at <paramref name="nowNs"/>, to count as in time: max(smallest lead that made
    /// its refresh, largest that did not) over the window, plus the margin. <see cref="long.MaxValue"/> without a present
    /// that made its refresh in the window (no evidence: every miss is the pipeline's).
    /// </summary>
    public long RequiredLeadNs(long nowNs)
    {
        var okMin = long.MaxValue;
        var lateMax = long.MinValue;
        var from = nowNs - _windowNs;
        for (var i = 0; i < _count; i++)
        {
            var j = (_head - 1 - i + Capacity) % Capacity;
            if (_at[j] < from)
            {
                break;
            }

            if (_late[j])
            {
                lateMax = Math.Max(lateMax, _lead[j]);
            }
            else
            {
                okMin = Math.Min(okMin, _lead[j]);
            }
        }

        return okMin == long.MaxValue ? long.MaxValue : Math.Max(okMin, lateMax) + _marginNs;
    }

    /// <summary>
    /// A present shown <paramref name="refreshes"/> later than the refresh it was meant for (newly late: nothing queued
    /// before it was known to be late). Whose: the display's only if it was handed over at least the required lead before
    /// that refresh. The FIFO now owes that many refreshes, with the same attribution.
    /// </summary>
    public MissKind OnLateShow(long nowNs, long handoverLeadNs, int refreshes)
    {
        MissKind cause;
        if (InRateChange(nowNs))
        {
            cause = MissKind.RateChange;
        }
        else
        {
            cause = handoverLeadNs >= RequiredLeadNs(nowNs) ? MissKind.DisplayLate : MissKind.LatePresent;
            Record(nowNs, handoverLeadNs, late: true);
        }

        // What is owed is the pipeline's if any of it is (conservative).
        _debtCause = _debt > 0 && _debtCause == MissKind.LatePresent ? MissKind.LatePresent : cause;
        _debt += Math.Max(1, refreshes);
        return cause;
    }

    /// <summary>
    /// A slot that had no refresh left (dropped, or a real frame shown a refresh late behind an earlier late one): whose
    /// miss it is. Within what the FIFO owes, the late show's (the display's: a catch-up); otherwise the pipeline's.
    /// <paramref name="givesBackARefresh"/>: it was dropped, so the FIFO owes one refresh less.
    /// </summary>
    public MissKind OnCatchUp(long nowNs, bool givesBackARefresh, bool shownLate)
    {
        MissKind kind;
        if (InRateChange(nowNs))
        {
            kind = MissKind.RateChange;
        }
        else if (_debt > 0)
        {
            kind = _debtCause switch
            {
                MissKind.DisplayLate => MissKind.CatchUp,
                MissKind.RateChange => MissKind.RateChange,
                _ => shownLate ? MissKind.LatePresent : MissKind.Dropped,
            };
        }
        else
        {
            kind = shownLate ? MissKind.LatePresent : MissKind.Dropped;
        }

        if (givesBackARefresh && _debt > 0)
        {
            _debt--;
        }

        return kind;
    }

    /// <summary>A present submitted for its own refresh: whatever was owed has been given back.</summary>
    public void OnOnTime() => _debt = 0;

    private void Record(long nowNs, long leadNs, bool late)
    {
        _at[_head] = nowNs;
        _lead[_head] = leadNs;
        _late[_head] = late;
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }
}

/// <summary>
/// The present lead: how long before its refresh a slot's content must be ready (the scheduler is given the grid shifted
/// earlier by it). It starts at the configured lead (1.5 refreshes by default, or <c>--present-lead-ms</c>) and adapts to
/// the output: when a present is shown late although it was submitted in time and only its content was late (the content,
/// not the hand-over, missed the compositor's latch), the lead goes up by one step at once, so the next frames' content is
/// ready that much earlier; the level that last failed is then kept as a floor for a long time, so the lead does not drift
/// back into the same miss. Without a miss it comes down slowly toward the configured lead. It never exceeds what the
/// two-presents-in-flight cap lets a present be handed over (about two refreshes). Present thread; allocation-free.
/// </summary>
internal sealed class LeadController
{
    public const long RaiseStepNs = Nanoseconds.PerMillisecond;
    public const long DecayStepNs = 250 * Nanoseconds.PerMicrosecond;
    public const long QuietBeforeDecayNs = 30 * Nanoseconds.PerSecond;
    public const long DecayEveryNs = 10 * Nanoseconds.PerSecond;
    public const long FloorHoldNs = 10 * 60 * Nanoseconds.PerSecond;

    /// <summary>After a raise, late frames already decided with the old lead do not raise it again for this long.</summary>
    public const long RaiseCooldownNs = 50 * Nanoseconds.PerMillisecond;

    private readonly bool _adaptive;
    private long _floorNs;
    private long _floorUntilNs = long.MinValue;
    private long _lastRaiseNs = long.MinValue;
    private long _lastDecayNs = long.MinValue;

    public LeadController(long baseLeadNs, long maxLeadNs, bool adaptive)
    {
        _adaptive = adaptive;
        Reset(baseLeadNs, maxLeadNs, long.MinValue);
    }

    public long LeadNs { get; private set; }

    public long BaseLeadNs { get; private set; }

    public long MaxLeadNs { get; private set; }

    /// <summary>Times the lead went up (a present whose content missed the latch).</summary>
    public long Raises { get; private set; }

    /// <summary>The largest lead used.</summary>
    public long PeakLeadNs { get; private set; }

    /// <summary>A new output (rate): back to its configured lead.</summary>
    public void Reset(long baseLeadNs, long maxLeadNs, long nowNs)
    {
        MaxLeadNs = Math.Max(0, maxLeadNs);
        BaseLeadNs = Math.Clamp(baseLeadNs, 0, MaxLeadNs);
        LeadNs = BaseLeadNs;
        _floorNs = BaseLeadNs;
        _floorUntilNs = long.MinValue;
        _lastRaiseNs = nowNs;
        _lastDecayNs = nowNs;
        PeakLeadNs = Math.Max(PeakLeadNs, LeadNs);
    }

    /// <summary>
    /// A present shown late that was handed over as soon as it was there (decided too late, not held back by the in-flight
    /// cap): one step up, at once (true when the lead changed); frames decided before the raise do not raise it again for
    /// <see cref="RaiseCooldownNs"/>. The new level becomes the floor for <see cref="FloorHoldNs"/>.
    /// </summary>
    public bool OnLateContent(long nowNs)
    {
        if (!_adaptive || LeadNs >= MaxLeadNs || Since(_lastRaiseNs, nowNs) < RaiseCooldownNs)
        {
            return false;
        }

        LeadNs = Math.Min(MaxLeadNs, LeadNs + RaiseStepNs);
        _floorNs = LeadNs;
        _floorUntilNs = nowNs + FloorHoldNs;
        _lastRaiseNs = nowNs;
        Raises++;
        PeakLeadNs = Math.Max(PeakLeadNs, LeadNs);
        return true;
    }

    /// <summary>Lets the lead come down toward the configured one after a quiet spell (true when it changed).</summary>
    public bool Tick(long nowNs)
    {
        if (!_adaptive || LeadNs <= BaseLeadNs)
        {
            return false;
        }

        var floor = nowNs < _floorUntilNs ? _floorNs : BaseLeadNs;
        if (LeadNs <= floor || Since(_lastRaiseNs, nowNs) < QuietBeforeDecayNs || Since(_lastDecayNs, nowNs) < DecayEveryNs)
        {
            return false;
        }

        LeadNs = Math.Max(floor, LeadNs - DecayStepNs);
        _lastDecayNs = nowNs;
        return true;
    }

    private static long Since(long thenNs, long nowNs) => thenNs == long.MinValue ? long.MaxValue : nowNs - thenNs;
}

/// <summary>
/// How far ahead of a refresh the compositor takes a present (its latch, λ), bounded from timed shows: a present taken
/// for refresh S was handed over (submitted and written) by S − λ, so λ ≤ S − hand-over; one that could have been taken
/// for the refresh before (nothing queued for it) but was not was handed over after S − P − λ, so λ &gt; S − P − hand-over.
/// The bounds are over the last ten seconds. The lower bound is what an empty queue needs: a present handed over before
/// R − P − λ could be taken for the refresh before R and show a refresh early, so nothing is handed over into an empty
/// queue before R − P − <see cref="LowNs"/> (plus a margin). Present thread; allocation-free.
/// </summary>
internal sealed class LatchEstimator
{
    public const int Capacity = 4096;
    public const long WindowNs = 10 * Nanoseconds.PerSecond;

    private readonly long[] _at = new long[Capacity];
    private readonly long[] _low = new long[Capacity];
    private readonly long[] _high = new long[Capacity];
    private int _head;
    private int _count;
    private long _lowNs;
    private long _highNs = long.MaxValue;
    private long _computedAt = long.MinValue;

    /// <summary>The latch is at least this far ahead of a refresh (0 without evidence).</summary>
    public long LowNs => _lowNs;

    /// <summary>The latch is at most this far ahead of a refresh (long.MaxValue without evidence).</summary>
    public long HighNs => _highNs;

    public void Clear()
    {
        _count = 0;
        _lowNs = 0;
        _highNs = long.MaxValue;
    }

    /// <summary>
    /// A timed show at <paramref name="shownNs"/> handed over at <paramref name="handoverNs"/>; <paramref name="couldHaveMadeEarlier"/>:
    /// nothing was queued for the refresh before it (the previous present flipped two refreshes earlier, or none was
    /// queued), so it was not taken for that refresh only because it came after its latch.
    /// </summary>
    public void Add(long nowNs, long shownNs, long handoverNs, long periodNs, bool couldHaveMadeEarlier)
    {
        _at[_head] = nowNs;
        _high[_head] = shownNs - handoverNs;
        _low[_head] = couldHaveMadeEarlier ? shownNs - periodNs - handoverNs : long.MinValue;
        Take(_low[_head], _high[_head]);
    }

    /// <summary>A present predicted for a refresh it could have been taken for (nothing queued before it) but shown later: λ &gt; refresh − hand-over.</summary>
    public void AddMissed(long nowNs, long refreshNs, long handoverNs)
    {
        _at[_head] = nowNs;
        _high[_head] = long.MaxValue;
        _low[_head] = refreshNs - handoverNs;
        Take(_low[_head], _high[_head]);
    }

    private void Take(long low, long high)
    {
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        _highNs = Math.Min(_highNs, high);
        _lowNs = Math.Max(_lowNs, low);
        if (_highNs != long.MaxValue)
        {
            _lowNs = Math.Min(_lowNs, _highNs);
        }
    }

    /// <summary>Drops evidence older than the window (call now and then).</summary>
    public void Expire(long nowNs)
    {
        if (nowNs - _computedAt > Nanoseconds.PerSecond)
        {
            Recompute(nowNs);
        }
    }

    private void Recompute(long nowNs)
    {
        long low = 0, high = long.MaxValue;
        for (var i = 0; i < _count; i++)
        {
            var j = (_head - 1 - i + Capacity) % Capacity;
            if (nowNs - _at[j] > WindowNs)
            {
                break;
            }

            low = Math.Max(low, _low[j]);
            high = Math.Min(high, _high[j]);
        }

        _lowNs = high == long.MaxValue ? low : Math.Min(low, high);
        _highNs = high;
        _computedAt = nowNs;
    }
}
