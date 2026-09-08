using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// End to end through the XAML: rows in, painted glyphs out. Every other test on this path checks
/// one hop, and the overlay drew grey shapeless rows anyway because the hops disagreed.
/// </summary>
public class BoardViewRoleGlyphRenderTests
{
    private static readonly Color Neutral = Color.FromRgb(0xB4, 0xB6, 0xB8);

    private static readonly RoleGlyphSource Glyphs =
        new(BundledContracts.Catalog().Current.Role);

    /// <summary>
    /// Rows on two different roles, resolved through the served catalog.
    /// </summary>
    /// <remarks>
    /// Two roles at least, and roles whose hues differ: one hue on every row is exactly what a
    /// broken lookup looks like, so a single-role fixture would pass while the board was grey.
    /// </remarks>
    private static IReadOnlyList<BoardRowViewModel> Rows { get; } =
    [
        new BoardRowViewModel
        {
            SlotDisplay = "1",
            RoleId = "mortar",
            TypeAndQualifier = "MORTAR",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "12s",
            TicketCode = "MTR-14",
        }.WithGlyph(Glyphs),
        new BoardRowViewModel
        {
            SlotDisplay = "2",
            RoleId = "medic",
            TypeAndQualifier = "MEDIC",
            CoordinatesDisplay = "x12.10 y44.02",
            Requester = "Wolf",
            AgeDisplay = "4s",
            TicketCode = "MED-02",
        }.WithGlyph(Glyphs),
    ];

    private static IReadOnlyList<BoardRowViewModel> Yours { get; } =
    [
        new BoardRowViewModel
        {
            SlotDisplay = "3",
            RoleId = "ground_transport",
            TypeAndQualifier = "TRANSPORT",
            CoordinatesDisplay = "x51.00 y61.40",
            Requester = "Bear",
            AgeDisplay = "31s",
            TicketCode = "TPT-16",
        }.WithGlyph(Glyphs),
    ];

    [Fact]
    public void The_board_paints_a_glyph_and_a_role_hue_on_every_row()
    {
        OnStaThread(() =>
        {
            var paths = RenderedGlyphPaths();

            Assert.NotEmpty(paths);

            // One or two paths per row: d2 is empty for some roles and the row draws d1 alone.
            var rows = Rows.Count + Yours.Count;
            var painted = paths.Where(p => p.Data is not null).ToList();
            Assert.InRange(painted.Count, rows, rows * 2);

            foreach (var path in painted)
            {
                var stroke = Assert.IsAssignableFrom<SolidColorBrush>(path.Stroke);
                Assert.NotEqual(Neutral, stroke.Color);
            }
        });
    }

    /// <summary>Two hues on one board. One hue everywhere is what a broken lookup looks like.</summary>
    [Fact]
    public void The_hues_on_the_board_differ_by_role()
    {
        OnStaThread(() =>
        {
            var hues = RenderedGlyphPaths()
                .Where(p => p.Data is not null)
                .Select(p => ((SolidColorBrush)p.Stroke).Color)
                .Distinct()
                .ToList();

            Assert.True(hues.Count > 1, "every row painted the same hue");
        });
    }

    private static List<ShapePath> RenderedGlyphPaths()
    {
        var view = new BoardView();
        view.RenderBoard(Rows, Yours, 0, 0);

        view.Measure(new Size(400, 900));
        view.Arrange(new Rect(0, 0, 400, 900));
        view.UpdateLayout();

        var found = new List<ShapePath>();
        Walk(view, found);
        return found;
    }

    /// <summary>
    /// Glyph paths only: the ones inside a row's glyph Canvas. Line 2 carries a plain Path for the
    /// second-point arrow, which is not a glyph and never carries a role hue.
    /// </summary>
    private static void Walk(DependencyObject node, List<ShapePath> found)
    {
        if (node is ShapePath path && VisualTreeHelper.GetParent(path) is System.Windows.Controls.Canvas)
        {
            found.Add(path);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), found);
        }
    }

    /// <summary>Delegates to the one runner. See <see cref="Sta"/> for why it must be background.</summary>
    private static void OnStaThread(Action body) => Sta.Run(body);
}
