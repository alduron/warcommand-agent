using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// The observer, the board, the menu and the presenter wired together and driven the way a person
/// drives them, asserting what is actually on the overlay after each step.
/// </summary>
/// <remarks>
/// Every other test in this suite checks one piece in isolation, and they were all green through a
/// session where a status word stuck to the header for good, a job could not be closed after START,
/// and a claim vanished off the claimant's own screen. A bug that only exists between two correct
/// pieces cannot be caught by testing the pieces. This walks the real sequences instead.
/// </remarks>
public sealed class AWholeSessionTests
{
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Mate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private sealed class Session
    {
        internal Session()
        {
            Catalog = BundledContracts.Catalog().Current;
            Presenter = new BoardPresenter();
            Board = new BoardState(Viewer, Catalog.GrammarRules);
            Board.EnterDeployment(Deployment, T0, draft: null);

            Observer = new BoardRealtimeObserver(
                Dispatcher.CurrentDispatcher,
                Presenter,
                () => Catalog,
                _ => { },
                _ => { },
                () => { },
                snapshot => Slots = snapshot.Slots);

            Observer.Attach(Board, Viewer, new BoardHeader { Title = "61ST / ALPHA" });

            Machine = new MenuStateMachine(MenuTree.Compile(Catalog), Catalog);
        }

        internal Catalog Catalog { get; }

        internal BoardPresenter Presenter { get; }

        internal BoardState Board { get; }

        internal BoardRealtimeObserver Observer { get; }

        internal MenuStateMachine Machine { get; }

        internal IReadOnlyDictionary<int, SlotState> Slots { get; private set; } =
            new Dictionary<int, SlotState>();

        internal string? OnScreenFault => Presenter.Header?.Fault;

        /// <summary>What the hold key does: reads the snapshot the last render left behind.</summary>
        internal MenuContext ContextNow() => new()
        {
            OccupiedSlots = [.. Slots.Keys.Order()],
            Slots = Slots,
        };

        /// <summary>The verbs the overlay offers on a digit, exactly as a person would see them.</summary>
        internal List<string> VerbsOn(int slot)
        {
            Machine.OpenOnBoard(T0, ContextNow());

            // Scroll down to that row exactly as a person does, rather than jumping to it: the
            // highlight is what SELECT acts on, and the two disagreeing is its own class of bug.
            var wanted = $"board.{slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            for (var step = 0; step < Machine.Options.Count; step++)
            {
                if (Machine.Options[Machine.Highlight].Path == wanted)
                {
                    break;
                }

                Machine.Scroll(1, T0);
            }

            Assert.Equal(wanted, Machine.Options[Machine.Highlight].Path);
            Machine.Select(T0);
            Assert.Equal(MenuLevel.BoardAction, Machine.Level);
            return [.. Machine.Options.Select(o => o.VerbId!)];
        }
    }

