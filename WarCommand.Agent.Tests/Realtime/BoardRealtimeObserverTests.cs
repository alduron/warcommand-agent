using System.Threading;
using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// The socket driving the board. Every frame lands here, and the rules it has to honour are the
/// ones that are invisible until a fire mission goes missing.
/// </summary>
public class BoardRealtimeObserverTests
{
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed record Harness(
        BoardRealtimeObserver Observer,
        BoardState Board,
        List<RealtimeConnectionState> States,
        List<Guid?> Deployments,
        List<int> ConfigReloads,
        List<BoardSnapshot> Renders);

    private static Harness Build()
    {
        var states = new List<RealtimeConnectionState>();
        var deployments = new List<Guid?>();
        var reloads = new List<int>();
        var renders = new List<BoardSnapshot>();

        var catalog = BundledContracts.Catalog().Current;
        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, DateTimeOffset.UtcNow, draft: null);

        var observer = new BoardRealtimeObserver(
            Dispatcher.CurrentDispatcher,
            new BoardPresenter(),
            () => catalog,
            states.Add,
            deployments.Add,
            () => reloads.Add(1),
            renders.Add);

        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA" });
        return new Harness(observer, board, states, deployments, reloads, renders);
    }

    private static RequestBody Row(string ticket, Guid? claimant = null) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        DeploymentId = Deployment,
        TicketCode = ticket,
        TypeId = "mortar",
        TargetRoleIds = ["mortar"],
        Priority = Priority.Normal,
        State = claimant is null ? RequestState.Open : RequestState.Claimed,
        RequestedByParticipantId = Other,
        RequestedByCallsign = "Ghost",
        ClaimedByParticipantId = claimant,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        Version = 1,
        Points = [],
    };

    // --- rows -----------------------------------------------------------------------------------

    [Fact]
    public void A_submitted_frame_puts_a_row_on_the_board()
    {
        var h = Build();
        var row = Row("MTR-14");

        h.Observer.OnRequestSubmitted(new RequestSubmittedPayload
        {
            Id = row.Id,
            GroupId = row.GroupId,
            DeploymentId = row.DeploymentId,
            TicketCode = row.TicketCode,
            TypeId = row.TypeId,
            TargetRoleIds = row.TargetRoleIds,
            Priority = row.Priority,
            State = row.State,
            RequestedByParticipantId = row.RequestedByParticipantId,
            RequestedByCallsign = row.RequestedByCallsign,
            CreatedAt = row.CreatedAt,
            ExpiresAt = row.ExpiresAt,
            Version = row.Version,
            Points = row.Points,
        });

        Assert.NotNull(h.Board.ById(row.Id));
        Assert.Single(h.Renders);
    }

    /// <summary>
    /// Every non-claimant drops the row on request.claimed. This is the fastest slot-release path
    /// in the design, and it is why the four row-returning frames must carry a whole body.
    /// </summary>
    [Fact]
    public void A_claimed_frame_frees_its_digit_and_becomes_one_of_the_in_progress_count()
    {
        var h = Build();
        var row = Row("MTR-14");
        _ = h.Board.Upsert(row.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        h.Observer.OnRequestClaimed(new RequestClaimedPayload
        {
            RequestId = row.Id,
            ClaimedByParticipantId = Other,
            Callsign = "Bear",
            Version = 2,
        });

        // Off the board entirely: no digit, drawn as no row at all. It used to be REMOVED, and the
        // counter only counts rows that are still here, so IN PROGRESS decayed towards zero while
        // the work was actually running and a busy board read as an idle one.
        Assert.DoesNotContain(h.Board.Rows, r => r.Id == row.Id);
        Assert.Empty(h.Board.Yours);
        Assert.Equal(1, h.Board.InProgressCount);
    }

    /// <summary>
    /// completed with outcome unable puts the row back, and the payload carries the whole body
    /// because every board already dropped it.
    /// </summary>
    [Fact]
    public void Completed_unable_reopens_the_row_from_the_body_it_carries()
    {
        var h = Build();
        var row = Row("MTR-14");

        h.Observer.OnRequestCompleted(new RequestCompletedPayload
        {
            RequestId = row.Id,
            Version = 3,
            Outcome = Outcome.Unable,
            Reason = "out of range",
            Request = row,
        });

        Assert.NotNull(h.Board.ById(row.Id));
    }

    [Fact]
    public void Completed_delivered_leaves_the_row_off_the_board()
    {
        var h = Build();
        var row = Row("MTR-14");
        _ = h.Board.Upsert(row.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        h.Observer.OnRequestCompleted(new RequestCompletedPayload
        {
            RequestId = row.Id,
            Version = 3,
            Outcome = Outcome.Serviced,
        });

        Assert.Null(h.Board.ById(row.Id));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("superseded")]
    public void The_terminal_frames_all_drop_the_row(string frame)
    {
        var h = Build();
        var row = Row("MTR-14");
        _ = h.Board.Upsert(row.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        switch (frame)
        {
            case "cancelled":
                h.Observer.OnRequestCancelled(new RequestCancelledPayload { RequestId = row.Id, Version = 2 });
                break;
            case "expired":
                h.Observer.OnRequestExpired(new RequestExpiredPayload { RequestId = row.Id });
                break;
            default:
                h.Observer.OnRequestSuperseded(new RequestSupersededPayload
                {
                    RequestId = row.Id,
                    SupersededByRequestId = Guid.NewGuid(),
                    Version = 2,
                });
                break;
        }

        Assert.Null(h.Board.ById(row.Id));
    }

    /// <summary>
    /// The database's answer to a divergent presence. Restore exactly the named rows and drop any
    /// held claim that is not in the set. Never a release: presence is a report, the database wins.
    /// </summary>
    [Fact]
    public void Claims_reconcile_restores_the_named_rows_and_drops_the_rest()
    {
        var h = Build();
        var stale = Row("MTR-09", claimant: Viewer);
        var kept = Row("MTR-14", claimant: Viewer);
        _ = h.Board.Upsert(stale.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        h.Observer.OnClaimsReconcile(new ClaimsReconcilePayload { Requests = [kept] });

        Assert.Null(h.Board.ById(stale.Id));
        Assert.NotNull(h.Board.ById(kept.Id));
    }

    // --- the hop --------------------------------------------------------------------------------

    /// <summary>
    /// On the hop the board is dropped outright. Keeping a row would let "accept 4" claim a request
    /// on a server the player already left.
    /// </summary>
    [Fact]
    public void Clearing_the_board_lets_go_of_it_entirely()
    {
        var h = Build();
        _ = h.Board.Upsert(Row("MTR-14").ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        h.Observer.OnBoardCleared(BoardClearReason.DeploymentEntered);

        Assert.Null(h.Observer.Board);
    }

    /// <summary>The id from the frame, never a remembered one.</summary>
    [Fact]
    public void Entering_a_deployment_reports_the_id_the_frame_carried()
    {
        var h = Build();
        var moved = Guid.NewGuid();

        h.Observer.OnDeploymentEntered(new DeploymentEnteredPayload
        {
            GroupId = Guid.NewGuid(),
            DeploymentId = moved,
            Label = "BRAVO",
            MemberCount = 12,
            Source = DeploymentEnterSource.Invite,
        });

        Assert.Equal([moved], h.Deployments);
    }

    [Fact]
    public void Closing_the_deployment_leaves_no_board_and_reports_nothing_stood_on()
    {
        var h = Build();

        h.Observer.OnDeploymentClosed(new DeploymentClosedPayload
        {
            GroupId = Guid.NewGuid(),
            DeploymentId = Deployment,
        });

        Assert.Null(h.Observer.Board);
        Assert.Equal([(Guid?)null], h.Deployments);
    }

    /// <summary>The frame carries no config, so the only correct response is to go and read it.</summary>
    [Theory]
    [InlineData("config")]
    [InlineData("resync")]
    public void The_config_frames_ask_for_a_re_read(string frame)
    {
        var h = Build();

        if (frame == "config")
        {
            h.Observer.OnConfigChanged(new ConfigChangedPayload { ConfigVersion = 7 });
        }
        else
        {
            h.Observer.OnResyncRequired(new ResyncRequiredPayload { Reason = "replay buffer missed" });
        }

        Assert.Single(h.ConfigReloads);
    }

    // --- what the tray and the header read ------------------------------------------------------

    [Fact]
    public void The_connection_state_is_passed_straight_through()
    {
        var h = Build();

        h.Observer.OnConnectionStateChanged(RealtimeConnectionState.Connected);
        h.Observer.OnConnectionStateChanged(RealtimeConnectionState.Reconnecting);

        Assert.Equal(
            [RealtimeConnectionState.Connected, RealtimeConnectionState.Reconnecting],
            h.States);
    }

    /// <summary>Presence is a report of what this device holds, and only what it holds.</summary>
    [Fact]
    public void Presence_reports_the_viewers_own_claims_and_nobody_elses()
    {
        var h = Build();
        var mine = Row("MTR-14", claimant: Viewer);
        var theirs = Row("MTR-15", claimant: Other);
        _ = h.Board.Upsert(mine.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);
        _ = h.Board.Upsert(theirs.ToBoardRow("MORTAR"), DateTimeOffset.UtcNow);

        Assert.Equal([mine.Id], h.Observer.ClaimedRequestIds);
    }

    [Fact]
    public void With_no_board_presence_reports_nothing_rather_than_throwing()
    {
        var h = Build();
        h.Observer.Detach();

        Assert.Empty(h.Observer.ClaimedRequestIds);
    }

    /// <summary>A stalled event channel is a different fault from a dropped socket, and says so.</summary>
    [Fact]
    public void Board_staleness_is_a_header_fault_not_a_connection_state()
    {
        var h = Build();

        h.Observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 45);

        Assert.Empty(h.States);
    }
}
