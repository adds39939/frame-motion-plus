using FrameMotionPlus.Audio.Dsp;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// The controller alone against a model of the delay line (the delay grows by the clock drift and shrinks by the ratio's
/// excess over 1): it converges, learns the drift, keeps the ratio inside its pitch bound and slew limit, and jumps only on
/// large sustained or sudden errors.
/// </summary>
public class DelayControllerTests
{
    private const double Dt = 256 / 48_000.0;

    /// <summary>Runs the model for <paramref name="seconds"/>; returns the delay at the end and the extremes of the ratio.</summary>
    private static (double Delay, double MinRatio, double MaxRatio, double MaxStep, int Jumps) Simulate(
        DelayController c, double delayNs, double targetNs, double driftPpm, double seconds, Func<double, double>? target = null)
    {
        var minRatio = double.MaxValue;
        var maxRatio = double.MinValue;
        var maxStep = 0.0;
        var previous = c.Ratio;
        var jumps = 0;
        for (var t = 0.0; t < seconds; t += Dt)
        {
            var d = c.Update(delayNs, target?.Invoke(t) ?? targetNs, Dt);
            if (d.JumpNs != 0)
            {
                jumps++;
                delayNs -= d.JumpNs;
                c.Jumped();
            }

            delayNs += ((driftPpm * 1e-6) - (d.Ratio - 1)) * Dt * 1e9;
            minRatio = Math.Min(minRatio, d.Ratio);
            maxRatio = Math.Max(maxRatio, d.Ratio);
            maxStep = Math.Max(maxStep, Math.Abs(d.Ratio - previous));
            previous = d.Ratio;
        }

        return (delayNs, minRatio, maxRatio, maxStep, jumps);
    }

    [Fact]
    public void The_first_update_places_the_position_at_the_target()
    {
        var c = new DelayController();
        var d = c.Update(57e6, 40e6, Dt);
        Assert.Equal(17e6, d.JumpNs);
        Assert.True(c.Locked);
        Assert.Equal(40e6, c.TargetNs);
    }

    [Theory]
    [InlineData(500.0)]
    [InlineData(-500.0)]
    [InlineData(0.0)]
    [InlineData(80.0)]
    public void It_converges_and_learns_the_drift(double driftPpm)
    {
        var c = new DelayController();
        _ = c.Update(40e6, 40e6, Dt);
        var (delay, _, _, _, jumps) = Simulate(c, 40e6 + 4e6, 40e6, driftPpm, 300);
        Assert.Equal(0, jumps);
        Assert.True(Math.Abs(delay - 40e6) < 20_000, $"delay {delay / 1e6:0.000} ms");
        Assert.True(Math.Abs(c.IntegralTerm - (driftPpm * 1e-6)) < 5e-6, $"integral {c.IntegralTerm * 1e6:0.0} ppm against drift {driftPpm}");
        Assert.True(Math.Abs(c.Ratio - 1 - (driftPpm * 1e-6)) < 5e-6);
    }

    [Fact]
    public void The_ratio_stays_inside_its_pitch_bound_and_slew_limit()
    {
        var o = new DelayControllerOptions();
        var c = new DelayController(o);
        _ = c.Update(40e6, 40e6, Dt);
        var (delay, minRatio, maxRatio, maxStep, jumps) = Simulate(c, 40e6 + 14e6, 40e6, 300, 120);
        Assert.Equal(0, jumps);
        Assert.True(maxRatio <= 1 + (o.MaxRatioPpm * 1e-6) + 1e-12, $"max ratio {(maxRatio - 1) * 1e6:0.0} ppm");
        Assert.True(minRatio >= 1 - (o.MaxRatioPpm * 1e-6) - 1e-12);
        Assert.True(maxStep <= (o.MaxRatioSlewPpmPerSecond * 1e-6 * Dt) + 1e-12, $"step {maxStep * 1e6:0.000} ppm per block");
        Assert.True(Math.Abs(delay - 40e6) < 100_000, $"delay {delay / 1e6:0.000} ms");
    }

    [Fact]
    public void It_follows_a_moving_target_smoothly()
    {
        var c = new DelayController();
        _ = c.Update(40e6, 40e6, Dt);

        // The video latency steps from 40 to 46 ms at 10 s, then wanders by ±1 ms at 0.1 Hz.
        static double Target(double t) => t < 10 ? 40e6 : 46e6 + (1e6 * Math.Sin(2 * Math.PI * 0.1 * t));
        var (delay, minRatio, maxRatio, _, jumps) = Simulate(c, 40e6, 0, 120, 120, Target);
        Assert.Equal(0, jumps);
        Assert.True(Math.Abs(delay - Target(120)) < 1.0e6, $"delay {delay / 1e6:0.000} ms, target {Target(120) / 1e6:0.000}");
        Assert.True(maxRatio - 1 <= 2e-3 + 1e-12 && 1 - minRatio <= 2e-3 + 1e-12, $"ratio {(minRatio - 1) * 1e6:0.0} to {(maxRatio - 1) * 1e6:0.0} ppm");
    }

    [Fact]
    public void A_large_sustained_error_is_corrected_by_one_jump()
    {
        var o = new DelayControllerOptions();
        var c = new DelayController(o);
        _ = c.Update(40e6, 40e6, Dt);
        var (delay, _, _, _, jumps) = Simulate(c, 40e6 + 30e6, 40e6, 0, 5);
        Assert.Equal(1, jumps);
        Assert.True(Math.Abs(delay - 40e6) < 1e6, $"delay {delay / 1e6:0.000} ms");
    }

    [Fact]
    public void A_sudden_huge_error_jumps_within_three_blocks()
    {
        var c = new DelayController();
        _ = c.Update(40e6, 40e6, Dt);
        for (var i = 0; i < 10; i++)
        {
            _ = c.Update(40e6, 40e6, Dt);
        }

        Assert.Equal(0, c.Update(340e6, 40e6, Dt).JumpNs);
        Assert.Equal(0, c.Update(340e6, 40e6, Dt).JumpNs);
        var d = c.Update(340e6, 40e6, Dt);
        Assert.Equal(300e6, d.JumpNs, 1e3);
    }

    [Fact]
    public void A_single_wild_measurement_does_not_jump()
    {
        var c = new DelayController();
        _ = c.Update(40e6, 40e6, Dt);
        for (var i = 0; i < 2000; i++)
        {
            var measured = i % 97 == 0 ? 400e6 : 40e6;
            Assert.Equal(0, c.Update(measured, 40e6, Dt).JumpNs);
        }
    }
}
