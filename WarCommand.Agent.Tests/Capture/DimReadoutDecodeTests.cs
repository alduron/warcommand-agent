using WarCommand.Agent.Capture;
using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// The whole decode stack against a readout painted from the game's OWN learned glyphs, at the ink
/// levels the map's gradient actually produces.
/// </summary>
/// <remarks>
/// The reported failure was that the readout only decodes near the middle of the map and goes blind
/// at the edges, where the text grays out. Reproducing that needs no game: the learned masks in
/// game-profile.json are the real shapes, so painting them at a lower ink value is the same input
/// the decoder sees at the border.
/// </remarks>
public class DimReadoutDecodeTests
{
    private const string TruthX = "x97.56";
    private const string TruthY = "y108.62";

    private static MapReadoutSection Readout => BundledContracts.GameProfile().Current.MapReadout;

    /// <summary>
    /// Bright text. This has always worked, and it is the control: if this breaks, the harness is
    /// wrong rather than the decoder.
    /// </summary>
    [Fact]
    public void A_bright_readout_decodes()
    {
        Assert.Equal((97.56m, 108.62m), Decode(ink: 250));
    }

    /// <summary>
    /// The map edge. Every one of these sits below the profile's fixed near_white_threshold of 240,
    /// which is the whole reported bug: perfectly legible text, no coordinate.
    /// </summary>
    [Theory]
    [InlineData(230)]
    [InlineData(210)]
    [InlineData(190)]
    [InlineData(170)]
    [InlineData(155)]
    public void A_dim_readout_decodes(int ink)
    {
        Assert.Equal((97.56m, 108.62m), Decode(ink));
    }

    /// <summary>
    /// The bug itself, pinned. Finding the run at one threshold and then measuring its ink at the
    /// profile's fixed one reads an empty box, whatever the ladder found.
    /// </summary>
    [Fact]
    public void Reading_a_dim_run_at_the_fixed_threshold_finds_nothing()
    {
        var readout = Readout;
        var frame = Paint(ink: 170);
        var reader = new ReadoutReader(readout);

        var blobs = NearWhiteScanner.Scan(frame, 165, glyphGap: readout.GlyphGapPx);
        Assert.NotEmpty(blobs);

        // Found at 165, read at 240: the old behavior, and it answers nothing.
        Assert.Null(reader.ReadPoint(frame, blobs, boundsMax: null, threshold: null));

        // Found at 165 and read at 165.
        Assert.NotNull(reader.ReadPoint(frame, blobs, boundsMax: null, threshold: 165));
    }

    /// <summary>
    /// Dim text still has to be MEASURED rather than assumed: a derived rung has to land on it, or
    /// the ladder is back to guessing.
    /// </summary>
    [Fact]
    public void A_derived_rung_lands_on_the_dim_text()
    {
        var readout = Readout;
        var frame = Paint(ink: 170);
        var rungs = MapReadoutCoordinateSource.Rungs(readout.NearWhiteLadder, readout, frame, Cursor);

        Assert.Contains(rungs, r => r <= 170 && r > 120);
    }

    /// <summary>
    /// Round digits, whose top row is one or two pixels each. The strict text band dropped that row
    /// and every one of these read as a different, legal, in-bounds coordinate, at every threshold
    /// and on every re-read.
    /// </summary>
    [Theory]
    [InlineData("x108.62", "y108.62", 108.62, 108.62)]
    [InlineData("x88.88", "y88.88", 88.88, 88.88)]
    [InlineData("x60.09", "y90.06", 60.09, 90.06)]
    [InlineData("x0.68", "y86.00", 0.68, 86.00)]
    public void A_run_of_round_digits_decodes_exactly(string xText, string yText, double x, double y)
    {
        Assert.Equal(((decimal)x, (decimal)y), Decode(250, xText, yText));
    }

    /// <summary>The full path: rungs, a decode per rung, and the vote across them.</summary>
    private static (decimal X, decimal Y)? Decode(int ink) => Decode(ink, TruthX, TruthY);

