using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A row offers only the verbs it can actually honour.
/// </summary>
/// <remarks>
/// Every verb used to appear on every row: START, DONE and RELEASE on an open row nobody had
/// claimed, and ACCEPT on a row already yours. The server refuses all of those, so the press did
/// nothing and the surface reported nothing. This is the same rule MORE already follows.
/// </remarks>
public sealed class RowVerbsAreHonourableTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);

    private static List<string> VerbsOn(RequestState state, bool mine)
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext
        {
            OccupiedSlots = [4],
            Slots = new Dictionary<int, SlotState> { [4] = new(state, mine) },
        });

        menu.Select(T0);
        Assert.Equal(MenuLevel.BoardAction, menu.Level);

        return [.. menu.Options.Select(o => o.VerbId!)];
    }

    [Fact]
    public void An_open_row_can_be_taken_but_not_finished()
    {
        var verbs = VerbsOn(RequestState.Open, mine: false);

        Assert.Contains("accept", verbs);
        Assert.Contains("pass", verbs);

        // The server refuses both of these on an open row.
        Assert.DoesNotContain("done", verbs);
        Assert.DoesNotContain("release", verbs);
    }

    [Fact]
    public void There_is_no_start_verb_anywhere_on_a_row()
    {
        // Taking a job starts it. START was a second keypress that meant nothing on its own, and
        // a claim that never got it was auto-released out from under the person doing the work.
        foreach (var state in (RequestState[])[RequestState.Open, RequestState.Claimed, RequestState.InProgress])
        {
            Assert.DoesNotContain("start", VerbsOn(state, mine: true));
            Assert.DoesNotContain("start", VerbsOn(state, mine: false));
        }
    }

    [Fact]
    public void A_row_you_hold_can_be_finished_or_given_back_but_not_taken_again()
    {
        var verbs = VerbsOn(RequestState.InProgress, mine: true);

        Assert.Contains("done", verbs);
        Assert.Contains("release", verbs);
        Assert.DoesNotContain("accept", verbs);
    }

    [Fact]
    public void A_row_claimed_before_claim_started_it_still_finishes()
    {
        // Rows written before claiming started the job are still in the database in Claimed.
        var verbs = VerbsOn(RequestState.Claimed, mine: true);

        Assert.Contains("done", verbs);
        Assert.Contains("release", verbs);
    }

    [Fact]
    public void Somebody_elses_row_offers_nothing_that_touches_it()
    {
        var verbs = VerbsOn(RequestState.InProgress, mine: false);

        Assert.DoesNotContain("accept", verbs);
        Assert.DoesNotContain("start", verbs);
        Assert.DoesNotContain("done", verbs);
        Assert.DoesNotContain("release", verbs);

        // Copying the grid is always useful, whoever is working it.
        Assert.Contains("copy", verbs);
    }

    [Fact]
    public void The_whole_lifecycle_is_reachable_from_the_menu()
    {
        // Take it, finish it. Two presses, and if either step stops offering its verb the job
        // cannot be closed from the overlay at all, which is the failure this file exists to catch.
        Assert.Contains("accept", VerbsOn(RequestState.Open, mine: false));
        Assert.Contains("done", VerbsOn(RequestState.InProgress, mine: true));
    }

    [Fact]
    public void A_digit_keeps_its_verb_wherever_the_verb_appears()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext
        {
            OccupiedSlots = [4],
            Slots = new Dictionary<int, SlotState> { [4] = new(RequestState.Claimed, true) },
        });
        menu.Select(T0);

        // Filtering removes entries; it must never renumber the survivors. A digit learned once
        // stays learned.
        var done = menu.Options.Single(o => o.VerbId == "done");
        Assert.Equal(3, done.Digit);
    }

    [Fact]
    public void With_no_slot_state_known_every_verb_is_still_offered()
    {
        // A caller that has not filled Slots in must not lose the board entirely.
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [4] });
        menu.Select(T0);

        Assert.Contains(menu.Options, o => o.VerbId == "accept");
        Assert.Contains(menu.Options, o => o.VerbId == "done");
    }
}
