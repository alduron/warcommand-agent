using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// End to end through the XAML: rows in, painted glyphs out. Every other test on this path checks
/// one hop, and the overlay drew grey shapeless rows anyway because the hops disagreed.
/// </summary>
public class BoardViewRoleGlyphRenderTests
{
    private static readonly Color Neutral = Color.FromRgb(0xB4, 0xB6, 0xB8);

    [Fact]
    public void The_demo_board_paints_a_glyph_and_a_role_hue_on_every_row()
    {
        OnStaThread(() =>
        {
            var paths = RenderedGlyphPaths();

            Assert.NotEmpty(paths);

            // One or two paths per row: d2 is empty for some roles and the row draws d1 alone.
            var rows = OverlayDemo.Rows.Count + OverlayDemo.SecondaryStrip.Count;
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
        view.RenderBoard(OverlayDemo.Rows, OverlayDemo.SecondaryStrip, 0, 0);

        view.Measure(new Size(400, 900));
        view.Arrange(new Rect(0, 0, 400, 900));
        view.UpdateLayout();

        var found = new List<ShapePath>();
        Walk(view, found);
        return found;
    }

    private static void Walk(DependencyObject node, List<ShapePath> found)
    {
        if (node is ShapePath path)
        {
            found.Add(path);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), found);
        }
    }

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (failure is not null)
        {
            throw new InvalidOperationException("The STA body threw.", failure);
        }
    }
}
