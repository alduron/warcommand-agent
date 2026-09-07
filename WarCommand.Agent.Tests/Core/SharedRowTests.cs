using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A multi_taker row on the board: it stays open, it keeps its digit for everyone who has not
/// taken it, and it lands in ACTIVE only for the people actually on it.
/// </summary>
public sealed class SharedRowTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static BoardRow Shared(params Guid[] takers) =>
        Rows.A() with { MultiTaker = true, TakerParticipantIds = takers };

    private static BoardState Board() => new(Rows.Viewer, ContractFixtures.Rules);

    [Fact]
    public void A_shared_row_nobody_has_taken_is_not_held()
    {
        Assert.False(Shared().IsHeld);
        Assert.Equal(0, Shared().TakerCount);
    }

    [Fact]
    public void A_shared_row_is_held_once_somebody_is_on_it()
    {
        var row = Shared(Rows.Viewer);
        Assert.True(row.IsHeld);
        Assert.True(row.IsClaimedBy(Rows.Viewer));
        Assert.Equal(1, row.TakerCount);
    }

    [Fact]
    public void A_shared_row_somebody_else_took_never_counts_as_in_progress()
    {
        // Collapsing it to a count would hide work this viewer is still wanted for.
        var row = Shared(Guid.NewGuid());
        Assert.False(row.CountsAsInProgress(Rows.Viewer));
    }

    [Fact]
    public void A_single_claimant_row_somebody_else_took_still_counts_as_in_progress()
    {
        var row = Rows.A() with
        {
            State = RequestState.InProgress,
            ClaimantParticipantId = Guid.NewGuid(),
        };
        Assert.True(row.CountsAsInProgress(Rows.Viewer));
    }

    [Fact]
    public void Accepting_a_shared_row_leaves_it_open_and_adds_a_taker()
    {
        var board = Board();
        var row = Shared();
        board.Upsert(row, T0);

        var after = board.ApplyClaim(row.Id, Rows.Viewer, "BEAR", 2, T0);

        Assert.NotNull(after);
        Assert.Equal(RequestState.Open, after!.State);
        Assert.Contains(Rows.Viewer, after.TakerParticipantIds);
        Assert.Contains(after, board.Yours);
    }

    [Fact]
    public void A_shared_row_somebody_else_accepted_keeps_its_digit_for_this_viewer()
    {
        var board = Board();
        var row = Shared();
        board.Upsert(row, T0);
        var slot = board.ById(row.Id)!.Slot;
        Assert.NotNull(slot);

        board.ApplyClaim(row.Id, Guid.NewGuid(), "GHOST", 2, T0);

        // Still sayable: this viewer can join it, and a row with no number cannot be said.
        var after = board.ById(row.Id)!;
        Assert.Equal(slot, after.Slot);
        Assert.True(after.ClaimableByDigit);
        Assert.DoesNotContain(after, board.Yours);
        Assert.Equal(0, board.InProgressCount);
    }

    [Fact]
    public void The_requester_of_a_shared_row_somebody_took_sees_it_in_active()
    {
        var requester = Rows.Viewer;
        var board = new BoardState(requester, ContractFixtures.Rules);
        var row = Rows.A() with { MultiTaker = true, RequestedByParticipantId = requester };
        board.Upsert(row, T0);

        board.ApplyClaim(row.Id, Guid.NewGuid(), "GHOST", 2, T0);

        Assert.Contains(board.Yours, r => r.Id == row.Id);
    }
}
