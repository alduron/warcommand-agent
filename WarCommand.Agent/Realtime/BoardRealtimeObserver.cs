using System.Linq;
using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
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
    private readonly Func<DateTimeOffset> _serverNow;
    private readonly Action<string?, int?> _onRoster;
    private readonly Action _onDraftAborted;
    private readonly Action<string> _onCredentialsRejected;

    private BoardState? _board;
    private Guid _viewerId;
    private BoardHeader _header = new() { Title = "WarCommand" };
    /// <summary>
    /// A one-off notice: a refusal, a failed read, a confirmation. It clears itself.
    /// </summary>
    /// <remarks>
    /// It used to be cleared by exactly one thing, a hold opening, and a hold needs the game to be
    /// the foreground window. So "NO GAME WINDOW  NOT READ, TRY AGAIN" could never be cleared by
    /// definition: the one condition that removed it was the one the message said was absent. Every
    /// notice now has a deadline, because a status that outlives what it describes is a lie about
    /// the current state.
    /// </remarks>
    private string? _notice;

    private DateTimeOffset _noticeUntil;

    /// <summary>
    /// A standing condition, shown while it holds and cleared by its own signal, never by time.
    /// </summary>
    private string? _condition;
    private GunPosition? _gunPosition;

    /// <summary>Creates the observer. The board is attached once a deployment is known.</summary>
    public BoardRealtimeObserver(
        Dispatcher dispatcher,
        BoardPresenter presenter,
        Func<Catalog> catalog,
        Action<RealtimeConnectionState> onState,
        Action<Guid?> onDeploymentChanged,
        Action onConfigChanged,
        Action<BoardSnapshot> onRendered,
        Func<DateTimeOffset>? serverNow = null,
        Action<string?, int?>? onRoster = null,
        Action? onDraftAborted = null,
        Action<string>? onCredentialsRejected = null)
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
        _serverNow = serverNow ?? (() => DateTimeOffset.UtcNow);
        _onRoster = onRoster ?? ((_, _) => { });
        _onDraftAborted = onDraftAborted ?? (() => { });
        _onCredentialsRejected = onCredentialsRejected ?? (_ => { });
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
            _notice = null;
            _condition = null;
            RenderHeader();
        });
    }

    /// <summary>
    /// The event channel stalled while the socket stayed up. A different fault from the amber dot,
    /// and it gets the header's own word rather than the connection colour.
    /// </summary>
    public void OnBoardStalenessChanged(bool stale, double drainAgeSeconds) => OnUi(() =>
    {
        _condition = stale ? "BOARD MAY BE STALE" : null;
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
                _serverNow());

            Render();
        });
    }

    /// <summary>
    /// START moved the row on. The local VERSION has to move with it.
    /// </summary>
    /// <remarks>
    /// This frame was never handled, so the row kept the version it had before START while the
    /// server bumped it. Every later DONE, RELEASE or START sent the stale version, the server's
    /// conditional update matched nothing, and the 409 was swallowed. A provider who pressed START
    /// could never close the job, and a claimed row never expires, so it sat there until the
    /// deployment did.
    /// </remarks>
    public void OnRequestStarted(RequestStartedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            _board?.ApplyProgress(payload.RequestId, RequestState.InProgress, payload.Version);
            Render();
        });
    }

    /// <inheritdoc />
    /// <remarks>Non-terminal: the row stays in progress and only its version moves.</remarks>
    public void OnRequestRoundsAway(RequestRoundsAwayPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            _board?.ApplyProgress(payload.RequestId, RequestState.InProgress, payload.Version);
            Render();
        });
    }

    /// <inheritdoc />
    public void OnRequestAdjusted(RequestAdjustedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        OnUi(() =>
        {
            _board?.ApplyProgress(payload.RequestId, RequestState.InProgress, payload.Version);
            Render();
        });
    }

    /// <summary>
    /// The server refused something. Say so.
    /// </summary>
    /// <remarks>
    /// Every error frame was dropped on the floor. Two providers race for a row and the loser's
    /// row simply vanished with no word; at the claim cap, accept read as a dead key. A refusal the
    /// user cannot see is indistinguishable from the product not working.
    /// </remarks>
    public void OnErrorFrame(ErrorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        OnUi(() => SetFaultCore(FaultFor(payload)));
    }

    private static string FaultFor(ErrorPayload payload) => payload.Code switch
    {
        "request_already_claimed" => "ALREADY TAKEN",
        "version_conflict" => "ROW MOVED ON, TRY AGAIN",
        "too_many_claims" => "TOO MANY CLAIMS",
        "rate_limited" => "SLOW DOWN",
        "forbidden" => "NOT ALLOWED",
        "request_expired" => "EXPIRED",
        _ => payload.Code.Replace('_', ' ').ToUpperInvariant(),
    };

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

            var now = _serverNow();
            var keep = payload.Rows.Select(r => r.Id).ToHashSet();

            foreach (var held in ClaimedRequestIds.Where(id => !keep.Contains(id)).ToList())
            {
                _ = board.Remove(held, now);
            }

            foreach (var row in payload.Rows)
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

    /// <summary>The headcount and the invite code, re-emitted whenever either changes.</summary>
    /// <remarks>
    /// It updated the header and nothing else, so after a rotation MORE &gt; COPY INVITE handed out
    /// six dead digits and the PEOPLE page kept the headcount it was born with.
    /// </remarks>
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
            _onRoster(payload.InviteCode, payload.MemberCount);
        });
    }

    /// <summary>
    /// The hop is starting: whatever was half-composed belongs to the match being left.
    /// </summary>
    /// <remarks>
    /// Unimplemented, so a request composed for one match could be submitted to the next one, and
    /// the frozen slot set claimed a digit on a board that had never issued it.
    /// </remarks>
    public void OnPendingDraftAborted(DraftAbortReason reason) => OnUi(_onDraftAborted);

    /// <summary>The credentials on disk are no longer accepted. Say so, and let the root re-register.</summary>
    public void OnCredentialsRejected(string code) => OnUi(() =>
    {
        SetFaultCore("SIGN IN AGAIN");
        _onCredentialsRejected(code);
    });

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
        _condition = present ? "ANOTHER DEVICE ON BOARD" : null;
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

            _ = board.Upsert(row.ToBoardRow(OverlayLabel(row.TypeId)), _serverNow());
            Render();
        });
    }

    private void Remove(Guid requestId) => OnUi(() =>
    {
        if (_board is not { } board || !board.Remove(requestId, _serverNow()))
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

        // Server time, not this machine's. expires_at is the server's wall clock, so a machine two
        // minutes fast dropped every open row from the overlay two minutes early while the web
        // board still showed them live. The offset is derived on every ready frame and was read by
        // nothing at all.
        var now = _serverNow();
        var glyphs = new RoleGlyphSource(_catalog().Role);

        // Served map scale, so a two-point row reads in metres. See RefreshBoardAsync.
        var unitsToMeters = BundledContracts.GameProfile().Current.DefaultUnitsToMeters;

        // Once, not once per row. It is the same context for every row on the board.
        var fire = FireContextNow();

        var rows = board.Rows
            .Select(r => BoardRowViewModel
                .FromPrimary(r, _viewerId, now, unitsToMeters, fire)
                .WithGlyph(glyphs))
            .ToList();
        var yours = board.Yours
            .Select(r => BoardRowViewModel.FromSecondary(r, now, unitsToMeters, _viewerId).WithGlyph(glyphs))
            .ToList();
        var overflow = board.Overflow;
        var urgent = overflow.Count(r => r.Priority == Priority.Urgent);

        _presenter.RenderBoard(rows, yours, overflow.Count, urgent, board.InProgressCount);

        // Built HERE, on the dispatcher, while nothing else is mutating the board. The menu used to
        // walk BoardState.Rows from the hook thread while socket frames were writing to it, which
        // is an InvalidOperationException that kills the hook and takes every hotkey with it.
        // EVERY row holding a digit, from both halves. Rows excludes anything that renders in
        // YOURS, and a job you have claimed is exactly that: it keeps its digit and moves down. So
        // the menu had no entry for the one row you actually have work to do on, and DONE could not
        // be pressed on the job you were doing.
        var slots = new Dictionary<int, SlotState>();
        foreach (var row in board.Rows.Concat(board.Yours))
        {
            if (row.Slot is { } digit)
            {
                slots[digit] = new SlotState(
                    row.State,
                    row.ClaimantParticipantId == _viewerId,
                    row.RequestedByParticipantId == _viewerId);
            }
        }

        _onRendered(new BoardSnapshot(
            rows.Count + yours.Count + overflow.Count,
            rows.Count(r => r.Accent == RowAccent.Mine),
            slots));
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
    public void SetFault(string? fault) => OnUi(() => SetFaultCore(fault));

    /// <summary>
    /// How long a notice stays on the header before it takes itself off.
    /// </summary>
    /// <remarks>
    /// Long enough to read a short line, short enough that it cannot be mistaken for the state of
    /// the system a minute later.
    /// </remarks>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(6);

    private void SetFaultCore(string? fault)
    {
        _notice = fault;
        _noticeUntil = fault is null ? default : DateTimeOffset.UtcNow + NoticeLifetime;
        RenderHeader();
    }

    /// <summary>
    /// Takes an expired notice off the header. Driven by the board tick, and by nothing else.
    /// </summary>
    /// <remarks>
    /// Called before the tick's board guard: an agent standing in no deployment is exactly where a
    /// notice was most likely to be stuck, because there is no board there to redraw over it.
    /// </remarks>
    public void ExpireNotice(DateTimeOffset now) => OnUi(() =>
    {
        if (_notice is null || now < _noticeUntil)
        {
            return;
        }

        _notice = null;
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

    // The notice wins while it lives, then the standing condition shows through again.
    private void RenderHeader() => _presenter.SetHeader(_header with { Fault = _notice ?? _condition });

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
/// <param name="OpenCount">Rows drawing a digit right now.</param>
/// <param name="MineCount">How many of them are the viewer's own.</param>
/// <param name="Slots">
/// What each digit holds, built here on the UI thread so the hook thread never walks the board.
/// </param>
public sealed record BoardSnapshot(
    int OpenCount,
    int MineCount,
    IReadOnlyDictionary<int, SlotState> Slots);
