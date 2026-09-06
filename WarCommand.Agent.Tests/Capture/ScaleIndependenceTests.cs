using WarCommand.Agent.Capture;
using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// The readout must decode at any UI scale, on any screen shape.
/// </summary>
/// <remarks>
/// The learned glyphs were cut at one scale on one 2560x1440 machine. Everything the decoder does
/// after finding a run is scale free by construction, cells are resampled and the pitch bound is a
/// ratio, but the SCAN in front of it is not: it takes a pixel merge gap and pixel height bounds.
/// A player on another monitor, or with the game's UI scale moved, hands it glyphs of a different
/// size, and none of these numbers were measured against that.
/// </remarks>
public class ScaleIndependenceTests
{
    private static MapReadoutSection Readout => BundledContracts.GameProfile().Current.MapReadout;

    private const string TruthX = "x97.56";
    private const string TruthY = "y108.62";

    /// <summary>
    /// One scale per row: what the same readout decodes as when the glyphs are bigger or smaller.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void The_readout_decodes_at_any_glyph_size(int scale)
    {
        var (x, y) = Decode(scale);

        Assert.Equal("x97.56", x);
        Assert.Equal("y108.62", y);
    }

    /// <summary>Reads both halves off a frame painted at the given integer scale.</summary>
    private static (string? X, string? Y) Decode(int scale)
    {
        var readout = Readout;
        var frame = Paint(scale);
        var reader = new ReadoutReader(readout);

        string? x = null;
        string? y = null;

        // The client the player is on, at this scale. 1440 is what the numbers were measured at.
        var geometry = ReadoutGeometry.For(readout, 1440 * scale);

        foreach (var threshold in readout.NearWhiteLadder)
        {
            var blobs = NearWhiteScanner.Scan(
                frame,
                threshold,
                minHeight: geometry.MinBlobHeight,
                maxHeight: geometry.MaxBlobHeight,
                glyphGap: geometry.GlyphGapPx);

            foreach (var run in reader.Read(frame, blobs, threshold))
            {
                if (run.Text.StartsWith('x'))
                {
                    x ??= run.Text;
                }
                else if (run.Text.StartsWith('y'))
                {
                    y ??= run.Text;
                }
            }
        }

        return (x, y);
    }

    /// <summary>
    /// The two halves at an integer multiple of the learned size, over a dark ground.
    /// </summary>
    internal static Frame PaintFor(int scale) => Paint(scale);

    private static Frame Paint(int scale)
    {
        const int pitch = 10;
        var width = 400 * scale;
        var height = 160 * scale;
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 30;
            pixels[i + 1] = 30;
            pixels[i + 2] = 30;
            pixels[i + 3] = 255;
        }

        Draw(pixels, width, height, TruthX, 40 * scale, 20 * scale, pitch * scale, scale);
        Draw(pixels, width, height, TruthY, 40 * scale, 70 * scale, pitch * scale, scale);
        return new Frame(pixels, width, height);
    }

    private static void Draw(
        byte[] pixels, int width, int height, string text, int left, int top, int pitch, int scale)
    {
        var learned = Readout.Atlas.Learned;

        for (var index = 0; index < text.Length; index++)
        {
            var mask = learned[text[index].ToString()];
            var glyphWidth = mask.Max(r => r.Length) * scale;
            var cell = left + (index * pitch) + ((pitch - glyphWidth) / 2);

            for (var y = 0; y < mask.Count; y++)
            {
                for (var x = 0; x < mask[y].Length; x++)
                {
                    if (mask[y][x] == '.')
                    {
                        continue;
                    }

                    // One mask pixel becomes a scale x scale block, which is what a UI scale does.
                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            Set(pixels, width, height, cell + (x * scale) + dx, top + (y * scale) + dy, 250);
                        }
                    }
                }
            }
        }
    }

    private static void Set(byte[] pixels, int width, int height, int x, int y, byte value)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var i = ((y * width) + x) * 4;
        pixels[i] = value;
        pixels[i + 1] = value;
        pixels[i + 2] = value;
    }
}
