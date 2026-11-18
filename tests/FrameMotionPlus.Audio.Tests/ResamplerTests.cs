using FrameMotionPlus.Audio.Dsp;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// The resampler: the windowed-sinc kernel is an exact impulse at whole frames and has unit gain at every phase; at a
/// ratio of 1 on a whole frame it copies bit for bit; resampled sines match the ideal signal; a change of ratio and a
/// cross-fade leave no step in the output.
/// </summary>
public class ResamplerTests
{
    private const double Rate = 48_000;

    [Fact]
    public void The_kernel_at_a_whole_frame_is_an_exact_unit_impulse()
    {
        var c = new float[SincKernel.Taps];
        SincKernel.Instance.Coefficients(0, c);
        for (var k = 0; k < SincKernel.Taps; k++)
        {
            Assert.Equal(k == SincKernel.Before ? 1f : 0f, c[k]);
        }
    }

    [Fact]
    public void Every_phase_has_unit_gain_at_dc_and_is_symmetric_about_half_a_frame()
    {
        var c = new float[SincKernel.Taps];
        var mirror = new float[SincKernel.Taps];
        for (var p = 0; p < 1000; p++)
        {
            var f = p / 1000.0;
            SincKernel.Instance.Coefficients(f, c);
            Assert.Equal(1.0, c.Sum(x => (double)x), 5);
            SincKernel.Instance.Coefficients(1 - f, mirror);
            if (p > 0)
            {
                // h_f[k] mirrors h_(1−f) about the centre between frames n and n+1.
                for (var k = 0; k < SincKernel.Taps; k++)
                {
                    Assert.Equal(c[k], mirror[SincKernel.Taps - 1 - k], 4);
                }
            }
        }
    }

