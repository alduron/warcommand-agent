using System.Linq;
using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Realtime;

/// <summary>
/// Turns socket frames into board state and a rendered board.
/// </summary>
/// <remarks>
/// Every frame lands on a socket thread and is marshalled onto the UI dispatcher here rather than
/// at each of the twenty-odd call sites. <see cref="BoardState"/> is not thread safe and the
/// presenter touches WPF, so nothing below this line may run anywhere else.
/// <para>
/// The four row-returning frames all carry a whole <see cref="RequestBody"/> and are treated as
/// upserts. The agent may never assume it holds prior state for a request id, because
/// <c>request.claimed</c> made every non-claimant drop it.
/// </para>
/// </remarks>
public sealed class BoardRealtimeObserver : IRealtimeObserver
{
    private readonly Dispatcher _dispatcher;
    private readonly BoardPresenter _presenter;
    private readonly Func<Catalog> _catalog;
    private readonly Action<RealtimeConnectionState> _onState;
    private readonly Action<Guid?> _onDeploymentChanged;
    private readonly Action _onConfigChanged;
    private readonly Action<BoardSnapshot> _onRendered;

    private BoardState? _board;
    private Guid _viewerId;
    private BoardHeader _header = new() { Title = "WarCommand" };
    private string? _fault;
    private GunPosition? _gunPosition;

    /// <summary>Creates the observer. The board is attached once a deployment is known.</summary>
    public BoardRealtimeObserver(
        Dispatcher dispatcher,
        BoardPresenter presenter,
        Func<Catalog> catalog,
        Action<RealtimeConnectionState> onState,
        Action<Guid?> onDeploymentChanged,
        Action onConfigChanged,
        Action<BoardSnapshot> onRendered)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(onState);
        ArgumentNullException.ThrowIfNull(onDeploymentChanged);
        ArgumentNullException.ThrowIfNull(onConfigChanged);
        ArgumentNullException.ThrowIfNull(onRendered);

