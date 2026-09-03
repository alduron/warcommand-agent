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
    public void Every_verb_a_row_offers_is_within_reach_of_the_hand_holding_the_key()
    {
        foreach (var state in (RequestState[])[RequestState.Open, RequestState.Claimed, RequestState.InProgress])
        {
            foreach (var mine in (bool[])[true, false])
            {
                var menu = Machine();
                menu.OpenOnBoard(T0, new MenuContext
                {
                    OccupiedSlots = [4],
                    Slots = new Dictionary<int, SlotState> { [4] = new(state, mine, true) },
                });
                menu.Select(T0);

                // A hand with its pinky on CapsLock covers 1 to 5 on the number row and nothing
                // past it. MUTE sat on 6 and COPY on 7, so both were unpressable one-handed.
                foreach (var option in menu.Options)
                {
                    Assert.InRange(option.Digit, 1, 5);
                }

                // And they are numbered from one with no gaps, because the number is a position in
                // what THIS row offers rather than a fixed identity for the verb.
                Assert.Equal(
                    Enumerable.Range(1, menu.Options.Count),
                    menu.Options.Select(o => o.Digit));
            }
        }
    }

    [Fact]
    public void The_first_verb_on_a_row_is_the_one_you_came_for()
    {
        // Learned once and stable, because the states are disjoint: on an open row 1 is always
        // ACCEPT, and on a row you hold 1 is always DONE.
        Assert.Equal("accept", VerbsOn(RequestState.Open, mine: false)[0]);
        Assert.Equal("done", VerbsOn(RequestState.InProgress, mine: true)[0]);
    }

    [Fact]
    public void With_no_slot_state_known_the_row_still_offers_something_reachable()
    {
        // A caller that has not filled Slots in must not lose the board entirely. It cannot know
        // which verbs the row will honour, so it offers the most important ones and still stops at
        // five, because the sixth would be a key the hand cannot reach anyway.
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [4] });
        menu.Select(T0);

        Assert.NotEmpty(menu.Options);
        Assert.True(menu.Options.Count <= 5);
        Assert.Contains(menu.Options, o => o.VerbId == "accept");
        Assert.Contains(menu.Options, o => o.VerbId == "done");
    }
}
