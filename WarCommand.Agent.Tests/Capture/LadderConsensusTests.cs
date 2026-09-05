using WarCommand.Agent.Capture;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// The near-white ladder votes. It used to answer with the first rung that decoded, which handed
/// the answer to the rung least able to see a dim edge readout.
/// </summary>
public class LadderConsensusTests
{
    private static Vote Read(string text, decimal x, decimal y, decimal margin = 0.02m) =>
        new(text, x, y, margin);

    [Fact]
    public void One_rung_reading_alone_is_answered()
    {
        var agreed = MapReadoutCoordinateSource.Consensus([Read("x97.56 y108.62", 97.56m, 108.62m)]);

        Assert.NotNull(agreed);
        Assert.Equal("x97.56 y108.62", agreed.Value.Vote.RawText);
        Assert.Equal(1, agreed.Value.Agreeing);
    }

    [Fact]
    public void The_reading_most_rungs_agree_on_wins_over_the_first_one()
    {
        // The top rung erodes a glyph and reads 35 where the text says 85. It is first, and it used
        // to win outright.
        var agreed = MapReadoutCoordinateSource.Consensus(
        [
            Read("x35.53 y108.62", 35.53m, 108.62m),
            Read("x85.53 y108.62", 85.53m, 108.62m),
            Read("x85.53 y108.62", 85.53m, 108.62m),
        ]);

        Assert.NotNull(agreed);
        Assert.Equal(85.53m, agreed.Value.Vote.X);
        Assert.Equal(2, agreed.Value.Agreeing);
    }

    [Fact]
    public void A_tie_between_two_readings_refuses()
    {
        var agreed = MapReadoutCoordinateSource.Consensus(
        [
            Read("x35.53 y108.62", 35.53m, 108.62m),
            Read("x85.53 y108.62", 85.53m, 108.62m),
        ]);

        Assert.Null(agreed);
    }

    [Fact]
    public void No_rung_decoding_refuses()
    {
        Assert.Null(MapReadoutCoordinateSource.Consensus([]));
    }
}
