namespace FrameMotionPlus.Core.Scheduling;

/// <summary>
/// Decides whether source timestamps sit on the fixed-refresh vblank grid (an "integer ratio": 60 on 120,
/// 30-in-60, 40-in-120) and, if so, at which phase. For each recent frame it takes the residual of its
/// timestamp modulo the refresh period, unwraps it around the newest frame, and fits a line: the slope is
/// the phase drift between the two clocks and the scatter about the line is timestamp jitter. A coherent
/// source lets the scheduler choose D so real frames land exactly on vblanks; 59.94 on 120 drifts about
/// 1000 ppm and is treated as a non-integer ratio instead.
/// </summary>
public sealed class PhaseCoherenceEstimator
{
    private readonly long[] _times;
    private readonly int _minFrames;
    private readonly double _maxDrift;
    private readonly double _maxJitterNs;
    private int _count;
    private int _next;
    private long _anchorNs;
    private double _periodNs;

    public PhaseCoherenceEstimator(int window, int minFrames, double maxDriftPpm, long maxJitterNs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(minFrames, 2);
        _times = new long[window];
        _minFrames = minFrames;
        _maxDrift = maxDriftPpm * 1e-6;
        _maxJitterNs = maxJitterNs;
    }

    public bool IsCoherent { get; private set; }

    /// <summary>Fitted phase of the newest frame relative to the grid anchor, in [0, period).</summary>
    public double PhaseNs { get; private set; }

    public double DriftPpm { get; private set; }

    public double JitterNs { get; private set; }

    public int Count => _count;

    public void SetGrid(long anchorNs, double periodNs)
    {
        _anchorNs = anchorNs;
        _periodNs = periodNs;
        Recompute();
    }

    public void Add(long contentTimeNs)
    {
        _times[_next] = contentTimeNs;
        _next = _next + 1 == _times.Length ? 0 : _next + 1;
        if (_count < _times.Length)
        {
            _count++;
        }

        Recompute();
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        IsCoherent = false;
        PhaseNs = 0;
        DriftPpm = 0;
        JitterNs = 0;
    }

    internal static double PositiveMod(double value, double modulus) => value - (modulus * Math.Floor(value / modulus));

    private void Recompute()
    {
        if (_count < 2 || !(_periodNs > 0))
        {
            IsCoherent = false;
            return;
        }

        var p = _periodNs;
        var newestIndex = _next == 0 ? _times.Length - 1 : _next - 1;
        var newest = _times[newestIndex];
        var newestResidual = PositiveMod(newest - _anchorNs, p);

        // Pass 1: means of x (time relative to newest) and δ (residual unwrapped around the newest frame).
        double sumX = 0, sumD = 0;
        for (var i = 0; i < _count; i++)
        {
            double x = _times[i] - newest;
            sumX += x;
            sumD += x - (p * Math.Round(x / p));
        }

        var meanX = sumX / _count;
        var meanD = sumD / _count;

        // Pass 2: centred least squares for slope b; then the worst deviation from the line.
        double sxx = 0, sxd = 0;
        for (var i = 0; i < _count; i++)
        {
            double x = _times[i] - newest;
            var d = x - (p * Math.Round(x / p));
            sxx += (x - meanX) * (x - meanX);
            sxd += (x - meanX) * (d - meanD);
        }

        var slope = sxx > 0 ? sxd / sxx : 0;
        var intercept = meanD - (slope * meanX);
        double jitter = 0;
        for (var i = 0; i < _count; i++)
        {
            double x = _times[i] - newest;
            var d = x - (p * Math.Round(x / p));
            jitter = Math.Max(jitter, Math.Abs(d - (intercept + (slope * x))));
        }

        PhaseNs = PositiveMod(newestResidual + intercept, p);
        DriftPpm = slope * 1e6;
        JitterNs = jitter;

        if (_count < _minFrames)
        {
            IsCoherent = false;
            return;
        }

        var drift = Math.Abs(slope);
        IsCoherent = IsCoherent
            ? drift <= _maxDrift && jitter <= _maxJitterNs
            : drift <= _maxDrift * 0.5 && jitter <= _maxJitterNs * 0.5;
    }
}
