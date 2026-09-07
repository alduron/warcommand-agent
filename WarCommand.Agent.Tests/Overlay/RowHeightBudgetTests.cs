using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// A board row has a height budget. The overlay has to show nine of them over a game.
/// </summary>
public class RowHeightBudgetTests
{
    /// <summary>A row with no tags is one line. This is the common case and it sets the density.</summary>
    private const double MaxBareRowHeight = 40;

    /// <summary>The worst case: fifteen tags, all drawn. Three lines, never a hidden count.</summary>
    private const double MaxLoadedRowHeight = 96;

    /// <summary>A second point may cost one line and no more.</summary>
    private const double MaxOneExtraLine = 20;

    private static readonly RoleGlyphSource Glyphs = new(BundledContracts.Catalog().Current.Role);

    private static BoardRowViewModel Row(IReadOnlyList<string> tags, string? secondPoint = null) =>
        new BoardRowViewModel
        {
            SlotDisplay = "1",
            RoleId = "ground_transport",
            TypeAndQualifier = "RIFLE",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "12s",
            TicketCode = "TPT-14",
            Tags = tags,
            TagsDisplay = string.Join(' ', tags),
            SecondPointDisplay = secondPoint,
            SecondPointLabel = secondPoint is null ? null : "to",
        }.WithGlyph(Glyphs);

    /// <summary>Fifteen, which is what a v5 weapon request can carry.</summary>
    private static readonly string[] ManyTags =
    [
        "M17S", "T21", "AK74", "GALIL", "M4", "FAL", "Ammo", "Mags", "Scope",
        "Comp", "Flash", "Brake", "Suppress", "Grip", "Bipod",
    ];

    private static double HeightOf(BoardRowViewModel row)
    {
        var view = new BoardView();
        view.RenderBoard([row], [], 0, 0);
        view.Measure(new Size(400, 2000));
        view.Arrange(new Rect(0, 0, 400, 2000));
        view.UpdateLayout();

        var found = new List<FrameworkElement>();
        Walk(view, found);
        var rowRoot = found.FirstOrDefault(e => e.Name == "RowRoot");
        Assert.NotNull(rowRoot);
        return rowRoot!.ActualHeight;
    }

    [Fact]
    public void A_row_with_no_tags_is_one_line()
    {
        Sta.Run(() => Assert.InRange(HeightOf(Row([])), 1, MaxBareRowHeight));
    }

    [Fact]
    public void A_row_carrying_fifteen_tags_still_fits_the_worst_case_budget()
    {
        Sta.Run(() => Assert.InRange(HeightOf(Row(ManyTags)), 1, MaxLoadedRowHeight));
    }

    [Fact]
    public void A_second_point_costs_at_most_one_line()
    {
        Sta.Run(() =>
        {
            var onePoint = HeightOf(Row([]));
            var twoPoints = HeightOf(Row([], "x12.10 y44.02"));
            Assert.InRange(twoPoints - onePoint, 0, MaxOneExtraLine);
        });
    }

    [Fact]
    public void Every_tag_draws_a_chip_of_its_own()
    {
        Sta.Run(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row(ManyTags)], [], 0, 0);
            view.Measure(new Size(400, 2000));
            view.Arrange(new Rect(0, 0, 400, 2000));
            view.UpdateLayout();

            var found = new List<FrameworkElement>();
            Walk(view, found);
            var tagLine = found.OfType<ItemsControl>().FirstOrDefault(e => e.Name == "TagLine");
            Assert.NotNull(tagLine);

            // The tags ARE the ask. A count standing in for them is the bug this holds shut.
            var chips = new List<FrameworkElement>();
            Walk(tagLine!, chips);
            var words = chips.OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(ManyTags.Length, words.Count);
            foreach (var tag in ManyTags)
            {
                Assert.Contains(tag, words);
            }
        });
    }

    private static void Walk(DependencyObject node, List<FrameworkElement> found)
    {
        if (node is FrameworkElement element)
        {
            found.Add(element);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), found);
        }
    }
}
