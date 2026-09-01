using WarCommand.Agent.Speech;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// A device that exists and delivers silence is a fault, and today it looks like hesitation.
/// The counter is consecutive, never cumulative.
/// </summary>
public class SilentHoldMonitorTests
{
    private const string Device = "Yeti Nano";

    private static SilentHoldMonitor New() => new(ContractFixtures.Profile.Speech);

    [Fact]
    public void Both_numbers_come_from_the_served_profile()
    {
        var monitor = New();

        Assert.Equal((double)ContractFixtures.Profile.Speech.NoiseFloorDbfs, monitor.NoiseFloorDbfs);
        Assert.Equal(ContractFixtures.Profile.Speech.SilentHoldsBeforeWarning, monitor.HoldsBeforeWarning);
    }

    [Fact]
    public void The_warning_is_raised_on_the_configured_consecutive_hold()
    {
        var monitor = New();
        var silent = monitor.NoiseFloorDbfs - 10;

        for (var hold = 1; hold < monitor.HoldsBeforeWarning; hold++)
        {
            Assert.Equal(SilentHoldResult.Silent, monitor.Hold(silent, Device));
            Assert.False(monitor.WarningActive);
        }

        Assert.Equal(SilentHoldResult.WarningRaised, monitor.Hold(silent, Device));
        Assert.True(monitor.WarningActive);
        Assert.Equal("NO AUDIO FROM YETI NANO", monitor.Warning);
    }

    [Fact]
    public void The_count_is_consecutive_and_not_cumulative()
    {
        var monitor = New();
        var silent = monitor.NoiseFloorDbfs - 10;
        var loud = monitor.NoiseFloorDbfs + 10;

        // One hold where somebody thought better of it must not accumulate over a whole match.
        for (var round = 0; round < 20; round++)
        {
            Assert.Equal(SilentHoldResult.Silent, monitor.Hold(silent, Device));
            Assert.Equal(SilentHoldResult.HadAudio, monitor.Hold(loud, Device));
        }

        Assert.Equal(0, monitor.ConsecutiveSilentHolds);
        Assert.False(monitor.WarningActive);
    }

    [Fact]
    public void Any_hold_with_audio_in_it_clears_the_warning()
    {
        var monitor = New();
        var silent = monitor.NoiseFloorDbfs - 10;

        for (var hold = 0; hold < monitor.HoldsBeforeWarning + 3; hold++)
        {
            monitor.Hold(silent, Device);
        }

        Assert.True(monitor.WarningActive);

        Assert.Equal(SilentHoldResult.HadAudio, monitor.Hold(monitor.NoiseFloorDbfs + 1, Device));
        Assert.False(monitor.WarningActive);
        Assert.Null(monitor.Warning);
        Assert.Equal(0, monitor.ConsecutiveSilentHolds);
    }

    [Fact]
    public void A_peak_exactly_on_the_floor_is_silence()
    {
        var monitor = New();

        Assert.Equal(SilentHoldResult.Silent, monitor.Hold(monitor.NoiseFloorDbfs, Device));
        Assert.Equal(1, monitor.ConsecutiveSilentHolds);
    }

    [Fact]
    public void A_buffer_that_took_no_audio_reads_as_a_silent_hold()
    {
        var monitor = New();
        using var buffer = new AudioBuffer(1_000);
        buffer.Append(new short[1_000]);

        Assert.Equal(SilentHoldResult.Silent, monitor.Hold(buffer, Device));
    }

    [Fact]
    public void A_buffer_with_speech_in_it_reads_as_audio()
    {
        var monitor = New();
        using var buffer = new AudioBuffer(1_000);
        var samples = new short[1_000];
        Array.Fill(samples, (short)8_000);
        buffer.Append(samples);

        Assert.Equal(SilentHoldResult.HadAudio, monitor.Hold(buffer, Device));
    }

    [Fact]
    public void Reset_drops_the_count_and_the_warning()
    {
        var monitor = New();
        for (var hold = 0; hold < monitor.HoldsBeforeWarning; hold++)
        {
            monitor.Hold(monitor.NoiseFloorDbfs - 20, Device);
        }

        monitor.Reset();

        Assert.False(monitor.WarningActive);
        Assert.Equal(0, monitor.ConsecutiveSilentHolds);
    }

    [Fact]
    public void An_unnamed_device_still_produces_a_readable_line()
    {
        var monitor = New();
        for (var hold = 0; hold < monitor.HoldsBeforeWarning; hold++)
        {
            monitor.Hold(monitor.NoiseFloorDbfs - 20, "   ");
        }

        Assert.Equal("NO AUDIO FROM MICROPHONE", monitor.Warning);
    }
}
