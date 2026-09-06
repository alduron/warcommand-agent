using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Capture;

/// <summary>
/// The profile's measured pixels, read as ratios and resolved for the screen actually in front of
/// the player.
/// </summary>
/// <remarks>
/// Every pixel number in map_readout was measured once, on one 2560x1440 machine. Taken literally
/// they are true there and quietly wrong everywhere else, and the failure is SILENCE: the scanner
/// throws a run away for being the wrong size and the decoder never sees the text at all, so the
/// tool reports no coordinate rather than an error anybody could act on.
/// <para>
/// A game UI scales with vertical resolution, so the numbers scale with it too. The blob height
/// window is deliberately generous on top of that, because it is the only thing covering the
/// game's own UI scale slider, which moves the HUD without moving the resolution. Everything after
/// the scan is already scale free: cells are resampled to a fixed grid and the pitch bound is a
/// ratio of the line height, so a wide window costs a few extra candidate blobs and nothing else.
/// </para>
/// </remarks>
public readonly record struct ReadoutGeometry(
    int SearchRadiusPx,
    int GlyphGapPx,
    int MinBlobHeight,
    int MaxBlobHeight,
    double Scale)
{
    /// <summary>
    /// The geometry for a client of this height. A height of zero falls back to the measured
    /// numbers, which is the old behaviour and the right answer when there is no window to measure.
    /// </summary>
    public static ReadoutGeometry For(MapReadoutSection readout, int clientHeight)
    {
        ArgumentNullException.ThrowIfNull(readout);

        var reference = Math.Max(1, readout.MeasuredAtClientHeight);
        var scale = clientHeight > 0 ? clientHeight / (double)reference : 1.0;

        // Clamped, but generously. Freezing the scale is not the safe option it looks like: a
        // merge gap that stops growing splits one run into fragments the solver can never
        // reassemble, which is a silent no-read. Overshooting only risks pulling in a neighbouring
        // piece of HUD text, which the pattern and the bounds gate then reject out loud.
        scale = Math.Clamp(scale, 0.5, 8.0);

        var line = Math.Max(1.0, readout.LineHeightPx * scale);

        return new ReadoutGeometry(
            SearchRadiusPx: AtLeast(1, readout.SearchRadiusPx * scale),
            GlyphGapPx: AtLeast(1, readout.GlyphGapPx * scale),
            MinBlobHeight: AtLeast(3, line * readout.BlobHeightMinScale),
            MaxBlobHeight: AtLeast(8, line * readout.BlobHeightMaxScale),
            Scale: scale);
    }

    private static int AtLeast(int floor, double value) =>
        Math.Max(floor, (int)Math.Round(value, MidpointRounding.AwayFromZero));
}