    [Fact]
    public void At_a_ratio_of_one_on_a_whole_frame_the_output_is_the_input_bit_for_bit()
    {
        var ring = new SampleRing(4096, 256);
        var input = new float[3000 * 2];
        var random = new Random(7);
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1);
        }

        ring.Write(input);
        var reader = new ResamplingReader();
        reader.Seek(100);
        reader.SetGain(1);
        var output = new float[2000 * 2];
        reader.Render(ring, output, 1000, 1.0, 1.0);
        reader.Render(ring, output.AsSpan(2000), 1000, 1.0, 1.0);
        for (var i = 0; i < 2000 * 2; i++)
        {
            Assert.Equal(input[(100 * 2) + i], output[i]);
        }

        Assert.Equal(2100, reader.Position);
    }

    [Theory]
    [InlineData(1000.0, 1.002)]
    [InlineData(1000.0, 0.998)]
    [InlineData(10000.0, 1.0005)]
    [InlineData(15000.0, 1.002)]
    public void A_resampled_sine_matches_the_ideal_signal(double hz, double ratio)
    {
        var ring = new SampleRing(1 << 15, 1024);
        var input = new float[20_000 * 2];
        for (var i = 0; i < 20_000; i++)
        {
            input[2 * i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Rate));
            input[(2 * i) + 1] = input[2 * i];
        }

        ring.Write(input);
        var reader = new ResamplingReader();
        const double start = 500.37;
        reader.Seek(start);
        reader.SetGain(1);
        var frames = 15_000;
        var output = new float[frames * 2];
        reader.Render(ring, output, frames, ratio, ratio);
        double error = 0, signal = 0;
        for (var i = 0; i < frames; i++)
        {
            var ideal = 0.5 * Math.Sin(2 * Math.PI * hz * (start + (i * ratio)) / Rate);
            error += (output[2 * i] - ideal) * (output[2 * i] - ideal);
            signal += ideal * ideal;
        }

        var snrDb = 10 * Math.Log10(signal / error);
        // The window's ripple (β = 7: about ±3·10⁻⁴) bounds the accuracy: about −79 dB at 1 kHz, and at 15 kHz, 5 kHz below
        // the edge of the band, about −60 dB. Inaudible either way: a static gain ripple, not noise.
        Assert.True(snrDb > (hz > 5_000 ? 55 : 75), $"SNR {snrDb:0.0} dB at {hz} Hz, ratio {ratio}");
    }

    [Fact]
    public void A_change_of_ratio_across_a_block_is_a_ramp_not_a_step()
    {
        // The output is the input read at a position whose rate of advance ramps: it must equal the ideal signal at the
        // integrated position, sample by sample (a step in the ratio would only show as a kink in phase, so compare phase).
        var ring = new SampleRing(1 << 15, 1024);
        var input = new float[20_000 * 2];
        const double hz = 2000;
        for (var i = 0; i < 20_000; i++)
        {
            input[2 * i] = (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        }

        ring.Write(input);
        var reader = new ResamplingReader();
        reader.Seek(1000);
        reader.SetGain(1);
        var output = new float[512 * 2];
        reader.Render(ring, output, 512, 1.0, 1.002);
        var position = 1000.0;
        var maxError = 0.0;
        for (var i = 0; i < 512; i++)
        {
            var ideal = Math.Sin(2 * Math.PI * hz * position / Rate);
            maxError = Math.Max(maxError, Math.Abs(output[2 * i] - ideal));
            position += 1.0 + (0.002 * i / 512);
        }

        Assert.True(maxError < 3e-4, $"max error {maxError}"); // the kernel's ripple, not the ramp
        Assert.Equal(position, reader.Position, 6);
    }

    [Fact]
    public void A_cross_faded_jump_has_no_step_and_no_overshoot()
    {
        var ring = new SampleRing(1 << 15, 1024);
        var input = new float[20_000 * 2];
        const double hz = 440;
        for (var i = 0; i < 20_000; i++)
        {
            input[2 * i] = (float)(0.8 * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        ring.Write(input);
        var reader = new ResamplingReader();
        reader.Seek(5000);
        reader.SetGain(1);
        var output = new float[4000 * 2];
        reader.Render(ring, output, 1000, 1.0, 1.0);
        reader.CrossFadeTo(reader.Position + 797.5, 960); // 16.6 ms later in the signal, out of phase
        reader.Render(ring, output.AsSpan(2000), 3000, 1.0, 1.0);
        var maxSlope = 0.8 * 2 * Math.PI * hz / Rate; // the largest step a 0.8 sine at 440 Hz makes per frame
        for (var i = 1; i < 4000; i++)
        {
            var step = Math.Abs(output[2 * i] - output[2 * (i - 1)]);
            Assert.True(step <= (maxSlope * 1.5) + 1e-4, $"step {step} at frame {i} (sine's largest {maxSlope})");
        }

        // Equal gain: never louder than the input (an equal-power fade of correlated sound peaks at 1.41x).
        Assert.True(output.Max(Math.Abs) <= 0.8f * 1.001f, $"peak {output.Max(Math.Abs)} above the input's 0.8");

        Assert.False(reader.CrossFading);
    }

    [Fact]
    public void A_gain_ramp_reaches_its_target_in_the_frames_given()
    {
        var ring = new SampleRing(4096, 256);
        var ones = Enumerable.Repeat(1f, 4000 * 2).ToArray();
        ring.Write(ones);
        var reader = new ResamplingReader();
        reader.Seek(100);
        reader.SetGain(0);
        reader.RampGain(1, 480);
        var output = new float[1000 * 2];
        reader.Render(ring, output, 1000, 1.0, 1.0);
        Assert.Equal(1f / 480, output[0], 5);
        Assert.Equal(1f, output[2 * 479], 5);
        Assert.Equal(1f, output[2 * 999]);
        for (var i = 1; i < 480; i++)
        {
            Assert.True(output[2 * i] > output[2 * (i - 1)]);
        }
    }

    [Fact]
    public void The_ring_wraps_and_overwrites_the_oldest_frames()
    {
        var ring = new SampleRing(64, 8);
        var block = new float[48 * 2];
        for (var b = 0; b < 3; b++)
        {
            for (var i = 0; i < 48; i++)
            {
                block[2 * i] = (b * 48) + i;
                block[(2 * i) + 1] = -((b * 48) + i);
            }

            ring.Write(block);
        }

        Assert.Equal(144, ring.Written);
        Assert.Equal(144 - 64 + 8, ring.OldestReadable(ring.Written));
        for (long f = ring.OldestReadable(ring.Written); f < ring.Written; f++)
        {
            Assert.Equal(f, ring.Read(f, 0));
            Assert.Equal(-f, ring.Read(f, 1));
        }

        ring.WriteSilence(10);
        Assert.Equal(0f, ring.Read(150, 0));
        Assert.Equal(143f, ring.Read(143, 0));
    }
}
