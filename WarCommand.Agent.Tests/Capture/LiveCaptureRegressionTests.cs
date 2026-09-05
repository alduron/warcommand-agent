using WarCommand.Agent.Capture;
using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// One real capture, from a live client, of the readout at y107.61 x99.90.
/// </summary>
/// <remarks>
/// These are MASKS, the 1-bit shapes the scanner isolated, so binding rule 3 holds: no frame
/// reached disk and none is committed here. What they pin is the reading the owner read off the
/// screen with their own eyes, which is the only ground truth this decoder has ever had.
/// <para>
/// The two halves came out of the SAME capture at different thresholds: x99.90 was legible at 150
/// and y107.61 at 179, and at no rung on the ladder were both. Every decode before this test
/// refused the pair.
/// </para>
/// </remarks>
public class LiveCaptureRegressionTests
{
    private static MapReadoutSection Readout => BundledContracts.GameProfile().Current.MapReadout;

    [Fact]
    public void The_x_half_reads_what_the_owner_read()
    {
        Assert.Equal("x99.90", Decode(XHalfAt150));
    }

    [Fact]
    public void The_y_half_reads_what_the_owner_read()
    {
        Assert.Equal("y107.61", Decode(YHalfAt179));
    }

    /// <summary>The pair, assembled from halves that no single threshold could produce together.</summary>
    [Fact]
    public void The_halves_assemble_into_the_point()
    {
        var reader = new ReadoutReader(Readout);
        var point = reader.PointFrom(Decode(XHalfAt150), Decode(YHalfAt179), margin: 0.15m);

        Assert.NotNull(point);
        Assert.Equal(99.90m, point.Value.X);
        Assert.Equal(107.61m, point.Value.Y);
    }

    private static string? Decode(string[] mask)
    {
        var readout = Readout;
        var frame = Frame.FromMask(mask);
        var blobs = NearWhiteScanner.Scan(frame, readout.NearWhiteThreshold, glyphGap: readout.GlyphGapPx);

        var runs = new ReadoutReader(readout).Read(frame, blobs);
        return runs.Count == 0 ? null : runs[0].Text;
    }

    private static readonly string[] XHalfAt150 =
    [
        "..............###........###..............##..........##...",
        "............###.##.....###.##...........###.##......##..##.",
        "............#.....#....#.....#..........#.....#....##....##",
        "...........##.....#...##.....#.........##.....#....#......#",
        "##.....#...##.....#...##.....#.........##.....#....#......#",
        ".#....##...##.....#...##.....#.........##.....#....#......#",
        ".##..##.....#....##....#....##..........#.....#....#......#",
        "..##.#......#######....###.###..........###.###....#......#",
        "...###........##..#......##..#............##..#....#......#",
        "...##.............#..........#................#....#......#",
        "...###............#..........#................#....#......#",
        "..##.#......#.....#....#.....#..........#.....#....#......#",
        ".##...#.....#.....#....#.....#....##....#.....#....#.....##",
        ".#....##....#######.....##.###....#......##.###.....##..##.",
        "##.....#......###........###...............##.........##...",
    ];

    private static readonly string[] YHalfAt179 =
    [
        ".............##......###......########...........###.......##",
        "...........####.....######....##....##.........###.###....###",
        "...........#..#....##....##...#......#.........#.....#......#",
        "..............#....#......#.........##.........#.....#......#",
        "#......#......#....#......#.........#..........#............#",
        "##....##......#....#......#........##..........#............#",
        "##....#.......#....#......#........##..........######.......#",
        ".#....#.......#....#......#........#...........##....#......#",
        ".##..##.......#....#......#.......##...........#.....#......#",
        ".##..#........#....#......#.......##...........#.....##.....#",
        "..#..#........#....#......#.......#............#.....##.....#",
        "..#..#........#....#......#......##............#.....#......#",
        "..###.........#....##....##......#.......##....#.....#......#",
        "...##.........#.....######.......#.......#......##.##.......#",
        "...##.........#......###........##................##........#",
        "...#.........................................................",
        "...#.........................................................",
        ".###.........................................................",
        "##...........................................................",
    ];
}
