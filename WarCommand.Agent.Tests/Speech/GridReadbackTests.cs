using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Speech.Readback;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// Readback defaults on for spoken_grid points only. Reading back a number the user did not say
/// tells them nothing they can check and costs a second of the glance budget on every request.
/// </summary>
public class GridReadbackTests
{
    [Fact]
    public void The_policy_comes_from_the_served_profile()
    {
        var readback = New(out _);

        Assert.Equal("spoken_grid_only", ContractFixtures.Profile.PointConfidence.TtsReadbackDefault);
        Assert.Equal("spoken_grid_only", readback.Policy);
    }

    [Theory]
    [InlineData("spoken_grid", true)]
    [InlineData("map_readout", false)]
    [InlineData("typed_grid", false)]
    [InlineData(null, false)]
    public void Only_a_spoken_grid_is_read_back(string? source, bool expected)
    {
        Assert.Equal(expected, New(out _).DefaultsOnFor(source));
    }

    [Fact]
    public void A_spoken_grid_is_spoken_digit_by_digit()
    {
        var readback = New(out var tts);

        Assert.True(readback.Read(new MapPoint(85.53m, 69.42m, "spoken_grid", "85.53 69.42", 0.9m)));
        Assert.Equal("eight five point five three, six nine point four two", tts.LastSpoken);
    }

    [Fact]
    public void A_captured_point_is_never_read_back()
    {
        var readback = New(out var tts);

        Assert.False(readback.Read(new MapPoint(85.53m, 69.42m, "map_readout", "x85.53", 0.9m)));
        Assert.Null(tts.LastSpoken);
    }

    [Fact]
    public void The_toggle_turns_it_off_over_the_top_of_the_policy()
    {
        var readback = New(out var tts);
        readback.Enabled = false;

        Assert.False(readback.Read(new MapPoint(1m, 2m, "spoken_grid", null, null)));
        Assert.Null(tts.LastSpoken);
    }

    [Fact]
    public void No_synthesizer_means_no_readback_and_no_failure()
    {
        var readback = new GridReadback(NullTextToSpeech.Instance, ContractFixtures.Profile);

        Assert.False(readback.Read(new MapPoint(1m, 2m, "spoken_grid", null, null)));
    }

    [Theory]
    [InlineData(85.53, "eight five point five three")]
    [InlineData(7.5, "seven point five zero")]
    [InlineData(0, "zero point zero zero")]
    [InlineData(123.4, "one two three point four zero")]
    public void An_axis_is_spelled_out_one_digit_at_a_time(double value, string expected)
    {
        Assert.Equal(expected, GridReadback.Spell((decimal)value));
    }

    private static GridReadback New(out RecordingTts tts)
    {
        tts = new RecordingTts();
        return new GridReadback(tts, ContractFixtures.Profile);
    }

    /// <summary>A fake, not a mock: it stores what it was told to say so the test can read it.</summary>
    private sealed class RecordingTts : ITextToSpeech
    {
        public bool IsAvailable => true;

        public string? LastSpoken { get; private set; }

        public void Speak(string text) => LastSpoken = text;

        public void Cancel() => LastSpoken = null;

        public void Dispose()
        {
        }
    }
}
