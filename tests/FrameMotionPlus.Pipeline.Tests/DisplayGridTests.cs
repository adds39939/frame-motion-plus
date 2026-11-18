using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The refresh grid the present thread predicts with (pure; no hardware). The output's nominal rate differs from its
/// refreshes as CLOCK_MONOTONIC sees them by tens of ppm (the gate run's output: about 20 ppm), so predictions must stay
/// on the refreshes the shows put the grid on, not on multiples of the nominal period.
/// </summary>
public class DisplayGridTests
{
    private const double NominalHz = 240.0;

    /// <summary>
    /// Ten minutes of one present per refresh at 240 Hz with the FIFO never empty, each present predicted one refresh after
    /// the one before (the present thread's chain): stepped with NextAfter, every prediction is the refresh the present is
    /// shown at, within the first-pixel times' jitter; adding the nominal period instead drifts by half a refresh within
    /// two minutes (which put presents on the drop/filler boundary and stalled the gate run's generator).
    /// </summary>
    [Theory]
    [InlineData(-30e-6)]
    [InlineData(30e-6)]
    public void Chained_predictions_stay_on_the_refreshes_when_the_true_rate_is_not_the_nominal_one(double rateError)
    {
        var nominal = FrameRate.FromDouble(NominalHz);
        var truePeriod = nominal.PeriodNs * (1 + rateError);
        var grid = new DisplayGrid(nominal);
        var random = new Random(42);
        long Refresh(long k) => 1_000_000_000L + (long)Math.Round(k * truePeriod);
        long Jitter() => (long)((random.NextDouble() - 0.5) * 600_000); // ±0.3 ms, as first-pixel times scatter here

        grid.AddShow(Refresh(0));
        var chained = Refresh(0);
        var added = Refresh(0);
        var worst = 0L;
        var addedWorst = 0L;
        var refreshes = (long)(600 * NominalHz);
        for (var k = 1; k <= refreshes; k++)
        {
            chained = grid.NextAfter(chained);
            added += (long)Math.Round(nominal.PeriodNs);
            var truth = Refresh(k);
            worst = Math.Max(worst, Math.Abs(chained - truth));
            addedWorst = Math.Max(addedWorst, Math.Abs(added - truth));
            grid.AddShow(truth + Jitter());
        }

        var half = (long)(nominal.PeriodNs / 2);
        Assert.True(addedWorst > half, $"adding the nominal period drifted only {addedWorst / 1e6:0.000} ms (the test would show nothing)");
        Assert.True(worst < 700_000, $"a chained prediction was {worst / 1e6:0.000} ms off its refresh (half a refresh is {half / 1e6:0.000} ms)");
    }

    /// <summary>
    /// The window moves from a 240 Hz output to a 119.88 Hz one (the review's run went that way by itself): the grid is reset
    /// to the new rate and the first show on the new output puts it on the new refreshes at once; every later prediction
    /// steps on the new period (synthetic feedback from both outputs, the new one at an arbitrary phase).
    /// </summary>
    [Fact]
    public void After_a_move_to_another_output_the_first_show_puts_the_grid_on_the_new_refreshes()
    {
        var grid = new DisplayGrid(FrameRate.FromDouble(240.016));
        for (var k = 0; k < 200; k++)
        {
            grid.AddShow(1_000_000_000L + (long)Math.Round(k * FrameRate.FromDouble(240.016).PeriodNs));
        }

        var slow = FrameRate.FromDouble(119.88);
        grid.Reset(slow);
        Assert.False(grid.HasPhase);
        var origin = 2_000_003_217L;
        grid.AddShow(origin);
        var predicted = origin;
        for (var k = 1; k <= 1000; k++)
        {
            predicted = grid.NextAfter(predicted);
            var truth = origin + (long)Math.Round(k * slow.PeriodNs);
            Assert.InRange(predicted - truth, -100_000, 100_000);
            grid.AddShow(truth);
        }
    }

    [Fact]
    public void Next_after_steps_one_refresh_from_the_nearest_one()
    {
        var grid = new DisplayGrid(FrameRate.FromDouble(100));
        grid.AddShow(1_000_000_000);
        Assert.Equal(1_010_000_000, grid.NextAfter(1_000_000_000));
        Assert.Equal(1_010_000_000, grid.NextAfter(1_004_000_000));
        Assert.Equal(1_020_000_000, grid.NextAfter(1_006_000_000));
        Assert.Equal(1_000_000_000, grid.NextAfter(990_000_000));
    }
}
