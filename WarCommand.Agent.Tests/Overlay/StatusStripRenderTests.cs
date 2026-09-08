using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The status strip draws, and it draws the way it was designed to.
/// </summary>
/// <remarks>
/// The strip is bindings and converters, and a wrong binding path in WPF fails silently at runtime:
/// the item renders blank and nothing throws. Constructing the view is not enough to catch that, so
/// these force a real measure and arrange pass and then read the visual tree, which is the only
/// thing that proves a template inflated rather than merely parsed.
/// </remarks>
public class StatusStripRenderTests
{
    private static readonly Size Panel = new(380, 600);

    private static BoardView Rendered(BoardView view)
    {
        view.Measure(Panel);
        view.Arrange(new Rect(Panel));
        view.UpdateLayout();
        return view;
    }

    private static List<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    /// <summary>Nothing standing costs the board no height at all.</summary>
    [Fact]
    public void An_empty_strip_is_collapsed()
    {
        Sta.Run(() =>
        {
            var view = Rendered(new BoardView());
            view.RenderStatus([]);
            Rendered(view);

            Assert.Equal(Visibility.Collapsed, view.StatusStrip.Visibility);
        });
    }

    /// <summary>
    /// Every item inflates: its text lands, and its dot takes the severity's color.
    /// </summary>
    [Fact]
    public void Each_item_draws_its_word_and_its_color()
    {
        Sta.Run(() =>
        {
            var view = Rendered(new BoardView());
            view.RenderStatus(
            [
                new StatusItem("ACCEPT REFUSED", StatusSeverity.Fault) { IsFirst = true },
                new StatusItem("BOARD MAY BE STALE", StatusSeverity.Warn),
                new StatusItem("COPIED WARCOMMAND:921585", StatusSeverity.Note),
            ]);
            Rendered(view);

            Assert.Equal(Visibility.Visible, view.StatusStrip.Visibility);

            var words = Descendants<TextBlock>(view.StatusItems)
                .Select(block => block.Text)
                .Where(text => text.Length > 0)
                .ToList();

            Assert.Equal(
                ["ACCEPT REFUSED", "BOARD MAY BE STALE", "COPIED WARCOMMAND:921585"],
                words);

            // Three dots, three different colors: the converter ran and resolved a real token
            // rather than falling through to one gray for everything.
            var dots = Descendants<System.Windows.Shapes.Ellipse>(view.StatusItems);
            Assert.Equal(3, dots.Count);
            Assert.Equal(3, dots.Select(dot => ((SolidColorBrush)dot.Fill).Color).Distinct().Count());
        });
    }

    /// <summary>
    /// The leading item draws no divider, so the strip never opens with a floating rule.
    /// </summary>
    [Fact]
    public void Only_the_items_after_the_first_carry_a_divider()
    {
        Sta.Run(() =>
        {
            var view = Rendered(new BoardView());
            view.RenderStatus(
            [
                new StatusItem("ONE", StatusSeverity.Warn) { IsFirst = true },
                new StatusItem("TWO", StatusSeverity.Warn),
                new StatusItem("THREE", StatusSeverity.Warn),
            ]);
            Rendered(view);

            var dividers = Descendants<Border>(view.StatusItems)
                .Where(border => border.Width == 1)
                .ToList();

            Assert.Equal(3, dividers.Count);
            Assert.Equal(Visibility.Collapsed, dividers[0].Visibility);
            Assert.All(dividers.Skip(1), border => Assert.Equal(Visibility.Visible, border.Visibility));
        });
    }

    /// <summary>
    /// The strip is one line whatever it holds. A second line moves every board row down.
    /// </summary>
    [Fact]
    public void The_strip_never_grows_past_one_line()
    {
        Sta.Run(() =>
        {
            var view = Rendered(new BoardView());

            view.RenderStatus([new StatusItem("ONE", StatusSeverity.Warn) { IsFirst = true }]);
            Rendered(view);
            var single = view.StatusStrip.ActualHeight;

            view.RenderStatus(
            [
                new StatusItem("A MUCH LONGER FAULT WORD", StatusSeverity.Fault) { IsFirst = true },
                new StatusItem("BOARD MAY BE STALE", StatusSeverity.Warn),
                new StatusItem("ANOTHER DEVICE ON BOARD", StatusSeverity.Warn),
            ],
            hidden: 2);
            Rendered(view);

            Assert.Equal(single, view.StatusStrip.ActualHeight);
            Assert.Equal("+2", view.StatusMore.Text);
        });
    }

    /// <summary>The strip is a surface like any other: it replays onto a view added later.</summary>
    [Fact]
    public void A_surface_added_after_the_render_still_gets_the_strip()
    {
        Sta.Run(() =>
        {
            var presenter = new BoardPresenter();
            presenter.RenderStatus([new StatusItem("BOARD MAY BE STALE", StatusSeverity.Warn) { IsFirst = true }]);

            var view = new BoardView();
            presenter.Add(view);
            Rendered(view);

            Assert.Equal(Visibility.Visible, view.StatusStrip.Visibility);
            Assert.Single((IEnumerable<object>)((IEnumerable)view.StatusItems.ItemsSource).Cast<object>());
        });
    }
}
