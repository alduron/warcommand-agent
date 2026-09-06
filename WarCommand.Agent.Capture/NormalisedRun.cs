namespace WarCommand.Agent.Capture;

/// <summary>
/// A found run, redrawn at the size the glyph atlas was cut at.
/// </summary>
/// <remarks>
/// The atlas is the game's own glyphs, cut once at one UI scale on one 2560x1440 machine.
/// Everything the solver does is scale free in principle, cells are resampled and pitch is bounded
/// as a ratio, but in practice a bigger run is not the same input:
/// <list type="bullet">
/// <item>The MARGIN decays. Measured on the same readout at one through six times the learned
/// size: 0.29, 0.16, 0.11, 0.08, 0.07, 0.06, every one of them decoding correctly. A floor set
/// from the 1x number silently refuses a correct reading on anybody running a larger UI.</item>
/// <item>The COST grows with the width. The split solver is quadratic in the run's width, so a
/// six times run took a minute where the learned size takes milliseconds, on a path that runs
/// three times per key press.</item>
/// </list>
/// Normalising the run to the reference height removes both. It is the same 1-bit shape, sampled
/// to the size the atlas already knows, so what reaches the solver is what it was tuned against
/// whatever monitor the player is on.
/// </remarks>
public static class NormalisedRun
{
    /// <summary>A run this much taller than the reference is worth normalising.</summary>
    private const double Tolerance = 1.25;

    /// <summary>Mask frames are 1-bit: this separates the two values FromMask writes.</summary>
    public const int MaskThreshold = 128;

    /// <summary>
    /// The run at the reference height, or null when it is already close enough to it.
    /// </summary>
    /// <param name="referenceHeight">The line height the atlas was cut at.</param>
    public static (Frame Frame, TextBlob Blob)? For(
        Frame frame,
        TextBlob blob,
        int threshold,
        int referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(blob);

        if (referenceHeight <= 0 || blob.Height <= referenceHeight * Tolerance)
        {
            return null;
        }

        var scale = referenceHeight / (double)blob.Height;
        var width = Math.Max(4, (int)Math.Round(blob.Width * scale, MidpointRounding.AwayFromZero));
        var height = referenceHeight;

        var mask = frame.MaskOf(blob, threshold);
        var rows = new List<string>(height);

        for (var y = 0; y < height; y++)
        {
            // Nearest neighbour, deliberately. The source is already 1-bit, so there is nothing to
            // average: interpolating would invent grey the threshold would then have to guess at.
            var sourceY = Math.Min(mask.Count - 1, (int)((y + 0.5) / scale));
            var source = mask[sourceY];
            var row = new char[width];

            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(source.Length - 1, (int)((x + 0.5) / scale));
                row[x] = source[sourceX];
            }

            rows.Add(new string(row));
        }

        var normalised = Frame.FromMask(rows);
        return (normalised, new TextBlob(0, 0, normalised.Width - 1, normalised.Height - 1, blob.PixelCount));
    }
}
