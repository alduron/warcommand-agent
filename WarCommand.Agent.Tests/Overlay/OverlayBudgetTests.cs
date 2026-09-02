using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The animation budget from 06-overlay-ux.md: at most one pulsing slot digit and at most two
/// countdown bars, board-wide, both on the soonest to expire.
/// </summary>
/// <remarks>
/// The rule exists because three or four pulsing digits turn the digit column into the moving
/// thing, which destroys the one property that makes a slot findable in a 400 ms glance.
/// </remarks>
public class OverlayBudgetTests
{
    /// <summary>CountdownFraction is life remaining, so a smaller value is sooner to expire.</summary>
    private static BoardRowViewModel Expiring(string ticket, double width) => new()
    {
        SlotDisplay = "1",
        TypeAndQualifier = "MORTAR",
        CoordinatesDisplay = "x85.53 y69.42",
        Requester = "Ghost",
        AgeDisplay = "12s",
        TicketCode = ticket,
        HasCountdown = true,
        CountdownFraction = width,
    };

    private static BoardRowViewModel Calm(string ticket) => new()
    {
        SlotDisplay = "9",
        TypeAndQualifier = "AMMO",
        CoordinatesDisplay = "x80.00 y60.00",
        Requester = "Kite",
        AgeDisplay = "2m",
        TicketCode = ticket,
    };

    private static List<BoardRowViewModel> Live(BoardView view) =>
        ((IEnumerable)view.RowsList.ItemsSource).Cast<BoardRowViewModel>().ToList();

    [Fact]
    public void Exactly_one_digit_pulses_and_it_is_the_soonest_to_expire()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard(
                [Expiring("A", 90), Expiring("B", 12), Expiring("C", 45), Calm("D")],
                [],
                0,
                0);

            var live = Live(view);

            Assert.Single(live.Where(r => r.Pulses));
            Assert.Equal("B", live.Single(r => r.Pulses).TicketCode);
        });
    }

    [Fact]
    public void Every_open_row_keeps_its_auto_cancel_bar()
    {
        // Bars are no longer rationed. Each open row cancels itself after its 120 s and the bar is
        // the only thing that says so, so a row without one would read as staying put. The bar is a
        // slow fill rather than motion; the PULSE is the thing still budgeted to exactly one.
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard(
                [Expiring("A", 90), Expiring("B", 12), Expiring("C", 45), Expiring("D", 100)],
                [],
                0,
                0);

            var showing = Live(view).Where(r => r.HasCountdown).Select(r => r.TicketCode).ToList();

            Assert.Equal(4, showing.Count);
            Assert.Single(Live(view).Where(r => r.Pulses));
            Assert.Equal("B", Live(view).Single(r => r.Pulses).TicketCode);
        });
    }

    /// <summary>A board with nothing expiring has no moving element at all.</summary>
    [Fact]
    public void A_calm_board_pulses_nothing()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Calm("A"), Calm("B")], [], 0, 0);

            Assert.DoesNotContain(Live(view), r => r.Pulses);
        });
    }

    /// <summary>
    /// The budget is re-spent every poll. A row that stops being the soonest must stop pulsing,
    /// or two digits end up moving one poll after the first one expires.
    /// </summary>
    [Fact]
    public void The_budget_moves_when_a_sooner_row_arrives()
    {
        OnStaThread(() =>
        {
            var view = new BoardView();
            view.RenderBoard([Expiring("A", 40)], [], 0, 0);
            Assert.Equal("A", Live(view).Single(r => r.Pulses).TicketCode);

            view.RenderBoard([Expiring("A", 30), Expiring("B", 5)], [], 0, 0);

            var live = Live(view);
            Assert.Single(live.Where(r => r.Pulses));
            Assert.Equal("B", live.Single(r => r.Pulses).TicketCode);
        });
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
