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

    /// <summary>How loudly the pending notice reads. A confirmation is not a fault.</summary>
    private StatusSeverity _noticeSeverity = StatusSeverity.Fault;

    /// <summary>
    /// The standing conditions, keyed by source, each one carrying its own way out.
    /// </summary>
    /// <remarks>
    /// It was a single nullable string. Two independent sources wrote it, so each erased the
    /// other's word in both directions, and nothing dropped it on a hop or a disconnect. The
    /// header's right cell is the same cell the join code lives in, so a word nobody took down
    /// covered the invite code for the rest of the session.
    /// </remarks>
    private readonly HeaderConditions _conditions = new();

    /// <summary>
    /// When a transient empty state stops being believable, or null when none is showing.
    /// </summary>
    /// <remarks>
    /// "re-reading the board" describes an operation in flight. If that operation never lands the
    /// banner is a claim about something that is not happening, and there is no board underneath
    /// to redraw over it. The tick escalates it to the truth and asks for the read again.
    /// </remarks>
    private DateTimeOffset? _transientUntil;

    /// <summary>How long the watchdog waits before asking again. Doubles while the reads fail.</summary>
    private TimeSpan _transientWait = TimeSpan.FromSeconds(12);
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
        _transientUntil = null;
        _transientWait = TransientEmptyStateLimit;
        RenderHeader();
    }

    /// <summary>
    /// Whether a deployment frame obliges the composition root to re-read the config.
    /// </summary>
    /// <remarks>
    /// The id alone is not the condition, and treating it as one is what stuck the overlay on
    /// "SWITCHING DEPLOYMENT". Every deployment.entered clears the board FIRST, and an entry made
    /// over HTTPS (the tray's switch, or a config re-read that won the race) has already moved the
    /// standing id to the frame's by the time it lands. Standing on the right deployment holding
    /// no board is not standing anywhere.
    /// </remarks>
    public static bool NeedsConfigReload(Guid? frameDeployment, Guid? standingOn, bool hasBoard) =>
        frameDeployment != standingOn || !hasBoard;

    /// <summary>
    /// Moves the board's window of nine and redraws. Returns false when there is only one page.
    /// </summary>
    public bool TurnPage(int delta)
    {
        if (_board is not { } board || board.PageCount <= 1)
        {
            return false;
        }

        _ = board.TurnPage(delta);
        Render();
        return true;
    }

    /// <summary>Lets go of the board. Standing on no deployment is not a fault, and not a board.</summary>
    /// <remarks>
    /// The third guaranteed exit. Standing nowhere is exactly where a word was most likely to be
    /// stuck: there is no board here to redraw over it and no frame coming to take it down.
    /// </remarks>
    public void Detach()
    {
        _board = null;
        if (_conditions.ClearAll())
        {
            RenderHeader();
        }
    }

    /// <summary>Claimed rows, for the presence heartbeat. Empty before a board exists.</summary>
    public IReadOnlyList<Guid> ClaimedRequestIds => _board is null
        ? []
        : [.. _board.All.Where(r => r.IsClaimedBy(_viewerId)).Select(r => r.Id)];

    // --- connection -----------------------------------------------------------------------------

    /// <summary>
    /// The connection moved. Anything the last connection told us stops being vouched for here.
    /// </summary>
    /// <remarks>
    /// One of the three guaranteed exits for a session-scoped condition. A word derived from a
    /// frame is only as good as the socket that delivered it; keeping it up across a drop states
    /// something the agent can no longer see. The next ready frame re-asserts whatever still holds.
    /// </remarks>
    public void OnConnectionStateChanged(RealtimeConnectionState state) => OnUi(() =>
    {
        if (state != RealtimeConnectionState.Connected && _conditions.ClearAll())
        {
            RenderHeader();
        }

        _onState(state);
    });

    /// <inheritdoc />
    public void OnReady(ReadyPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // An empty subscription set is the normal cold-start state, not a fault. The composition
        // root decides what to seed from; this only clears the words the last connection left.
        //
        // BoardStale only: it is derived from a drain age this connection has not measured yet.
        // AnotherDevice is re-asserted by the ready frame ITSELF, and this used to run after that
        // and null the whole field, so a reconnect onto a shared participant set the word and
        // erased it in the same breath.
        OnUi(() =>
        {
            _notice = null;
            _ = _conditions.Clear(HeaderCondition.BoardStale);
            RenderHeader();
        });
    }

    /// <summary>
    /// The event channel stalled while the socket stayed up. A different fault from the amber dot,
    /// and it gets the header's own word rather than the connection colour.
    /// </summary>
    public void OnBoardStalenessChanged(bool stale, double drainAgeSeconds) => OnUi(() =>
    {
        // Renewed: it is derived from a drain age the socket keeps measuring. A socket that stops
        // measuring cannot vouch for the word, so it comes down rather than standing on evidence
        // nobody is gathering any more.
        _ = stale
            ? _conditions.Raise(
                HeaderCondition.BoardStale,
                "BOARD MAY BE STALE",
                StatusSeverity.Warn,
                ConditionExit.Renewed,
                _serverNow())
            : _conditions.Clear(HeaderCondition.BoardStale);
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
        OnUi(() => SetFaultCore(FaultFor(payload), StatusSeverity.Fault));
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
        Remove(payload.RequestId);
    }

    /// <inheritdoc />
    public void OnRequestAbandoned(RequestAbandonedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Remove(payload.RequestId);
    }

    /// <inheritdoc />
    /// <remarks>A full row, because the board dropped it when it went terminal.</remarks>
    public void OnRequestReopened(RequestReopenedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Upsert(payload);
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
    /// <remarks>
    /// The copy names the reason. All three read "Switching deployment", so a replay-buffer miss
    /// on a board nobody had left said the match had changed, and a stand-down said it too.
    /// </remarks>
    public void OnBoardCleared(BoardClearReason reason) => OnUi(() =>
    {
        _board = null;

        // One of the three guaranteed exits. A word about the match being left must not follow the
        // viewer into the next one.
        //
        // Cleared AND redrawn. Dropping the state without repainting leaves the last strip on
        // screen with nothing behind it, which looks exactly like the bug this replaced.
        if (_conditions.ClearAll())
        {
            RenderHeader();
        }
        // A fresh hop is a fresh chance, so the backoff resets with it.
        _transientWait = TransientEmptyStateLimit;
        _transientUntil = _serverNow() + _transientWait;

        var (title, detail) = reason switch
        {
            BoardClearReason.DeploymentClosed => ("Deployment closed", "waiting for the next one"),
            BoardClearReason.ResyncRequired => ("Reconnected", "re-reading the board"),
            _ => ("Switching deployment", "re-reading the board"),
        };
        _presenter.ShowEmptyState(title, detail);
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
        SetFaultCore("SIGN IN AGAIN", StatusSeverity.Fault);
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
        // Session: it arrives on the ready frame and on nothing else, so there is no cadence to
        // expire against. The hop, the detach and the socket dropping take it down instead, and
        // the next ready frame re-asserts it.
        _ = present
            ? _conditions.Raise(
                HeaderCondition.AnotherDevice,
                "ANOTHER DEVICE ON BOARD",
                StatusSeverity.Warn,
                ConditionExit.Session,
                _serverNow())
            : _conditions.Clear(HeaderCondition.AnotherDevice);
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

        // ONE counter down the whole board. The number a person reads is the position, so it runs
        // 1, 2, 3 through the claimable rows and straight on into YOURS without a gap. It used to
        // be the allocation slot, which is stable for a row's life and therefore full of holes:
        // clear the row on 2 and the board read 1, 3, 4.
        // The page IS the numbering. Every line 1..9 comes off this one window, so the digit a row
        // draws, the digit the menu offers and the digit voice resolves cannot disagree, and a row
        // on page 2 is as pressable as one on page 1.
        var page = board.Lines;
        var line = page
            .Select((row, index) => (row.Id, Line: index + 1))
            .ToDictionary(x => x.Id, x => x.Line);

        var inYours = board.Yours.Select(r => r.Id).ToHashSet();

        var rows = page
            .Where(r => !inYours.Contains(r.Id))
            .Select(r => BoardRowViewModel
                .FromPrimary(r, _viewerId, now, unitsToMeters, fire, _catalog(), line[r.Id])
                .WithGlyph(glyphs))
            .ToList();

        // YOURS draws on every page: it is the work in this viewer's hands and it does not belong
        // to a window over the queue. It carries a digit only while that row is on the page shown,
        // because a digit naming a row nobody can see is a key that does the wrong thing.
        var yours = board.Yours
            .Select(r => BoardRowViewModel
                .FromPrimary(
                    r,
                    _viewerId,
                    now,
                    unitsToMeters,
                    fire,
                    _catalog(),
                    line.TryGetValue(r.Id, out var n) ? n : null,
                    RowSurface.Active)
                .WithGlyph(glyphs))
            .ToList();

        // Rows off this page, not rows without a digit. With paging the two stopped being the same
        // thing: on page 2 of 3 the count is what is still behind and ahead of the window.
        var offPage = board.LineCount - page.Count;
        var urgent = board.Overflow.Count(r => r.Priority == Priority.Urgent);

        _presenter.RenderBoard(
            rows,
            yours,
            Math.Max(0, offPage),
            urgent,
            board.InProgressCount,
            board.Page + 1,
            board.PageCount);

        // Built HERE, on the dispatcher, while nothing else is mutating the board. The menu used to
        // walk BoardState.Rows from the hook thread while socket frames were writing to it, which
        // is an InvalidOperationException that kills the hook and takes every hotkey with it.
        // EVERY row holding a digit, from both halves. Rows excludes anything that renders in
        // YOURS, and a job you have claimed is exactly that: it keeps its digit and moves down. So
        // the menu had no entry for the one row you actually have work to do on, and DONE could not
        // be pressed on the job you were doing.
        // Keyed by LINE, the same number the row just drew, so a digit pressed is the row read.
        var slots = new Dictionary<int, SlotState>();
        var pressed = 0;
        foreach (var row in page)
        {
            // IsClaimedBy, never the claimant column. A shared row you accepted leaves that column
            // null and stays Open, so the menu read it as somebody else's and offered you ACCEPT
            // on work you already had, with no DONE and no RELEASE anywhere.
            slots[++pressed] = new SlotState(
                row.State,
                row.IsClaimedBy(_viewerId),
                row.RequestedByParticipantId == _viewerId,
                row.Id);
        }

        _onRendered(new BoardSnapshot(
            board.Rows.Count + board.Yours.Count + board.Overflow.Count,
            rows.Count(r => r.Accent == RowAccent.Mine),
            slots,
            board.PageCount));
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
    public void SetFault(string? fault) => OnUi(() => SetFaultCore(fault, StatusSeverity.Fault));

    /// <summary>
    /// A confirmation of something the viewer just did. Same deadline, quieter colour.
    /// </summary>
    /// <remarks>
    /// COPIED and GUN CLEARED are not faults, and drawing them in the fault colour taught people
    /// to read the strip's loudest colour as noise.
    /// </remarks>
    public void SetNote(string note) => OnUi(() => SetFaultCore(note, StatusSeverity.Note));

    /// <summary>
    /// How long a notice stays on the header before it takes itself off.
    /// </summary>
    /// <remarks>
    /// Long enough to read a short line, short enough that it cannot be mistaken for the state of
    /// the system a minute later.
    /// </remarks>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a transient empty state has to turn into a board before it is disbelieved.
    /// </summary>
    /// <remarks>
    /// Long enough for an ordinary HTTPS re-read on a slow connection, short enough that nobody
    /// stares at a banner describing work that already failed. On expiry the word becomes honest
    /// and the read is asked for again, so the overlay recovers on its own even if every other
    /// path to a re-read is broken.
    /// </remarks>
    private static readonly TimeSpan TransientEmptyStateLimit = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The slowest the watchdog asks again once the reads keep failing.
    /// </summary>
    /// <remarks>
    /// The FIRST escalation is always at <see cref="TransientEmptyStateLimit"/>, because that is
    /// the promise: a banner naming work in flight becomes the truth within twelve seconds. Only
    /// the retry cadence backs off after that. Re-reading the whole config every twelve seconds
    /// against an API that is refusing is how a rate limit becomes permanent, since the read that
    /// would clear it is the read keeping it exhausted and each one costs a request per group.
    /// </remarks>
    private static readonly TimeSpan TransientEmptyStateCeiling = TimeSpan.FromMinutes(2);

    private void SetFaultCore(string? fault, StatusSeverity severity)
    {
        _notice = fault;
        _noticeSeverity = severity;
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
        // The sweep runs FIRST and unconditionally. A renewed condition whose source went quiet
        // comes down on the same tick that expires a notice, and neither depends on there being a
        // board to redraw: standing nowhere is where a word gets stuck.
        var swept = _conditions.Sweep(now);

        if (_notice is not null && now >= _noticeUntil)
        {
            _notice = null;
            swept = true;
        }

        if (swept)
        {
            RenderHeader();
        }

        DisbelieveAStuckBanner(now);
    });

    /// <summary>
    /// Replaces a transient empty state that never became a board, and asks for the read again.
    /// </summary>
    /// <remarks>
    /// The backstop for every way a re-read can fail to land: a frame that never arrived, a reload
    /// that threw, a revalidation that died on a pool thread. Whatever the cause, the overlay says
    /// something true within <see cref="TransientEmptyStateLimit"/> and keeps trying, rather than
    /// holding a banner about work that stopped.
    /// </remarks>
    private void DisbelieveAStuckBanner(DateTimeOffset now)
    {
        if (_transientUntil is not { } until || now < until)
        {
            return;
        }

        if (_board is not null)
        {
            _transientUntil = null;
            _transientWait = TransientEmptyStateLimit;
            return;
        }

        // Re-armed rather than fired once, because the word says "retrying" and that has to be
        // true. Backed off while it keeps failing: the banner is already honest by now, so slowing
        // down costs the reader nothing and stops the retry from holding the limit open.
        var doubled = _transientWait + _transientWait;
        _transientWait = doubled > TransientEmptyStateCeiling ? TransientEmptyStateCeiling : doubled;
        _transientUntil = now + _transientWait;
        _presenter.ShowEmptyState("Board unreachable", "retrying");
        _onConfigChanged();
    }

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
    /// <summary>
    /// How many status items the strip draws before the rest become a count.
    /// </summary>
    /// <remarks>
    /// Three fits the panel's width at SmallSize without wrapping, and a strip that wraps pushes
    /// every row of the board down by a line.
    /// </remarks>
    private const int StatusShown = 3;

    /// <summary>
    /// Redraws the header and the status strip. Every path that changes either goes through here.
    /// </summary>
    /// <remarks>
    /// The header's right cell is the JOIN CODE and nothing else now. Status used to be written
    /// into it, so a word nobody took down covered the six digits somebody was about to read out
    /// loud, and a second status silently replaced the first instead of appearing beside it.
    /// </remarks>
    private void RenderHeader()
    {
        _presenter.SetHeader(_header);
        var (shown, hidden) = StatusNow();
        _presenter.RenderStatus(shown, hidden);
    }

    /// <summary>
    /// Everything standing, worst first: the transient notice, then the keyed conditions.
    /// </summary>
    /// <remarks>
    /// Built fresh on every render rather than mutated, so the strip cannot hold an item whose
    /// source has gone: what is not still true this instant is not drawn this instant.
    /// </remarks>
    private (IReadOnlyList<StatusItem> Shown, int Hidden) StatusNow()
    {
        var items = new List<StatusItem>();

        // The notice first. It is the answer to something the viewer just did, so it is the one
        // they are looking for, and it takes itself off on a deadline.
        if (_notice is { } notice)
        {
            items.Add(new StatusItem(notice, _noticeSeverity));
        }

        items.AddRange(_conditions.Items());

        List<StatusItem> shown =
        [
            .. items
                .OrderBy(item => item.Severity)
                .Take(StatusShown)
                .Select((item, index) => item with { IsFirst = index == 0 }),
        ];

        return (shown, items.Count - shown.Count);
    }

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
    IReadOnlyDictionary<int, SlotState> Slots,
    int Pages = 1);
