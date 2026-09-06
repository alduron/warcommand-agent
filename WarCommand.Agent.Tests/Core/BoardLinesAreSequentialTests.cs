using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The board is numbered 1, 2, 3 down the page, always, with no holes.
/// </summary>
/// <remarks>
/// The number is what somebody counts down to before pressing, and what they say out loud in
/// "accept 3". It used to be the allocation SLOT, which is stable for a row's life and therefore
/// full of holes: clear the row on 2 and the board read 1, 3, 4, so the third line down answered
/// to 4 and every count anybody did aloud was wrong.
/// <para>
/// The slot still exists and still does its job, which is admission: nine at a time, and no two
/// live rows sharing one. It is simply not the number on the screen any more.
/// </para>
/// </remarks>
public class BoardLinesAreSequentialTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static readonly Guid Viewer = Rows.Viewer;

    private static BoardState Board() => new(Viewer, ContractFixtures.Rules);

    private static BoardRow Row(int n, Guid? claimant = null) =>
        Rows.A(
            id: Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:d2}"),
            typeId: "shell_mission",
            createdAt: T0.AddSeconds(n),
            state: claimant is null ? RequestState.Open : RequestState.InProgress,
            claimant: claimant) with
        {
            TicketCode = $"MTR-{n:d2}",
        };

    private static BoardState WithRows(int count)
    {
        var board = Board();
        for (var n = 1; n <= count; n++)
        {
            board.Upsert(Row(n), T0);
        }

        return board;
    }

    private static int[] Numbers(BoardState board) =>
        [.. Enumerable.Range(1, board.Lines.Count)];

    [Fact]
    public void A_fresh_board_counts_from_one()
    {
        var board = WithRows(5);

        Assert.Equal([1, 2, 3, 4, 5], Numbers(board));
        Assert.Equal("MTR-01", board.ByLine(1)!.TicketCode);
        Assert.Equal("MTR-05", board.ByLine(5)!.TicketCode);
    }

    /// <summary>The reported complaint: a row leaves the middle and the numbers close up.</summary>
    [Fact]
    public void Clearing_a_row_out_of_the_middle_closes_the_gap()
    {
        var board = WithRows(5);
        var second = board.ByLine(2)!;

        board.Remove(second.Id, T0);

        Assert.Equal([1, 2, 3, 4], Numbers(board));
        Assert.Equal("MTR-01", board.ByLine(1)!.TicketCode);

        // What was line 3 is line 2 now, and 2 is what reaches it.
        Assert.Equal("MTR-03", board.ByLine(2)!.TicketCode);
        Assert.Equal("MTR-05", board.ByLine(4)!.TicketCode);
    }

    [Fact]
    public void Clearing_the_first_row_renumbers_everything_under_it()
    {
        var board = WithRows(4);
        board.Remove(board.ByLine(1)!.Id, T0);

        Assert.Equal([1, 2, 3], Numbers(board));
        Assert.Equal("MTR-02", board.ByLine(1)!.TicketCode);
    }

    /// <summary>
    /// A row this viewer claimed moves to YOURS at the foot, and the count runs straight on into it.
    /// </summary>
    [Fact]
    public void The_count_runs_on_through_yours()
    {
        var board = WithRows(3);
        var mine = Row(2, claimant: Viewer);
        board.Upsert(mine, T0);

        // Two claimable rows, then the one held, and the numbers do not restart or skip.
        Assert.Equal([1, 2, 3], Numbers(board));
        Assert.Equal(3, board.Lines.Count);
        Assert.Equal(mine.Id, board.ByLine(3)!.Id);
        Assert.Contains(board.Yours, r => r.Id == mine.Id);
    }

    [Fact]
    public void A_line_off_the_end_is_nothing_rather_than_the_wrong_row()
    {
        var board = WithRows(2);

        Assert.Null(board.ByLine(0));
        Assert.Null(board.ByLine(3));
        Assert.Null(board.ByLine(9));
    }

    [Fact]
    public void An_empty_board_has_no_lines()
    {
        Assert.Empty(Board().Lines);
        Assert.Null(Board().ByLine(1));
    }

    /// <summary>
    /// A press resolves to the row that was DRAWN on that line, not whatever is there now.
    /// </summary>
    /// <remarks>
    /// The cost of positional numbering: a row leaving renumbers everything under it, so between
    /// the render somebody read and the key they pressed a digit can come to mean a different job.
    /// The render snapshot carries the row id per line so the press means what was on screen.
    /// </remarks>
    [Fact]
    public void The_snapshot_remembers_which_row_was_on_each_line()
    {
        var board = WithRows(4);

        // What the render captured: line 3 held MTR-03.
        var drawn = board.Lines.Select(r => r.Id).ToList();
        Assert.Equal("MTR-03", board.ById(drawn[2])!.TicketCode);

        // The row above it clears before the key lands, so line 3 is MTR-04 now.
        board.Remove(board.ByLine(1)!.Id, T0);
        Assert.Equal("MTR-04", board.ByLine(3)!.TicketCode);

        // The id from the render still reaches the row that was read.
        Assert.Equal("MTR-03", board.ById(drawn[2])!.TicketCode);
    }

    /// <summary>A row that left the board resolves to nothing, rather than to its neighbour.</summary>
    [Fact]
    public void A_row_that_left_is_not_silently_replaced()
    {
        var board = WithRows(3);
        var drawn = board.ByLine(2)!.Id;

        board.Remove(drawn, T0);

        Assert.Null(board.ById(drawn));
    }

    /// <summary>
    /// Whatever happens to the board, the numbers are 1 to N with nothing missing and nothing twice.
    /// </summary>
    [Fact]
    public void The_numbering_is_always_one_to_n()
    {
        var board = WithRows(9);

        board.Remove(board.ByLine(4)!.Id, T0);
        board.Remove(board.ByLine(1)!.Id, T0);
        board.Upsert(Row(20), T0);
        board.Upsert(Row(3, claimant: Viewer), T0);

        var lines = board.Lines;
        Assert.NotEmpty(lines);

        for (var n = 1; n <= lines.Count; n++)
        {
            Assert.Same(lines[n - 1], board.ByLine(n));
        }

        // Every row on the board answers to exactly one number, and every number to one row.
        Assert.Equal(lines.Count, lines.Select(r => r.Id).Distinct().Count());
    }
}