        _dispatcher = dispatcher;
        _presenter = presenter;
        _catalog = catalog;
        _onState = onState;
        _onDeploymentChanged = onDeploymentChanged;
        _onConfigChanged = onConfigChanged;
        _onRendered = onRendered;
    }

    /// <summary>The board the socket is driving, or null before a deployment is known.</summary>
    public BoardState? Board => _board;

    /// <summary>
    /// Points the observer at a deployment. Called by the composition root after the HTTPS seed,
    /// which is the only seed there is.
    /// </summary>
    public void Attach(BoardState board, Guid viewerParticipantId, BoardHeader header)
    {
        ArgumentNullException.ThrowIfNull(board);

        _board = board;
        _viewerId = viewerParticipantId;
        _header = header;
        RenderHeader();
    }

    /// <summary>Lets go of the board. Standing on no deployment is not a fault, and not a board.</summary>
    public void Detach() => _board = null;

    /// <summary>Claimed rows, for the presence heartbeat. Empty before a board exists.</summary>
    public IReadOnlyList<Guid> ClaimedRequestIds => _board is null
        ? []
        : [.. _board.All.Where(r => r.IsClaimedBy(_viewerId)).Select(r => r.Id)];

    // --- connection -----------------------------------------------------------------------------

    /// <inheritdoc />
    public void OnConnectionStateChanged(RealtimeConnectionState state) => OnUi(() => _onState(state));

    /// <inheritdoc />
    public void OnReady(ReadyPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // An empty subscription set is the normal cold-start state, not a fault. The composition
        // root decides what to seed from; this only clears any stale amber word.
        OnUi(() =>
        {
            _fault = null;
            RenderHeader();
        });
    }

    /// <summary>
    /// The event channel stalled while the socket stayed up. A different fault from the amber dot,
    /// and it gets the header's own word rather than the connection colour.
    /// </summary>
    public void OnBoardStalenessChanged(bool stale, double drainAgeSeconds) => OnUi(() =>
    {
        _fault = stale ? "BOARD MAY BE STALE" : null;
        RenderHeader();
    });

    // --- rows -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public void OnRequestSubmitted(RequestSubmittedPayload payload) => Upsert(payload);

    /// <summary>Every non-claimant drops the row. The fastest slot-release path in the design.</summary>
    /// <summary>
    /// A claim clears the row from every board except the two people in it.
    /// </summary>
    /// <remarks>
    /// It used to Remove for everyone, so the provider who accepted a job watched it disappear off
    /// their own overlay instead of dropping into YOURS, and the requester lost sight of the
    /// request they had just made.
    /// </remarks>
    public void OnRequestClaimed(RequestClaimedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            _board?.ApplyClaim(
                payload.RequestId,
                payload.ClaimedByParticipantId,
                payload.Callsign,
                payload.Version,
                DateTimeOffset.UtcNow);

            Render();
        });
    }

    /// <inheritdoc />
    public void OnRequestReleased(RequestReleasedPayload payload) => Upsert(payload);

    /// <inheritdoc />
    public void OnRequestEscalated(RequestEscalatedPayload payload) => Upsert(payload);

    /// <summary>Only outcome unable puts a row back, and then the body is required.</summary>
    public void OnRequestCompleted(RequestCompletedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.ReturnsToOpen)
        {
            Upsert(payload.ReopenedRow);
            return;
        }

        Remove(payload.RequestId);
    }

    /// <inheritdoc />
    public void OnRequestCancelled(RequestCancelledPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Remove(payload.RequestId);
    }

    /// <inheritdoc />
    public void OnRequestExpired(RequestExpiredPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Remove(payload.RequestId);
    }

    /// <summary>Terminal. The successor arrives as its own submitted frame.</summary>
    public void OnRequestSuperseded(RequestSupersededPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Remove(payload.RequestId);
    }

    /// <summary>
    /// The database's answer to a divergent presence. Restore exactly these rows and drop any held
    /// claim that is not in the set. Never a release: presence is a report and the database wins.
    /// </summary>
    public void OnClaimsReconcile(ClaimsReconcilePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            if (_board is not { } board)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var keep = payload.Requests.Select(r => r.Id).ToHashSet();

            foreach (var held in ClaimedRequestIds.Where(id => !keep.Contains(id)).ToList())
            {
                _ = board.Remove(held, now);
            }

            foreach (var row in payload.Requests)
            {
                _ = board.Upsert(row.ToBoardRow(OverlayLabel(row.TypeId)), now);
            }

            Render();
        });
    }

    // --- the deployment -------------------------------------------------------------------------

    /// <summary>
    /// The hop. Drop every row and reset the allocator including its reissue order, or "accept 4"
    /// claims a request on a server you already left.
    /// </summary>
    public void OnBoardCleared(BoardClearReason reason) => OnUi(() =>
    {
        _board = null;
        _presenter.ShowEmptyState("Switching deployment", "re-reading the board");
    });

    /// <inheritdoc />
    public void OnDeploymentEntered(DeploymentEnteredPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The id from this frame, never a remembered one.
        var deployment = payload.DeploymentId;
        OnUi(() => _onDeploymentChanged(deployment));
    }

    /// <summary>Header only. Carries the invite code, re-emitted whenever it rotates.</summary>
    public void OnDeploymentRoster(DeploymentRosterPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            _header = _header with
            {
                PeopleCount = payload.MemberCount,
                Right = payload.InviteCode,
            };
            RenderHeader();
        });
    }

    /// <summary>The whole stand-down. The individual cancels are deliberately not sent.</summary>
    public void OnDeploymentClosed(DeploymentClosedPayload payload) => OnUi(() =>
    {
        _board = null;
        _onDeploymentChanged(null);
    });

    /// <summary>The replay buffer missed. Drop local state and re-seed over HTTPS.</summary>
    public void OnResyncRequired(ResyncRequiredPayload payload) => OnUi(() => _onConfigChanged());

    /// <summary>Refetch the config payload over HTTPS. The frame carries no config.</summary>
    public void OnConfigChanged(ConfigChangedPayload payload) => OnUi(() => _onConfigChanged());

    /// <summary>Server-derived. The client never names a topic and never asks to subscribe.</summary>
    /// <summary>
    /// The viewer's roles changed, on the web or anywhere else. Re-read the config.
    /// </summary>
    /// <remarks>
    /// This used to report a deployment id, and App ignores one that matches where it already
    /// stands, so changing a role on the website moved nothing on the overlay: the header kept the
    /// old glyphs because subscribed_role_ids only ever arrives on /v1/me.
    /// </remarks>
    public void OnSubscriptionsChanged(SubscriptionsChangedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() => _onConfigChanged());
    }

    /// <summary>Stop rendering that group and say why. The socket stays up.</summary>
    public void OnMembershipEnded(MembershipEndedPayload payload) => OnUi(() => _onConfigChanged());

    /// <summary>The account the agent holds changed underneath it. Re-read everything.</summary>
    public void OnIdentityAccountLinked(IdentityAccountLinkedPayload payload) =>
        OnUi(() => _onConfigChanged());

    /// <summary>Another live session holds the same participant, so the digits differ per device.</summary>
    public void OnAnotherDeviceOnBoard(bool present) => OnUi(() =>
    {
        _fault = present ? "ANOTHER DEVICE ON BOARD" : null;
        RenderHeader();
    });

    // --- rendering ------------------------------------------------------------------------------

    private void Upsert(RequestBody row)
    {
        ArgumentNullException.ThrowIfNull(row);

        OnUi(() =>
        {
            if (_board is not { } board)
            {
                return;
            }

            _ = board.Upsert(row.ToBoardRow(OverlayLabel(row.TypeId)), DateTimeOffset.UtcNow);
            Render();
        });
    }

    private void Remove(Guid requestId) => OnUi(() =>
    {
        if (_board is not { } board || !board.Remove(requestId, DateTimeOffset.UtcNow))
        {
            return;
        }

        Render();
    });

    /// <summary>One snapshot to every surface. Called after any frame that moved a row.</summary>
    public void Render()
    {
        if (_board is not { } board)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var glyphs = new RoleGlyphSource(_catalog().Role);

        // Served map scale, so a two-point row reads in metres. See RefreshBoardAsync.
        var unitsToMeters = BundledContracts.GameProfile().Current.DefaultUnitsToMeters;

        var rows = board.Rows
            .Select(r => BoardRowViewModel
                .FromPrimary(r, _viewerId, now, unitsToMeters, FireContextNow())
                .WithGlyph(glyphs))
            .ToList();
        var yours = board.Yours
            .Select(r => BoardRowViewModel.FromSecondary(r, now, unitsToMeters, _viewerId).WithGlyph(glyphs))
            .ToList();
        var overflow = board.Overflow;
        var urgent = overflow.Count(r => r.Priority == Priority.Urgent);

        _presenter.RenderBoard(rows, yours, overflow.Count, urgent, board.InProgressCount);
        _onRendered(new BoardSnapshot(
            rows.Count + yours.Count + overflow.Count,
            rows.Count(r => r.Accent == RowAccent.Mine)));
    }

    /// <summary>Replaces the header's hint cell, which the menu owns while it is open.</summary>
    public void SetHint(string? hint) => OnUi(() =>
    {
        _header = _header with { Hint = hint };
        RenderHeader();
    });

    /// <summary>
    /// Where the viewer's gun is. Every subsequent render draws a bracket on that role's rows.
    /// </summary>
    /// <remarks>
    /// Null until MORE > GUN HERE reads the map, which is the normal state for anybody not on a
    /// gun. Rendering immediately, because the point of setting it is to see the brackets appear.
    /// </remarks>
    public void SetGunPosition(GunPosition? gun) => OnUi(() =>
    {
        _gunPosition = gun;
        Render();
    });

    /// <summary>
    /// The roles the header draws, after a toggle changed what this participant receives.
    /// </summary>
    public void SetRoles(IReadOnlyCollection<string> roleIds) => OnUi(() =>
    {
        _header = _header with { RoleIds = [.. roleIds] };
        RenderHeader();
    });

    /// <summary>Shows a fault word in the header, or clears it with null.</summary>
    public void SetFault(string? fault) => OnUi(() =>
    {
        _fault = fault;
        RenderHeader();
    });

    /// <summary>
    /// The bracket context, or null when the viewer has set no gun position.
    /// </summary>
    /// <remarks>
    /// The weapon's role decides which rows draw a bracket, so a mortarman sees one on mortar rows
    /// and on nothing else.
    /// </remarks>
    private FireContext? FireContextNow()
    {
        if (_gunPosition is not { } gun)
        {
            return null;
        }

        var ballistics = BundledContracts.Ballistics().Current;
        var weapon = ballistics.Weapon(gun.WeaponId);

        return weapon is null
            ? null
            : new FireContext(
                gun,
                weapon.Role,
                ballistics,
                BundledContracts.GameProfile().Current,
                null);
    }

    private void RenderHeader() => _presenter.SetHeader(_header with { Fault = _fault });

    private string OverlayLabel(string typeId) =>
        _catalog().RequestType(typeId)?.OverlayLabel ?? typeId.ToUpperInvariant();

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = _dispatcher.BeginInvoke(action);
    }
}

/// <summary>What the tray's counts read after a render.</summary>
public sealed record BoardSnapshot(int OpenCount, int MineCount);
