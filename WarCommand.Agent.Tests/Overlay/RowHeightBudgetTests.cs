using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// A row with no tags is TWO lines and nothing more, second point or not. The board shows nine
    /// of them, so this is the number that decides whether the overlay is usable at all.
    /// </summary>
    private const double MaxBareRowHeight = 40;

    /// <summary>The worst case: fifteen tags, all drawn. Three lines, never a hidden count.</summary>
    private const double MaxLoadedRowHeight = 96;

    /// <summary>
    /// Nine rows is what the board shows, so nine rows is what has to fit. At 380 wide on a 1080p
    /// screen this is the whole reason a row is two lines rather than four.
    /// </summary>
    private const double MaxNineBareRows = 9 * MaxBareRowHeight;

    private static readonly RoleGlyphSource Glyphs = new(BundledContracts.Catalog().Current.Role);

    private static BoardRowViewModel Row(
        IReadOnlyList<string> tags,
        string? secondPoint = null,
        string ticket = "TPT-14") =>
        new BoardRowViewModel
        {
            SlotDisplay = "1",
            RoleId = "ground_transport",
            TypeAndQualifier = "RIFLE",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "12s",
            TicketCode = ticket,
            Tags = tags,
            Chips = tags,
            TagsDisplay = string.Join(' ', tags),
            SecondPointDisplay = secondPoint,
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
    public void A_second_point_costs_no_extra_line_at_all()
    {
        // It shares line 2 with the callsign and the ticket. A HOTDROP is exactly as tall as a
        // MEDIC, which is what makes nine of them fit.
        Sta.Run(() => Assert.Equal(HeightOf(Row([])), HeightOf(Row([], "x12.10 y44.02"))));
    }

    [Fact]
    public void Nine_rows_fit_the_board()
    {
        // The requirement the whole row design answers. A tagged HOTDROP was four lines once and
        // nine of those do not go on a screen.
        Sta.Run(() =>
        {
            var rows = Enumerable
                .Range(0, 9)
                .Select(i => Row(
                    i % 3 == 0 ? [] : ["SMOKE"],
                    i % 2 == 0 ? null : "x12.10 y44.02",
                    $"TPT-{i.ToString(CultureInfo.InvariantCulture)}"))
                .ToArray();

            var view = new BoardView();
            view.RenderBoard(rows, [], 0, 0);
            view.Measure(new Size(400, 4000));
            view.Arrange(new Rect(0, 0, 400, 4000));
            view.UpdateLayout();

            var found = new List<FrameworkElement>();
            Walk(view, found);
            var total = found.Where(e => e.Name == "RowRoot").Sum(e => e.ActualHeight);

            Assert.InRange(total, 1, MaxNineBareRows * 1.5);
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

    [Fact]
    public void Both_points_and_every_other_row_share_one_coordinate_column()
    {
        // The whole design. One flat WrapPanel let PICKUP and DROPOFF flow like words, so a
        // HOTDROP drew its two coordinates at different x and usually on different lines, with
        // the tags and the callsign wrapped in between them.
        Sta.Run(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row([], "x12.10 y44.02"), Row(["SMOKE"])], [], 0, 0);
            view.Measure(new Size(400, 2000));
            view.Arrange(new Rect(0, 0, 400, 2000));
            view.UpdateLayout();

            var found = new List<FrameworkElement>();
            Walk(view, found);

            var xs = found
                .OfType<TextBlock>()
                .Where(t => t.Text.StartsWith('x') && t.Text.Contains(" y"))
                .Select(t => Left(view, t))
                .Distinct()
                .ToList();

            Assert.NotEmpty(xs);
            Assert.Single(xs);
        });
    }

    [Fact]
    public void The_callsign_and_the_ticket_hold_the_same_two_corners_on_every_row()
    {
        // Reported: a reader had to hunt for the ticket code, the callsign and the tags in a
        // different place on every row, because the meta line wrapped and the tag count decided
        // where everything after it landed. Nothing in a row flows now.
        Sta.Run(() =>
        {
            var view = new BoardView();
            view.RenderBoard(
                [
                    Row([], ticket: "TPT-14"),
                    Row(["SMOKE"], "x12.10 y44.02", "TPT-15"),
                    Row(ManyTags, ticket: "TPT-16"),
                ],
                [],
                0,
                0);
            view.Measure(new Size(400, 2000));
            view.Arrange(new Rect(0, 0, 400, 2000));
            view.UpdateLayout();

            var found = new List<FrameworkElement>();
            Walk(view, found);

            var callsigns = found.OfType<TextBlock>().Where(t => t.Text == "Ghost").ToList();
            var tickets = found.OfType<TextBlock>()
                .Where(t => t.Text.StartsWith("TPT-", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(3, callsigns.Count);
            Assert.Equal(3, tickets.Count);

            Assert.Single(callsigns.Select(t => Left(view, t)).Distinct());
            Assert.Single(tickets.Select(t => Left(view, t) + t.ActualWidth).Distinct());
        });
    }

    private static double Left(Visual root, Visual element) =>
        element.TransformToAncestor(root).Transform(new Point(0, 0)).X;

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
