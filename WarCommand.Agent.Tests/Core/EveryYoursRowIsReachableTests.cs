using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Every row drawn in YOURS answers to a number, on whatever page it is drawn.
/// </summary>
/// <remarks>
/// The reported failure: the active work was on screen and no key reached it. A slot is an
/// admission token for the claimable queue, and a row in YOURS is past being claimed, so it may
/// hold none: ApplyClaim releases the digit for everyone who is not the claimant, the requester
/// included, and a claim arriving for a row already in overflow never had one to keep. The line
/// list filtered YOURS on the slot, so those rows were on no page, CANCEL and DONE were offered
/// for rows no digit could select, and there was no route to update or close your own job.
/// </remarks>
public sealed class EveryYoursRowIsReachableTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static readonly Guid Viewer = Rows.Viewer;

    private static readonly Guid Other = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static BoardState Board() => new(Viewer, ContractFixtures.Rules);

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);

    /// <summary>
    /// The menu context the render builds: one entry per LINE on the page shown, in the order the
    /// board drew them. Mirrors BoardRealtimeObserver.Render.
    /// </summary>
    private static MenuContext ContextFor(BoardState board)
    {
        var slots = new Dictionary<int, SlotState>();
        var line = 0;
        foreach (var row in board.Lines)
        {
            slots[++line] = new SlotState(
                row.State,
                row.IsClaimedBy(board.ViewerParticipantId),
                row.RequestedByParticipantId == board.ViewerParticipantId,
                row.Id);
        }

        return new MenuContext
        {
            OccupiedSlots = [.. slots.Keys.Order()],
            Slots = slots,
            BoardPages = board.PageCount,
        };
    }

    private static int LineOf(BoardState board, Guid requestId)
    {
        var lines = board.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Id == requestId)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Walks the menu to the row on that line and presses the named verb.</summary>
    private static MenuOutcome PressVerb(BoardState board, int line, string verbId)
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, ContextFor(board));

        Assert.IsType<MenuNavigated>(menu.Digit(line, T0));
        Assert.Equal(MenuLevel.BoardAction, menu.Level);

        var entry = menu.Options.FirstOrDefault(o => o.VerbId == verbId);
        Assert.True(entry is not null, $"{verbId} is not offered on line {line}.");

        return menu.Digit(entry!.Digit, T0);
    }

    /// <summary>The bug as reported: your own request, taken by somebody else, with no way in.</summary>
    [Fact]
    public void A_row_you_asked_for_that_somebody_else_took_can_be_cancelled_from_the_menu()
    {
        var board = Board();
        var asked = Rows.A(requester: Viewer, createdAt: T0);
        board.Upsert(asked, T0);

        board.ApplyClaim(asked.Id, Other, "BEAR", 2, T0);

        // It draws in YOURS and holds no digit: the claim released it for everyone but the taker.
        var stored = board.ById(asked.Id)!;
        Assert.Contains(board.Yours, r => r.Id == asked.Id);
        Assert.False(stored.HoldsSlot);

        // And it is still on a line, because the line is the position and not the slot.
        var line = LineOf(board, asked.Id);
        Assert.Equal(1, line);
        Assert.Equal(asked.Id, board.ByLine(line)!.Id);

        var pressed = PressVerb(board, line, "cancel");

        var action = Assert.IsType<MenuBoardAction>(pressed);
        Assert.Equal("cancel", action.VerbId);
        Assert.Equal(line, action.Slot);
    }

    /// <summary>
    /// A job you are holding that never got a digit. The claim frame arrives for a row already in
    /// overflow, which is what an accept from the web surface looks like on a saturated board.
    /// </summary>
    [Fact]
    public void A_row_you_hold_with_no_digit_can_be_finished_from_the_menu()
    {
        var board = Board();
        for (var i = 0; i < 9; i++)
        {
            board.Upsert(Rows.A(createdAt: T0.AddSeconds(i)), T0);
        }

        var mine = Rows.A(createdAt: T0.AddSeconds(10));
        board.Upsert(mine, T0);
        Assert.Null(board.ById(mine.Id)!.Slot);

        board.ApplyClaim(mine.Id, Viewer, "GHOST", 2, T0);

        var held = board.ById(mine.Id)!;
        Assert.False(held.HoldsSlot);
        Assert.Contains(board.Yours, r => r.Id == mine.Id);

        // It is behind the first page of nine, so the board pages onto it rather than losing it.
        Assert.Equal(0, LineOf(board, mine.Id));
        board.TurnPage(1);

        var line = LineOf(board, mine.Id);
        Assert.Equal(1, line);

        var action = Assert.IsType<MenuBoardAction>(PressVerb(board, line, "done"));
        Assert.Equal("done", action.VerbId);
        Assert.Equal(line, action.Slot);
    }

    /// <summary>Every verb a YOURS row offers reaches the row it was offered for.</summary>
    [Fact]
    public void No_row_in_yours_is_on_no_page()
    {
        var board = Board();
        for (var i = 0; i < 9; i++)
        {
            board.Upsert(Rows.A(createdAt: T0.AddSeconds(i)), T0);
        }

        // One you asked for and somebody took, one you hold, one shared job you joined.
        var asked = Rows.A(requester: Viewer, createdAt: T0.AddSeconds(20));
        var held = Rows.A(createdAt: T0.AddSeconds(21));
        var shared = Rows.A(createdAt: T0.AddSeconds(22)) with { MultiTaker = true };
        foreach (var row in (BoardRow[])[asked, held, shared])
        {
            board.Upsert(row, T0);
        }

        board.ApplyClaim(asked.Id, Other, "BEAR", 2, T0);
        board.ApplyClaim(held.Id, Viewer, "GHOST", 2, T0);
        board.ApplyClaim(shared.Id, Viewer, "GHOST", 2, T0);

        var seen = new List<Guid>();
        for (var page = 0; page < board.PageCount; page++)
        {
            seen.AddRange(board.Lines.Select(r => r.Id));
            board.TurnPage(1);
        }

        foreach (var row in board.Yours)
        {
            Assert.Contains(row.Id, seen);
        }

        // Once each. A shared row you joined stays open and holds no digit, so it satisfies both
        // YOURS and overflow and would otherwise draw twice on one board.
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    /// <summary>The off-page count still describes what is not on screen.</summary>
    [Fact]
    public void The_line_count_covers_every_page()
    {
        var board = Board();
        for (var i = 0; i < 12; i++)
        {
            board.Upsert(Rows.A(createdAt: T0.AddSeconds(i)), T0);
        }

        var asked = Rows.A(requester: Viewer, createdAt: T0.AddSeconds(20));
        board.Upsert(asked, T0);
        board.ApplyClaim(asked.Id, Other, "BEAR", 2, T0);

        var counted = 0;
        for (var page = 0; page < board.PageCount; page++)
        {
            counted += board.Lines.Count;
            board.TurnPage(1);
        }

        Assert.Equal(board.Rows.Count + board.Yours.Count + board.Overflow.Count, counted);
        Assert.True(board.LineCount >= board.Yours.Count);
    }
}
