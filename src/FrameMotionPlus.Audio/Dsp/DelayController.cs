namespace FrameMotionPlus.Audio.Dsp;

/// <summary>Tuning of <see cref="DelayController"/>. The defaults are what <c>fmp capture</c> uses.</summary>
public sealed record DelayControllerOptions
{
    /// <summary>Ratio deviation per second of delay error (P): 1 /s turns a 1 ms error into 1,000 ppm, 1 ms/s of correction.</summary>
    public double Proportional { get; init; } = 1.0;

    /// <summary>
    /// Integral gain per second squared (it learns the clock drift): 0.1 /s² makes the loop overdamped (ζ ≈ 1.6), so a change
    /// of target settles to 50 µs in about 20 s without overshooting by more than the target filter's lag.
    /// </summary>
    public double Integral { get; init; } = 0.1;

    /// <summary>The integral (the clock drift it has learned) is kept within this, ppm.</summary>
    public double MaxDriftPpm { get; init; } = 1_000;

    /// <summary>|ratio − 1| never exceeds this, ppm: 2,000 ppm is a pitch change of at most 3.5 cents, and 2 ms/s of correction.</summary>
    public double MaxRatioPpm { get; init; } = 2_000;

    /// <summary>The ratio changes by at most this per second, ppm/s: no step and no audible wobble.</summary>
    public double MaxRatioSlewPpmPerSecond { get; init; } = 2_000;

    /// <summary>Time constant of the low-pass on the measured error (timestamp jitter), s.</summary>
    public double MeasurementTimeConstantS { get; init; } = 0.5;

    /// <summary>Time constant of the low-pass on the requested target (it follows the video slowly), s.</summary>
    public double TargetTimeConstantS { get; init; } = 1.0;

    /// <summary>A filtered error this large for <see cref="JumpAfterS"/> is corrected by a cross-faded jump instead of resampling, ns.</summary>
    public double JumpThresholdNs { get; init; } = 15e6;

    public double JumpAfterS { get; init; } = 0.3;

    /// <summary>A raw error this large for three blocks in a row (a sink switch, a stall) jumps at once, ns.</summary>
    public double ImmediateJumpNs { get; init; } = 100e6;
}

/// <summary>What <see cref="DelayController.Update"/> decided for one block.</summary>
public readonly record struct DelayDecision(double Ratio, double JumpNs, double TargetNs, double FilteredErrorNs);

/// <summary>
/// Keeps the audio's total delay (capture time to the time it is heard) on a target that moves: a proportional-integral
/// controller on the measured delay error drives the resampling ratio, so the delay converges and then holds against
/// the drift between the capture card's and the sink's sample clocks (the integral learns the drift; the ratio averages to
/// it), with the target's own rate of change fed forward (following a moving target is not mistaken for drift). The measured error is low-passed against timestamp jitter, the requested target against the video's changes, and
/// the ratio is bounded in deviation and in slew (the pitch bound). Errors too large to resample away within a few seconds
/// are corrected by a jump that the caller cross-fades. Pure and deterministic: time is the caller's (seconds per block).
/// </summary>
public sealed class DelayController
{
    private readonly DelayControllerOptions _o;
    private bool _locked;
    private double _target;
    private double _filtered;
    private double _fast;
    private double _integral;
    private double _ratio = 1;
    private double _overSince;
    private int _bigRaw;

    public DelayController(DelayControllerOptions? options = null)
    {
        _o = options ?? new DelayControllerOptions();
    }

    public DelayControllerOptions Options => _o;

    public bool Locked => _locked;

    /// <summary>The ratio of the last decision (input frames per output frame).</summary>
    public double Ratio => _ratio;

    /// <summary>The learned drift (the integral term), as a ratio deviation.</summary>
    public double IntegralTerm => _integral;

    /// <summary>The smoothed target, ns.</summary>
    public double TargetNs => _target;

