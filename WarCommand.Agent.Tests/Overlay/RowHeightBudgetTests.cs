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
    /// <summary>The tallest a row may draw, tags and a second point included.</summary>
    private const double MaxRowHeight = 64;

    /// <summary>A bare row and a fully loaded one may not differ by more than one extra line.</summary>
    private const double MaxTagCost = 20;

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
    public void A_row_carrying_fifteen_tags_stays_inside_the_budget()
    {
        Sta.Run(() =>
        {
            var height = HeightOf(Row(ManyTags));
            Assert.InRange(height, 1, MaxRowHeight);
        });
    }

    [Fact]
    public void Tags_cost_at_most_one_line_of_height()
    {
        Sta.Run(() =>
        {
            var bare = HeightOf(Row([]));
            var loaded = HeightOf(Row(ManyTags));
            Assert.InRange(loaded - bare, 0, MaxTagCost);
        });
    }

    [Fact]
    public void A_second_point_costs_no_extra_line()
    {
        Sta.Run(() =>
        {
            var onePoint = HeightOf(Row([]));
            var twoPoints = HeightOf(Row([], "x12.10 y44.02"));
            Assert.Equal(onePoint, twoPoints, 1);
        });
    }

    [Fact]
    public void Only_the_first_three_tags_draw_and_the_rest_become_one_chip()
    {
        var row = Row(ManyTags);
        Assert.Equal(BoardRowViewModel.MaxVisibleTags + 1, row.VisibleTags.Count);
        Assert.Equal("+12", row.VisibleTags[^1]);
    }

    [Fact]
    public void A_row_inside_the_cap_draws_every_tag_and_no_counter()
    {
        var row = Row(["Mags", "Scope"]);
        Assert.Equal(["Mags", "Scope"], row.VisibleTags);
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
