using WarCommand.Agent.Capture;
using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// The near-white ladder votes, one axis at a time. It used to answer with the first rung that
/// produced a whole pair, which handed the answer to the rung least able to see a dim readout and
/// threw away every reading where the two halves were clean at different thresholds.
/// </summary>
public class LadderConsensusTests
{
    private static Vote Read(string text, decimal margin = 0.02m) => new(text, margin);

    [Fact]
    public void One_rung_reading_alone_is_answered()
    {
        var agreed = MapReadoutCoordinateSource.Consensus([Read("x97.56")]);

        Assert.NotNull(agreed);
        Assert.Equal("x97.56", agreed.Value.Vote.Text);
        Assert.Equal(1, agreed.Value.Agreeing);
    }

    [Fact]
    public void The_reading_most_rungs_agree_on_wins_over_the_first_one()
    {
        // The top rung erodes a glyph and reads 35 where the text says 85. It is first, and it used
        // to win outright.
        var agreed = MapReadoutCoordinateSource.Consensus(
        [
            Read("x35.53"),
            Read("x85.53"),
            Read("x85.53"),
        ]);

        Assert.NotNull(agreed);
        Assert.Equal("x85.53", agreed.Value.Vote.Text);
        Assert.Equal(2, agreed.Value.Agreeing);
    }

    [Fact]
    public void A_tie_between_two_readings_refuses()
    {
        var agreed = MapReadoutCoordinateSource.Consensus(
        [
            Read("x35.53"),
            Read("x85.53"),
        ]);

        Assert.Null(agreed);
    }

    /// <summary>
    /// The live tallies, both of them, from the map's bottom right corner. The owner then read the
    /// x half off the screen: 96.60, the reading with ONE vote and the better margin.
    /// </summary>
    /// <remarks>
    /// More rungs producing a reading is not corroboration. The extra rungs thicken or erode the
    /// strokes, and a thickened 6 closes into an 8, so the distorted reading is the one more
    /// thresholds agree on. Counting them picked 8 both times.
    /// </remarks>
    [Theory]
    [InlineData("x96.60", 0.14, 1, "x96.80", 0.12, 2)]
    [InlineData("x96.65", 0.14, 1, "x96.85", 0.09, 2)]
    public void The_better_margin_beats_the_bigger_count(
        string truth,
        double truthMargin,
        int truthVotes,
        string distorted,
        double distortedMargin,
        int distortedVotes)
    {
        List<Vote> votes = [];
        for (var i = 0; i < truthVotes; i++)
        {
            votes.Add(new Vote(truth, (decimal)truthMargin));
        }

        for (var i = 0; i < distortedVotes; i++)
        {
            votes.Add(new Vote(distorted, (decimal)distortedMargin));
        }

        var agreed = MapReadoutCoordinateSource.Consensus(votes);

        Assert.NotNull(agreed);
        Assert.Equal(truth, agreed.Value.Vote.Text);
        Assert.True(agreed.Value.Opposed, "a split axis has to be marked as contested");
    }

    /// <summary>A contested axis must never report certainty. It reported 1.00.</summary>
    [Fact]
    public void An_unopposed_axis_is_still_never_certain()
    {
        var agreed = MapReadoutCoordinateSource.Consensus([Read("y110.81", 0.31m), Read("y110.81", 0.31m)]);

        Assert.NotNull(agreed);
        Assert.False(agreed.Value.Opposed);
    }

    [Fact]
    public void No_rung_decoding_refuses()
    {
        Assert.Null(MapReadoutCoordinateSource.Consensus([]));
    }
}

/// <summary>
/// The derived rungs. A fixed ladder guesses where the text sits on the map's gradient; these
/// measure it off the frame.
/// </summary>
public class DerivedRungTests
{
    private static MapReadoutSection Readout(params decimal[] ratios) =>
        BundledContracts.GameProfile().Current.MapReadout with
        {
            NearWhiteLadder = [240, 180],
            NearWhiteRelativeRatios = ratios,
            NearWhiteFloor = 120,
            SearchRadiusPx = 20,
        };

    /// <summary>A gray field with one brighter square at the center, which is the readout's core.</summary>
    private static Frame Field(byte background, byte peak)
    {
        var pixels = new byte[40 * 40 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = background;
            pixels[i + 1] = background;
            pixels[i + 2] = background;
            pixels[i + 3] = 255;
        }

        for (var y = 19; y <= 21; y++)
        {
            for (var x = 19; x <= 21; x++)
            {
                var i = ((y * 40) + x) * 4;
                pixels[i] = peak;
                pixels[i + 1] = peak;
                pixels[i + 2] = peak;
            }
        }

        return new Frame(pixels, 40, 40);
    }

    [Fact]
    public void A_dim_readout_gets_rungs_the_fixed_ladder_never_reaches()
    {
        var rungs = MapReadoutCoordinateSource.Rungs(
            [240, 180], Readout(0.9m, 0.8m, 0.7m), Field(background: 40, peak: 200), (20, 20));

        // 200 * 0.9, 0.8, 0.7. The fixed ladder bottoms out at 180 and would never have looked here.
        Assert.Contains(180, rungs);
        Assert.Contains(160, rungs);
        Assert.Contains(140, rungs);
        Assert.Equal([240, 180, 160, 140], rungs);
    }

    [Fact]
    public void A_derived_rung_under_the_floor_is_dropped()
    {
        var rungs = MapReadoutCoordinateSource.Rungs(
            [240, 180], Readout(0.9m, 0.7m), Field(background: 20, peak: 150), (20, 20));

        Assert.Contains(135, rungs);
        Assert.DoesNotContain(105, rungs);
    }

    [Fact]
    public void No_ratios_leaves_the_fixed_ladder_alone()
    {
        var rungs = MapReadoutCoordinateSource.Rungs(
            [240, 180], Readout(), Field(background: 40, peak: 200), (20, 20));

        Assert.Equal([240, 180], rungs);
    }

    [Fact]
    public void A_saturated_color_is_bright_and_is_not_text()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        Assert.Equal(0, new Frame(pixels, 4, 4).PeakNearWhite(0, 0, 4, 4));
    }
}
