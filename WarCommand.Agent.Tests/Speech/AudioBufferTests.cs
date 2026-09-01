using WarCommand.Agent.Speech;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// The two promises the buffer makes: it is bounded at 8 seconds, and it is zeroed on release.
/// A PTT key that sticks must not allocate without limit, and audio must not linger in memory.
/// </summary>
public class AudioBufferTests
{
    [Fact]
    public void The_cap_is_eight_seconds_at_sixteen_kilohertz()
    {
        Assert.Equal(16_000, AudioBuffer.SampleRateHz);
        Assert.Equal(8, AudioBuffer.MaxDurationSeconds);
        Assert.Equal(128_000, AudioBuffer.MaxSamples);
    }

    [Fact]
    public void A_stuck_key_fills_the_buffer_and_then_drops_the_overflow()
    {
        using var buffer = new AudioBuffer();
        var chunk = new short[10_000];
        Array.Fill(chunk, (short)1000);

        var taken = 0;
        for (var i = 0; i < 100; i++)
        {
            taken += buffer.Append(chunk);
        }

        Assert.Equal(AudioBuffer.MaxSamples, taken);
        Assert.Equal(AudioBuffer.MaxSamples, buffer.Length);
        Assert.True(buffer.IsFull);
        Assert.Equal(TimeSpan.FromSeconds(8), buffer.Duration);
        Assert.Equal(0, buffer.Append(chunk));
    }

    [Fact]
    public void A_buffer_cannot_be_built_above_the_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBuffer(AudioBuffer.MaxSamples + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBuffer(0));
    }

    [Fact]
    public void Release_zeroes_every_sample()
    {
        var buffer = new AudioBuffer(4_000);
        buffer.Append(Ramp(4_000));
        Assert.Contains(buffer.Storage, s => s != 0);

        buffer.Dispose();

        Assert.True(buffer.IsDisposed);
        Assert.Equal(0, buffer.Length);
        Assert.All(buffer.Storage, s => Assert.Equal(0, s));
        Assert.True(double.IsNegativeInfinity(buffer.PeakDbfs));
    }

    [Fact]
    public void Reset_zeroes_every_sample_and_the_peak()
    {
        using var buffer = new AudioBuffer(4_000);
        buffer.Append(Ramp(4_000));

        buffer.Reset();

        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.IsSilent);
        Assert.All(buffer.Storage, s => Assert.Equal(0, s));
    }

    [Fact]
    public void A_released_buffer_refuses_to_take_more_audio()
    {
        var buffer = new AudioBuffer(64);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            buffer.Append(new short[8]);
        });
        Assert.Throws<ObjectDisposedException>(buffer.Reset);
    }

    [Fact]
    public void Peak_is_the_loudest_sample_written_and_silence_is_negative_infinity()
    {
        using var silent = new AudioBuffer(100);
        silent.Append(new short[100]);
        Assert.True(silent.IsSilent);
        Assert.True(double.IsNegativeInfinity(silent.PeakDbfs));

        using var loud = new AudioBuffer(100);
        loud.Append([0, 100, short.MaxValue, -20]);
        Assert.False(loud.IsSilent);
        Assert.Equal(0, loud.PeakDbfs, 3);

        using var quiet = new AudioBuffer(100);
        quiet.Append([(short)(short.MaxValue / 100)]);
        Assert.InRange(quiet.PeakDbfs, -41, -39);
    }

    [Fact]
    public void Samples_never_escape_as_an_array()
    {
        var members = typeof(AudioBuffer)
            .GetMembers(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            members.OfType<System.Reflection.PropertyInfo>(),
            p => p.PropertyType.IsArray);
        Assert.DoesNotContain(
            members.OfType<System.Reflection.MethodInfo>(),
            m => m.ReturnType.IsArray);
    }

    private static short[] Ramp(int count)
    {
        var samples = new short[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (short)(1 + (i % 1000));
        }

        return samples;
    }
}
