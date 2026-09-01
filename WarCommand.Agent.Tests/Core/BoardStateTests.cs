using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Ordering, admission and allocation are three different decisions and only the first is fixed.
/// </summary>
public class BoardStateTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static BoardState NewBoard() => new(Rows.Viewer, ContractFixtures.Rules);

    [Fact]
    public void Board_order_is_ascending_by_slot_and_never_by_priority_or_age()
    {
        var board = NewBoard();
        var normal = board.Upsert(Rows.A(priority: Priority.Normal, createdAt: T0), T0)!;
        var urgent = board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(10)), T0)!;

        Assert.Equal(1, normal.Slot);
        Assert.Equal(2, urgent.Slot);
        Assert.Equal([1, 2], board.Rows.Select(r => r.Slot!.Value));
        Assert.Equal(normal.Id, board.Rows[0].Id);
    }

    [Fact]
    public void A_rows_slot_never_changes_while_it_holds_one()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);

        for (var i = 0; i < 5; i++)
        {
            board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(i)), T0);
        }

        board.Tick(T0.AddSeconds(1));
        Assert.Equal(1, board.ById(row.Id)!.Slot);
    }

    [Fact]
    public void Rows_beyond_nine_sit_in_overflow_with_no_slot_and_cannot_be_claimed_by_digit()
    {
        var board = NewBoard();
        for (var i = 0; i < 9; i++)
        {
            board.Upsert(Rows.A(createdAt: T0.AddSeconds(i)), T0);
        }

        var tenth = board.Upsert(Rows.A(createdAt: T0.AddSeconds(10)), T0)!;

        Assert.Null(tenth.Slot);
        Assert.False(tenth.ClaimableByDigit);
        Assert.Single(board.Overflow);
        Assert.Equal(9, board.Rows.Count);
    }

    [Fact]
    public void Admission_is_a_different_decision_from_ordering()
    {
        var board = NewBoard();
        for (var i = 0; i < 9; i++)
        {
            board.Upsert(Rows.A(createdAt: T0.AddSeconds(i)), T0);
        }

        var stale = board.Upsert(Rows.A(priority: Priority.Low, createdAt: T0.AddSeconds(10)), T0)!;
        var urgent = board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(20)), T0)!;
        var freed = board.Rows[3];

        board.Remove(freed.Id, T0.AddSeconds(30));

        // The urgent row admitted despite arriving last; nothing already holding a digit moved.
        Assert.Equal(freed.Slot, board.ById(urgent.Id)!.Slot);
        Assert.Null(board.ById(stale.Id)!.Slot);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], board.Rows.Select(r => r.Slot!.Value));
    }

    [Fact]
    public void A_low_priority_row_past_its_residency_is_demoted_and_stays_open()
    {
        var board = NewBoard();
        var fortify = board.Upsert(Rows.A(typeId: "fortify", priority: Priority.Low), T0)!;
        Assert.Equal(1, fortify.Slot);

        var past = T0.AddSeconds(ContractFixtures.Rules.LowPrioritySlotResidencyS + 1);
        var tick = board.Tick(past);

        var demoted = board.ById(fortify.Id)!;
        Assert.Single(tick.Demoted);
        Assert.Null(demoted.Slot);
        Assert.Equal(RequestState.Open, demoted.State);
        Assert.Contains(board.Overflow, r => r.Id == fortify.Id);
    }

    [Fact]
    public void A_demoted_row_does_not_take_the_digit_straight_back()
    {
        var board = NewBoard();
        var fortify = board.Upsert(Rows.A(typeId: "fortify", priority: Priority.Low), T0)!;

        board.Tick(T0.AddSeconds(ContractFixtures.Rules.LowPrioritySlotResidencyS + 1));
        board.Tick(T0.AddSeconds(ContractFixtures.Rules.LowPrioritySlotResidencyS + 2));

        Assert.Null(board.ById(fortify.Id)!.Slot);
        Assert.Contains(fortify.Id, board.DemotedRequestIds);
    }

    [Fact]
    public void Demotion_frees_the_digit_for_a_fire_mission_behind_it()
    {
        var board = NewBoard();
        for (var i = 0; i < 9; i++)
        {
            board.Upsert(Rows.A(typeId: "fortify", priority: Priority.Low, createdAt: T0), T0);
        }

        var mortar = board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(5)), T0)!;
        Assert.Null(mortar.Slot);

        board.Tick(T0.AddSeconds(ContractFixtures.Rules.LowPrioritySlotResidencyS + 1));

        Assert.NotNull(board.ById(mortar.Id)!.Slot);
    }

    [Fact]
    public void A_row_claimed_by_somebody_else_leaves_the_slot_board_and_renders_on_the_strip()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);

        var claimed = board.Upsert(
            row with { State = RequestState.Claimed, ClaimantParticipantId = Guid.NewGuid(), Version = 2 },
            T0.AddSeconds(1))!;

        Assert.Null(claimed.Slot);
        Assert.False(claimed.ClaimableByDigit);
        Assert.Empty(board.Rows);
        Assert.Single(board.SecondaryStrip);
    }

    [Fact]
    public void A_row_claimed_by_the_viewer_keeps_its_digit()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);

        var mine = board.Upsert(
            row with { State = RequestState.Claimed, ClaimantParticipantId = Rows.Viewer, Version = 2 },
            T0.AddSeconds(1))!;

        Assert.Equal(1, mine.Slot);
        Assert.Empty(board.SecondaryStrip);
    }

    [Fact]
    public void A_released_row_is_an_upsert_that_reacquires_a_slot()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);
        board.Upsert(row with { State = RequestState.Claimed, ClaimantParticipantId = Guid.NewGuid() }, T0);
        Assert.Empty(board.Rows);

        var back = board.Upsert(row with { State = RequestState.Open, ReleaseCount = 1, Version = 3 }, T0.AddSeconds(2))!;

        Assert.NotNull(back.Slot);
        Assert.Single(board.Rows);
    }

    [Fact]
    public void A_terminal_row_leaves_the_board_and_frees_its_digit()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);

        var gone = board.Upsert(row with { State = RequestState.Completed }, T0.AddSeconds(1));

        Assert.Null(gone);
        Assert.Empty(board.Rows);
        Assert.Null(board.ById(row.Id));
    }

    [Fact]
    public void Mute_hides_every_row_from_one_requester_and_frees_their_digits()
    {
        var board = NewBoard();
        var flooder = Guid.NewGuid();
        board.Upsert(Rows.A(requester: flooder), T0);
        board.Upsert(Rows.A(requester: flooder), T0);
        var other = board.Upsert(Rows.A(), T0)!;

        var hidden = board.MuteRequester(flooder, T0.AddSeconds(1));

        Assert.Equal(2, hidden);
        Assert.Equal([other.Id], board.Rows.Select(r => r.Id));
    }

    [Fact]
    public void A_muted_requesters_later_rows_never_reach_the_board()
    {
        var board = NewBoard();
        var flooder = Guid.NewGuid();
        board.MuteRequester(flooder, T0);

        var stored = board.Upsert(Rows.A(requester: flooder), T0.AddSeconds(1));

        Assert.Null(stored);
        Assert.Empty(board.Rows);
    }

    [Fact]
    public void Clear_unmutes_everyone()
    {
        var board = NewBoard();
        var flooder = Guid.NewGuid();
        board.Upsert(Rows.A(requester: flooder), T0);
        board.MuteRequester(flooder, T0);

        board.ClearMutes(T0.AddSeconds(1));

        Assert.Single(board.Rows);
    }

    [Fact]
    public void Pass_hides_one_row_on_this_board_only_and_frees_the_digit()
    {
        var board = NewBoard();
        var row = board.Upsert(Rows.A(), T0)!;
        var behind = board.Upsert(Rows.A(createdAt: T0.AddSeconds(1)), T0)!;

        Assert.True(board.Pass(row.Id, T0.AddSeconds(2)));

        Assert.Equal([behind.Id], board.Rows.Select(r => r.Id));
        Assert.DoesNotContain(board.Overflow, r => r.Id == row.Id);
    }

    [Fact]
    public void The_deployment_hop_aborts_the_draft_then_drops_rows_then_resets_the_reissue_order()
    {
        var board = NewBoard();
        var first = Rows.A();
        board.Upsert(first, T0);
        board.Upsert(Rows.A(), T0);
        board.Remove(first.Id, T0.AddSeconds(1));
        var draft = new RecordingDraftOwner(() => board.Rows.Count);

        var change = board.EnterDeployment(Guid.NewGuid(), T0.AddSeconds(2), draft);

        Assert.True(change.DraftAborted);
        Assert.Equal(1, draft.RowsWhenAborted);
        Assert.Equal(1, change.RowsDropped);
        Assert.Empty(board.Rows);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], board.Allocator.ReissueOrder);
    }

    [Fact]
    public void Nothing_from_the_old_match_can_reclaim_a_digit()
    {
        var board = NewBoard();
        var row = Rows.A();
        board.Upsert(row, T0);

        board.EnterDeployment(Guid.NewGuid(), T0.AddSeconds(1), draft: null);

        Assert.Null(board.ById(row.Id));
        Assert.Equal(0, board.Allocator.Held);
    }

    [Fact]
    public void Accept_next_is_highest_priority_then_lowest_slot()
    {
        var board = NewBoard();
        board.Upsert(Rows.A(priority: Priority.Normal, createdAt: T0), T0);
        var urgent = board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(5)), T0)!;
        board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(6)), T0);

        Assert.Equal(urgent.Id, board.AcceptNext()!.Id);
    }

    [Fact]
    public void Accept_next_skips_rows_the_viewer_cannot_service()
    {
        var board = NewBoard();
        var unreachable = board.Upsert(Rows.A(priority: Priority.Urgent, createdAt: T0), T0)!;
        var reachable = board.Upsert(Rows.A(priority: Priority.Normal, createdAt: T0.AddSeconds(1)), T0)!;

        var chosen = board.AcceptNext(r => r.Id != unreachable.Id);

        Assert.Equal(reachable.Id, chosen!.Id);
    }

    [Fact]
    public void By_slot_resolves_the_digit_a_speaker_named()
    {
        var board = NewBoard();
        board.Upsert(Rows.A(), T0);
        var second = board.Upsert(Rows.A(createdAt: T0.AddSeconds(1)), T0)!;

        Assert.Equal(second.Id, board.BySlot(2)!.Id);
        Assert.Null(board.BySlot(6));
    }

    private sealed class RecordingDraftOwner(Func<int> rowCount) : IDraftOwner
    {
        /// <summary>Rows still on the board when the draft was aborted. Step 0 runs before step 1.</summary>
        public int RowsWhenAborted { get; private set; } = -1;

        public bool AbortDraft(DateTimeOffset now)
        {
            RowsWhenAborted = rowCount();
            return true;
        }
    }
}