    private static RequestSubmittedPayload Row(string ticket, Guid requester, Guid? claimant = null) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        DeploymentId = Deployment,
        TicketCode = ticket,
        TypeId = "mortar_fire",
        TargetRoleIds = ["mortar"],
        Priority = Priority.Normal,
        State = claimant is null ? RequestState.Open : RequestState.InProgress,
        RequestedByParticipantId = requester,
        RequestedByCallsign = "GHOST",
        ClaimedByParticipantId = claimant,
        ClaimedByCallsign = claimant is null ? null : "BEAR",
        Points = [],
        Modifiers = [],
        CreatedAt = T0,
        ExpiresAt = T0.AddSeconds(120),
        Version = 1,
    };

    [Fact]
    public void Somebody_asks_you_take_it_and_you_finish_it()
    {
        var session = new Session();

        // A request lands.
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));
        Assert.Single(session.Presenter.Rows);
        var row = session.Board.Rows[0];
        Assert.NotNull(row.Slot);

        // You take it. The whole point of the change: this is also starting it.
        var claimed = session.Board.ApplyClaim(row.Id, Viewer, "WOLF", row.Version + 1, T0);
        Assert.NotNull(claimed);
        Assert.Equal(RequestState.InProgress, claimed.State);
        session.Observer.Render();

        // It is on YOUR half of the board, holding its digit, and it did not vanish.
        Assert.Contains(session.Board.Yours, r => r.Id == row.Id);
        Assert.NotNull(claimed.Slot);

        // And the overlay offers exactly the verbs that can be honoured on it: no START anywhere,
        // and DONE reachable, which is the step that could not be reached at all before.
        var verbs = session.VerbsOn(claimed.Slot!.Value);
        Assert.Contains("done", verbs);
        Assert.Contains("release", verbs);
        Assert.DoesNotContain("start", verbs);
        Assert.DoesNotContain("accept", verbs);
    }

    [Fact]
    public void Up_is_the_menu_and_down_is_the_board_and_neither_crosses_the_other()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));
        session.Observer.OnRequestSubmitted(Row("MED-2", Mate));

        var menu = session.Machine;
        menu.OpenOnBoard(T0, session.ContextNow());

        // DOWN from rest is the board. Walking down stays on rows until they run out.
        Assert.NotNull(menu.HighlightedSlot);
        var firstRow = menu.Highlight;
        menu.Scroll(1, T0);
        Assert.NotNull(menu.HighlightedSlot);

        // UP off the top row is the menu, and it is the request categories first, never the tools.
        menu.Scroll(-1, T0);
        menu.Scroll(-1, T0);
        Assert.True(menu.HighlightIsARequest, "up off a row must reach the requests");
        Assert.NotEqual("home.more", menu.Options[menu.Highlight].Path);
        Assert.True(menu.Highlight < firstRow, "the menu is above the board, not below it");
    }

    [Fact]
    public void Tools_is_one_press_from_anywhere_and_back_never_closes()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));

        var menu = session.Machine;
        menu.OpenOnBoard(T0, session.ContextNow());

        // Into a request category, three levels from home.
        while (!menu.HighlightIsARequest)
        {
            menu.Scroll(-1, T0);
        }

        menu.Select(T0);
        Assert.Equal(MenuLevel.Branch, menu.Level);

        // 0 reaches the tools from there without walking back out.
        menu.Digit(0, T0);
        Assert.Equal(MenuLevel.More, menu.Level);

        // And back climbs out to rest rather than ending the interaction. It used to CLOSE at the
        // top, so the key meaning "I did not mean that" also dropped you out of the menu entirely.
        menu.Back(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
        menu.Back(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
        Assert.True(menu.IsOpen, "back must never close the menu");
    }

    [Fact]
    public void Your_own_request_offers_cancel_and_never_offers_you_your_own_job()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MED-9", Viewer));
        session.Observer.Render();

        var slot = session.Board.Rows.Concat(session.Board.Yours).Single().Slot;
        Assert.NotNull(slot);

        var verbs = session.VerbsOn(slot.Value);

        // Cancel parsed from voice and was offered nowhere, so a requester had no way to withdraw
        // their own request at all. Accepting your own is not a thing either.
        Assert.Contains("cancel", verbs);
        Assert.DoesNotContain("accept", verbs);
        Assert.DoesNotContain("pass", verbs);
    }

    [Fact]
    public void A_status_word_never_outlives_what_it_describes()
    {
        var session = new Session();

        // The exact sequence reported: a screen read refuses because the game window is gone.
        session.Observer.SetFault("NO GAME WINDOW  NOT READ, TRY AGAIN");
        Assert.NotNull(session.OnScreenFault);

        // The board ticks on, as it does every second. Nothing else happens: no keypress, no hold,
        // no game window, because the message is telling you the game window is what is missing.
        for (var second = 1; second <= 10; second++)
        {
            session.Board.Tick(T0.AddSeconds(second));
            session.Observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(second));
            session.Observer.Render();
        }

        Assert.Null(session.OnScreenFault);
    }

    [Fact]
    public void Every_refusal_the_overlay_can_show_clears_itself()
    {
        // The bug was never about one message. Any of these could stick for the whole session.
        var notices = new[]
        {
            "SUBMIT FAILED",
            "ALREADY TAKEN",
            "TOO MANY CLAIMS",
            "ROLE REFUSED",
            "JOIN REFUSED",
            "NOT RECOGNIZED",
            "NOT SUPPORTED",
            "CAPTURE OFF",
            "SIGN IN AGAIN",
            "DONE REFUSED",
        };

        foreach (var notice in notices)
        {
            var session = new Session();
            session.Observer.SetFault(notice);
            Assert.Equal(notice, session.OnScreenFault);

            session.Observer.ExpireNotice(DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.True(session.OnScreenFault is null, $"'{notice}' stuck to the header");
        }
    }

    [Fact]
    public void A_row_that_runs_out_of_time_leaves_the_screen_by_itself()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));
        Assert.Single(session.Presenter.Rows);

        // Nobody took it and its ttl ran out. No frame arrives: the overlay has the expiry time in
        // its own hand and must act on it. Two ended in the web UI and sat at zero on the overlay.
        session.Board.Tick(T0.AddSeconds(121));
        session.Observer.Render();

        Assert.Empty(session.Presenter.Rows);
        Assert.Equal(0, session.Presenter.OverflowCount);
    }

    [Fact]
    public void Work_somebody_else_is_doing_is_counted_and_not_drawn()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));
        var row = session.Board.Rows[0];

        // A third party takes it. It leaves your digits, because you cannot act on it, but the
        // IN PROGRESS count has to know it exists or a busy board reads as an idle one.
        session.Board.ApplyClaim(row.Id, Guid.NewGuid(), "FOX", row.Version + 1, T0);
        session.Observer.Render();

        Assert.Empty(session.Presenter.Rows);
        Assert.Empty(session.Presenter.Yours);
        Assert.Equal(1, session.Presenter.InProgressCount);
    }

    [Fact]
    public void Your_own_request_stays_in_sight_when_somebody_takes_it()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Viewer));
        var row = session.Board.Rows[0];

        session.Board.ApplyClaim(row.Id, Mate, "BEAR", row.Version + 1, T0);
        session.Observer.Render();

        // News, not a job: you see who took it and you get no digit, because there is nothing for
        // you to do to it.
        var yours = Assert.Single(session.Presenter.Yours);
        Assert.Equal("BEAR", session.Board.Yours[0].ClaimantCallsign);
        Assert.NotNull(yours);
        Assert.Null(session.Board.Yours[0].Slot);
    }

    [Fact]
    public void A_frame_from_the_match_you_left_never_reaches_the_screen()
    {
        var session = new Session();
        session.Observer.OnRequestSubmitted(Row("MTR-1", Mate));
        Assert.Single(session.Presenter.Rows);

        // The hop. The old subscription keeps delivering for a moment afterwards.
        session.Board.EnterDeployment(Guid.NewGuid(), T0.AddSeconds(1), draft: null);
        session.Observer.OnRequestSubmitted(Row("MTR-2", Mate));
        session.Observer.Render();

        Assert.Empty(session.Presenter.Rows);
    }

    [Fact]
    public void The_digit_you_press_is_the_row_you_looked_at()
    {
        var session = new Session();
        for (var i = 0; i < 3; i++)
        {
            session.Observer.OnRequestSubmitted(Row($"MTR-{i}", Mate));
        }

        session.Observer.Render();

        // The menu reads the snapshot the render left behind, not the live board. If the two ever
        // disagree, a digit names one row on screen and a different one when pressed.
        foreach (var row in session.Board.Rows.Concat(session.Board.Yours))
        {
            if (row.Slot is { } digit)
            {
                Assert.True(
                    session.Slots.ContainsKey(digit),
                    $"slot {digit} is drawn on the overlay and unknown to the menu");
            }
        }
    }
}