    private static (decimal X, decimal Y)? Decode(int ink, string xText, string yText)
    {
        var readout = Readout;
        var frame = Paint(xText, yText, ink);
        var reader = new ReadoutReader(readout);
        var xVotes = new List<Vote>();
        var yVotes = new List<Vote>();

        foreach (var threshold in MapReadoutCoordinateSource.Rungs(readout.NearWhiteLadder, readout, frame, Cursor))
        {
            var blobs = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx);
            foreach (var run in reader.Read(frame, blobs, threshold))
            {
                var vote = new Vote(run.Text, (decimal)run.WorstMargin);
                (run.Text.StartsWith('x') ? xVotes : yVotes).Add(vote);
            }
        }

        if (MapReadoutCoordinateSource.Consensus(xVotes) is not { } xAgreed
            || MapReadoutCoordinateSource.Consensus(yVotes) is not { } yAgreed)
        {
            return null;
        }

        return reader.PointFrom(xAgreed.Vote.Text, yAgreed.Vote.Text, 0m) is { } point
            ? (point.X, point.Y)
            : null;
    }

    private const int Width = 260;
    private const int Height = 120;
    private const int Pitch = 10;
    private const int XRunTop = 20;
    private const int YRunTop = 60;
    private const int RunLeft = 40;

    /// <summary>Where the crosshair would be: between the two halves, as the game draws them.</summary>
    private static (int X, int Y)? Cursor => (RunLeft + 30, 45);

    /// <summary>
    /// A frame carrying the two halves of a readout at the given ink, over a terrain-ish gradient.
    /// </summary>
    /// <remarks>
    /// Each glyph is the learned mask, centered in a fixed pitch cell, ringed in black the way the
    /// game rings its own text. The outline is why a low threshold stays safe and belongs in the
    /// fixture: without it the test would prove something easier than the real problem.
    /// </remarks>
    private static Frame Paint(int ink) => Paint(TruthX, TruthY, ink);

    private static Frame Paint(string xText, string yText, int ink)
    {
        var pixels = new byte[Width * Height * 4];

        for (var y = 0; y < Height; y++)
        {
            // The gradient the map has under the readout, well below any rung so it contributes no
            // runs of its own.
            var ground = (byte)(30 + (y * 60 / Height));
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                pixels[i] = ground;
                pixels[i + 1] = ground;
                pixels[i + 2] = ground;
                pixels[i + 3] = 255;
            }
        }

        Draw(pixels, xText, RunLeft, XRunTop, ink);
        Draw(pixels, yText, RunLeft, YRunTop, ink);
        return new Frame(pixels, Width, Height);
    }

    private static void Draw(byte[] pixels, string text, int left, int top, int ink)
    {
        var learned = Readout.Atlas.Learned;

        for (var index = 0; index < text.Length; index++)
        {
            var mask = learned[text[index].ToString()];
            var glyphWidth = mask.Max(r => r.Length);
            var cell = left + (index * Pitch) + ((Pitch - glyphWidth) / 2);

            // The black ring first, then the ink over it, so a stroke is never overwritten by the
            // outline of the stroke beside it.
            Ring(pixels, mask, cell, top);

            for (var y = 0; y < mask.Count; y++)
            {
                for (var x = 0; x < mask[y].Length; x++)
                {
                    if (mask[y][x] != '.')
                    {
                        Set(pixels, cell + x, top + y, (byte)ink);
                    }
                }
            }
        }
    }

    private static void Ring(byte[] pixels, IReadOnlyList<string> mask, int left, int top)
    {
        for (var y = 0; y < mask.Count; y++)
        {
            for (var x = 0; x < mask[y].Length; x++)
            {
                if (mask[y][x] == '.')
                {
                    continue;
                }

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        Set(pixels, left + x + dx, top + y + dy, 0);
                    }
                }
            }
        }
    }

    private static void Set(byte[] pixels, int x, int y, byte value)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        var i = ((y * Width) + x) * 4;
        pixels[i] = value;
        pixels[i + 1] = value;
        pixels[i + 2] = value;
    }
}
