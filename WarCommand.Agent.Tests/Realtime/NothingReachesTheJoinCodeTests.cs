using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;
using Xunit;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// The header's right cell holds the join code and nothing else. Six digits somebody is about to
/// read out loud cannot be covered by a word about something that went wrong somewhere.
/// </summary>
/// <remarks>
/// Two guarantees, and the second is what keeps the first true next year. Every text-bearing entry
/// point on the observer is called here and none of them may move the cell; and the set of public
/// entry points is asserted, so a new one added later fails this test until somebody has decided
/// which of the two lists it belongs in.
/// </remarks>
public sealed class NothingReachesTheJoinCodeTests
{
    private const string Code = "921585";

    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>The only entry point allowed to write the cell, and the only thing it may write.</summary>
    private const string TheOneWriter = "OnDeploymentRoster";

    [Fact]
    public void No_word_from_any_source_can_reach_the_join_code()
    {
        var (observer, presenter) = Build();

        observer.SetFault("SUBMIT FAILED");
        observer.SetNote("COPIED");
        observer.SetHint("HOLD CAPSLOCK");
        observer.OnAnotherDeviceOnBoard(present: true);
        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.OnCredentialsRejected("device_revoked");
        observer.OnConnectionStateChanged(RealtimeConnectionState.Reconnecting);
        observer.SetRoles(["mortar", "attack_vehicle"]);
        observer.OnPendingDraftAborted(DraftAbortReason.DeploymentChanged);
        observer.SetGunPosition(new GunPosition(
            "l81",
            new MapPoint(1m, 2m, "map_readout", null, null),
            DateTimeOffset.UnixEpoch));
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(Code, presenter.Header!.Right);
    }

    /// <summary>The cell moves for a rotation, which is the whole reason the frame carries one.</summary>
    [Fact]
    public void The_roster_frame_is_the_one_thing_that_moves_it()
    {
        var (observer, presenter) = Build();

        observer.OnDeploymentRoster(new DeploymentRosterPayload
        {
            DeploymentId = Deployment,
            MemberCount = 9,
            InviteCode = "440217",
            RotatedByCallsign = "Bear",
        });

        Assert.Equal("440217", presenter.Header!.Right);
    }

    /// <summary>
    /// The list is the point. A new entry point is not covered by the calls above, so it is named
    /// here or this fails, and naming it is what makes somebody ask whether it writes the cell.
    /// </summary>
    [Fact]
    public void Every_entry_point_on_the_observer_is_accounted_for()
    {
        string[] known =
        [
            "Attach", "Detach", "Render", "TurnPage", "ExpireNotice",
            "SetFault", "SetNote", "SetHint", "SetRoles", "SetGunPosition",
            "OnConnectionStateChanged", "OnReady", "OnBoardStalenessChanged", "OnAnotherDeviceOnBoard",
            "OnRequestSubmitted", "OnRequestClaimed",
            "OnRequestReleased", "OnRequestEscalated", "OnRequestCompleted", "OnRequestAdvanced",
            "OnRequestAbandoned", "OnRequestReopened", "OnRequestSuperseded", "OnClaimsReconcile",
            "OnErrorFrame", "OnBoardCleared", "OnDeploymentEntered", TheOneWriter,
            "OnDeploymentClosed", "OnPendingDraftAborted", "OnCredentialsRejected",
            "OnResyncRequired", "OnConfigChanged", "OnSubscriptionsChanged", "OnMembershipEnded",
            "OnIdentityAccountLinked",
        ];

        var declared = typeof(BoardRealtimeObserver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var unaccounted = declared.Except(known, StringComparer.Ordinal).ToArray();
        Assert.True(
            unaccounted.Length == 0,
            $"New entry point(s): {string.Join(", ", unaccounted)}. "
            + "Say whether one may write the header's right cell, then add it here.");

        // And the reverse, so the list cannot rot into naming methods that are gone.
        var stale = known.Except(declared, StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, $"Gone, still listed: {string.Join(", ", stale)}");
    }

    private static (BoardRealtimeObserver Observer, BoardPresenter Presenter) Build()
    {
        var catalog = BundledContracts.Catalog().Current;
        var presenter = new BoardPresenter();
        var observer = new BoardRealtimeObserver(
            Dispatcher.CurrentDispatcher,
            presenter,
            () => catalog,
            _ => { },
            _ => { },
            () => { },
            _ => { });

        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, DateTimeOffset.UnixEpoch, draft: null);
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA", Right = Code });
        return (observer, presenter);
    }
}