    /// <summary>
    /// Starts over at <paramref name="targetNs"/> (the caller has placed the read position there): the filters restart, the
    /// learned drift is kept (the clocks have not changed), the ratio returns to it.
    /// </summary>
    public void Lock(double targetNs)
    {
        _locked = true;
        _target = targetNs;
        _filtered = 0;
        _fast = 0;
        _overSince = 0;
        _bigRaw = 0;
        _ratio = 1 + _integral;
    }

    /// <summary>Forgets the lock (the next update locks at once); the learned drift is kept.</summary>
    public void Unlock() => _locked = false;

    /// <summary>
    /// One block: the delay measured at its start, the target asked for (already clamped to what the path can do), and
    /// the block's length in seconds. Returns the ratio for the block and a jump (ns; positive: less delay) when one is due.
    /// </summary>
    public DelayDecision Update(double measuredNs, double requestedTargetNs, double dtSeconds)
    {
        if (!_locked)
        {
            // Not placed yet: the caller places the read position at the target (the error is the jump).
            Lock(requestedTargetNs);
            return new DelayDecision(_ratio, measuredNs - requestedTargetNs, _target, 0);
        }

        var previousTarget = _target;
        _target += (requestedTargetNs - _target) * Alpha(dtSeconds, _o.TargetTimeConstantS);

        // Feed-forward: a target moving at s seconds per second needs the ratio 1 − s on top of the drift. The PI then only
        // corrects what is left, and the integral learns the clocks' drift, not the video's wander.
        var slope = (_target - previousTarget) / 1e9 / dtSeconds;
        var error = measuredNs - _target;
        _filtered += (error - _filtered) * Alpha(dtSeconds, _o.MeasurementTimeConstantS);
        _fast += (error - _fast) * Alpha(dtSeconds, FastTimeConstantS);

        _bigRaw = Math.Abs(error) > _o.ImmediateJumpNs ? _bigRaw + 1 : 0;
        _overSince = Math.Abs(_filtered) > _o.JumpThresholdNs ? _overSince + dtSeconds : 0;
        if (_bigRaw >= 3 || _overSince >= _o.JumpAfterS)
        {
            // The jump is the error now (a short average: the filtered error lags a step by its time constant).
            var jump = _bigRaw >= 3 ? error : _fast;
            _filtered = 0;
            _fast = 0;
            _overSince = 0;
            _bigRaw = 0;
            return new DelayDecision(_ratio, jump, _target, 0);
        }

        var errorS = _filtered / 1e9;
        var maxDrift = _o.MaxDriftPpm * 1e-6;
        var maxDeviation = _o.MaxRatioPpm * 1e-6;
        var unbounded = 1 + (_o.Proportional * errorS) + _integral - slope;

        // Anti-windup: the integral does not grow further while the ratio is held at its bound in the same direction.
        var step = _o.Integral * errorS * dtSeconds;
        if (!((unbounded >= 1 + maxDeviation && step > 0) || (unbounded <= 1 - maxDeviation && step < 0)))
        {
            _integral = Math.Clamp(_integral + step, -maxDrift, maxDrift);
        }

        var wanted = Math.Clamp(1 + (_o.Proportional * errorS) + _integral - slope, 1 - maxDeviation, 1 + maxDeviation);
        var maxStep = _o.MaxRatioSlewPpmPerSecond * 1e-6 * dtSeconds;
        _ratio += Math.Clamp(wanted - _ratio, -maxStep, maxStep);
        return new DelayDecision(_ratio, 0, _target, _filtered);
    }

    /// <summary>The filtered error is reset after the caller moved the position by a jump.</summary>
    public void Jumped()
    {
        _filtered = 0;
        _fast = 0;
    }

    /// <summary>Time constant of the short average a jump is sized from, s (a few blocks).</summary>
    private const double FastTimeConstantS = 0.02;

    private static double Alpha(double dt, double tau) => tau <= 0 ? 1 : 1 - Math.Exp(-dt / tau);
}
