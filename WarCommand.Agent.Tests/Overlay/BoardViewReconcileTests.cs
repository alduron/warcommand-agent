using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// A poll is a reconcile, not a rebuild. The board refreshes every five seconds, so a render that
/// replaced its ItemsSource made every row on screen flash because one age went from 11s to 16s.
/// </summary>
public class BoardViewReconcileTests
{
    private static BoardRowViewModel Row(string ticket, string age, string slot = "1") => new()
    {
        SlotDisplay = slot,
        TypeAndQualifier = "MORTAR",
        CoordinatesDisplay = "x85.53 y69.42",
        Requester = "Ghost",
        AgeDisplay = age,
        TicketCode = ticket,
    };

    private static List<BoardRowViewModel> Live(BoardView view) =>
        ((IEnumerable)view.RowsList.ItemsSource).Cast<BoardRowViewModel>().ToList();

    /// <summary>
    /// The one that matters. Same tickets, newer ages: the row objects on screen must be the same
    /// instances, updated, because a replaced instance is a rebuilt container and a visible flash.
    /// </summary>
    [Fact]
    public void A_poll_that_changes_only_the_age_keeps_the_rows_it_already_has()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row("MTR-14", "11s"), Row("MTR-15", "4s", "2")], [], 0, 0);
            var first = Live(view);

            view.RenderBoard([Row("MTR-14", "16s"), Row("MTR-15", "9s", "2")], [], 0, 0);
            var second = Live(view);

            Assert.Same(first[0], second[0]);
            Assert.Same(first[1], second[1]);
            Assert.Equal("16s", second[0].AgeDisplay);
            Assert.Equal("9s", second[1].AgeDisplay);
        });
    }

    [Fact]
    public void A_new_ticket_is_added_without_disturbing_the_rows_already_there()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row("MTR-14", "11s")], [], 0, 0);
            var first = Live(view);

            view.RenderBoard([Row("MTR-14", "16s"), Row("MTR-15", "1s", "2")], [], 0, 0);
            var second = Live(view);

            Assert.Same(first[0], second[0]);
            Assert.Equal(2, second.Count);
            Assert.Equal("MTR-15", second[1].TicketCode);
        });
    }

    /// <summary>
    /// A row that changed slot moves. Moving keeps the container, so it slides rather than
    /// replaying an entrance in its new position.
    /// </summary>
    [Fact]
    public void A_row_that_changed_slot_is_moved_rather_than_rebuilt()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row("MTR-14", "11s"), Row("MTR-15", "4s", "2")], [], 0, 0);
            var first = Live(view);

            view.RenderBoard([Row("MTR-15", "9s"), Row("MTR-14", "16s", "2")], [], 0, 0);
            var second = Live(view);

            Assert.Same(first[1], second[0]);
            Assert.Same(first[0], second[1]);
        });
    }

    /// <summary>
    /// The empty state clears the board outright. There is nothing to fade against: the deployment
    /// is gone, not the request.
    /// </summary>
    [Fact]
    public void The_empty_state_clears_every_row()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Row("MTR-14", "11s")], [], 0, 0);

            view.ShowEmptyState("No live deployment", "start one from the web");

            Assert.Empty(Live(view));
        });
    }

    /// <summary>Rows carry change notifications, or an in-place update never reaches the screen.</summary>
    [Fact]
    public void A_row_raises_a_change_for_the_field_that_moved_and_nothing_else()
    {
        var row = Row("MTR-14", "11s");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.CopyFrom(Row("MTR-14", "16s"));

        Assert.Equal(["AgeDisplay"], changed);
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
