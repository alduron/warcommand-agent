using WarCommand.Agent.Speech;
using WarCommand.Agent.Speech.Capture;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// WASAPI shared mode delivers the device's mix format and the recognizer accepts one format.
/// Timing-sensitive capture is out of scope for CI; the arithmetic between them is not.
/// </summary>
public class PcmResamplerTests
{
    [Fact]
    public void A_sixteen_kilohertz_mono_source_passes_through_unchanged()
    {
        var resampler = new PcmResampler(new PcmFormat(16_000, 1, 16, IsFloat: false));
        var source = Pcm16(mono: [0, 1000, -1000, 32000, -32000]);
        var destination = new short[16];

        var written = resampler.Convert(source, destination);

        Assert.Equal(5, written);
        Assert.Equal(new short[] { 0, 1000, -1000, 32000, -32000 }, destination[..written]);
    }

    [Fact]
    public void Forty_eight_kilohertz_decimates_by_three()
    {
        var resampler = new PcmResampler(new PcmFormat(48_000, 1, 16, IsFloat: false));
        var source = Pcm16(mono: new short[3_000]);
        var destination = new short[2_000];

        var written = resampler.Convert(source, destination);

        Assert.InRange(written, 999, 1_001);
    }

    [Fact]
    public void Stereo_is_averaged_into_one_channel()
    {
        var resampler = new PcmResampler(new PcmFormat(16_000, 2, 16, IsFloat: false));
        var source = Pcm16(mono: [1000, 3000, -2000, 2000]);
        var destination = new short[8];

        var written = resampler.Convert(source, destination);

        Assert.Equal(2, written);
        Assert.InRange(destination[0], 1_999, 2_001);
        Assert.InRange(destination[1], -1, 1);
    }

    [Fact]
    public void Float_input_is_scaled_into_sixteen_bit()
    {
        var resampler = new PcmResampler(new PcmFormat(16_000, 1, 32, IsFloat: true));
        var source = new byte[3 * sizeof(float)];
        BitConverter.GetBytes(0f).CopyTo(source, 0);
        BitConverter.GetBytes(0.5f).CopyTo(source, sizeof(float));
        BitConverter.GetBytes(-1f).CopyTo(source, 2 * sizeof(float));
        var destination = new short[8];

        var written = resampler.Convert(source, destination);

        Assert.Equal(3, written);
        Assert.Equal((short)0, destination[0]);
        Assert.Equal((short)16_384, destination[1]);
        Assert.Equal(short.MinValue, destination[2]);
    }

    [Fact]
    public void A_full_scale_tone_survives_the_conversion_with_its_level_intact()
    {
        var resampler = new PcmResampler(new PcmFormat(48_000, 2, 32, IsFloat: true));
        var source = FloatTone(1_000, 48_000, 4_800, 2);
        var destination = new short[4_000];

        var written = resampler.Convert(source, destination);

        var peak = 0;
        for (var i = 0; i < written; i++)
        {
            peak = Math.Max(peak, Math.Abs((int)destination[i]));
        }

        Assert.InRange(written, 1_590, 1_610);
        Assert.InRange(peak / 32_768.0, 0.9, 1.0);
    }

    [Fact]
    public void Phase_is_carried_across_callbacks_so_the_sample_count_does_not_drift()
    {
        var resampler = new PcmResampler(new PcmFormat(44_100, 1, 16, IsFloat: false));
        var destination = new short[8_000];
        var total = 0;

        // 441 source samples is exactly 10 ms. A hundred of them is one second, which must produce
        // 16 000 output samples give or take one, not 100 truncations.
        for (var callback = 0; callback < 100; callback++)
        {
            total += resampler.Convert(Pcm16(mono: new short[441]), destination);
        }

        Assert.InRange(total, 15_999, 16_001);
    }

    [Fact]
    public void An_unsupported_layout_is_refused_rather_than_read_as_noise()
    {
        Assert.Throws<NotSupportedException>(() => new PcmResampler(new PcmFormat(48_000, 2, 20, IsFloat: false)));
        Assert.Throws<NotSupportedException>(() => new PcmResampler(new PcmFormat(48_000, 2, 16, IsFloat: true)));
    }

    [Fact]
    public void The_target_is_the_one_rate_the_recognizer_accepts()
    {
        var resampler = new PcmResampler(new PcmFormat(48_000, 2, 32, IsFloat: true));

        Assert.Equal(AudioBuffer.SampleRateHz, resampler.TargetSampleRateHz);
        Assert.Equal(8, resampler.Source.BytesPerFrame);
    }

    private static byte[] Pcm16(short[] mono)
    {
        var bytes = new byte[mono.Length * sizeof(short)];
        Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] FloatTone(double hz, int sampleRate, int frames, int channels)
    {
        var bytes = new byte[frames * channels * sizeof(float)];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)Math.Sin(2 * Math.PI * hz * frame / sampleRate);
            for (var channel = 0; channel < channels; channel++)
            {
                BitConverter.GetBytes(value).CopyTo(bytes, ((frame * channels) + channel) * sizeof(float));
            }
        }

        return bytes;
    }
}
