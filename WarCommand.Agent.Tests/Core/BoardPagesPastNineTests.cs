using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A board holds more than nine rows, and every one of them is reachable.
/// </summary>
/// <remarks>
/// There are nine digits and there is no tenth, so nine is what a page holds. Before this, a row
/// past nine drew as part of a <c>...12 more</c> count, answered to no number, and waited for a
/// digit to fall free: on a saturated board that is a queue nobody can work. The window moves
/// instead, and page 2 is nine rows with nine digits exactly like page 1.
/// </remarks>
public class BoardPagesPastNineTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static BoardRow Row(int n) =>
        Rows.A(
            id: Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:d2}"),
            typeId: "shell_mission",
            createdAt: T0.AddSeconds(n)) with
        {
            TicketCode = $"MTR-{n:d2}",
        };

    private static BoardState WithRows(int count)
    {
        var board = new BoardState(Rows.Viewer, ContractFixtures.Rules);
        for (var n = 1; n <= count; n++)
        {
            board.Upsert(Row(n), T0);
        }

        return board;
    }

    [Fact]
    public void A_board_that_fits_is_one_page()
    {
        var board = WithRows(9);

        Assert.Equal(1, board.PageCount);
        Assert.Equal(0, board.Page);
        Assert.Equal(9, board.Lines.Count);
    }

    [Fact]
    public void The_tenth_row_opens_a_second_page()
    {
        var board = WithRows(10);

        Assert.Equal(2, board.PageCount);
        Assert.Equal(9, board.Lines.Count);

        board.TurnPage(1);

        Assert.Equal(1, board.Page);
        Assert.Single(board.Lines);
        Assert.Equal("MTR-10", board.ByLine(1)!.TicketCode);
    }

    /// <summary>The point of the change: the row past nine answers to a digit.</summary>
    [Fact]
    public void Every_row_is_reachable_by_a_digit_on_some_page()
    {
        var board = WithRows(23);
        var reached = new List<string>();

        for (var page = 0; page < board.PageCount; page++)
        {
            for (var line = 1; line <= board.Lines.Count; line++)
            {
                reached.Add(board.ByLine(line)!.TicketCode!);
            }

            board.TurnPage(1);
        }

        Assert.Equal(23, reached.Count);
        Assert.Equal(23, reached.Distinct().Count());
        Assert.Contains("MTR-23", reached);
    }

    [Fact]
    public void Paging_wraps_at_both_ends()
    {
        var board = WithRows(20);

        Assert.Equal(3, board.PageCount);
        Assert.Equal(2, board.TurnPage(-1));
        Assert.Equal(0, board.TurnPage(1));
    }

    /// <summary>
    /// The page a board no longer spans must not leave the overlay drawing nothing.
    /// </summary>
    [Fact]
    public void A_page_that_empties_clamps_back_onto_the_board()
    {
        var board = WithRows(10);
        board.TurnPage(1);
        Assert.Equal(1, board.Page);

        board.Remove(board.ByLine(1)!.Id, T0);

        Assert.Equal(1, board.PageCount);
        Assert.Equal(0, board.Page);
        Assert.Equal(9, board.Lines.Count);
    }

    [Fact]
    public void The_hop_returns_to_the_first_page()
    {
        var board = WithRows(20);
        board.TurnPage(1);

        board.EnterDeployment(Guid.NewGuid(), T0, draft: null);

        Assert.Equal(0, board.Page);
    }

    /// <summary>Page one is exactly what the board drew before paging existed.</summary>
    [Fact]
    public void The_first_page_is_the_slotted_rows_in_the_old_order()
    {
        var board = WithRows(15);

        Assert.Equal(
            board.Rows.Select(r => r.TicketCode),
            board.Lines.Select(r => r.TicketCode));
    }
}
