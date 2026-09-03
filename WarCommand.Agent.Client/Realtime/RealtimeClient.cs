using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Client.Realtime;

/// <summary>
/// The agent's realtime socket: ticket over HTTPS, connect, wait for <c>ready</c>, then frames.
/// </summary>
/// <remarks>
/// Three rules this type exists to hold. The clock offset is re-derived from every
/// <c>ready</c> and not only the first, because a reconnect is exactly when a laptop's clock has
/// drifted. A <c>presence</c> frame reports and never releases: divergence is answered by the
/// server with <c>claims.reconcile</c> and the database wins. And the client never names a topic:
/// subscriptions arrive in <c>ready</c> and <c>subscriptions.changed</c>, and an empty set is the
/// normal cold-start state of every new install.
/// </remarks>
public sealed class RealtimeClient : IAsyncDisposable
{
    private readonly Uri _url;
    private readonly IRealtimeTicketSource _tickets;
    private readonly IWebSocketChannelFactory _channels;
    private readonly IRealtimeObserver _observer;
    private readonly IPresenceSource _presence;
    private readonly IBoardRevalidator _revalidator;
    private readonly ISystemClockOffset _clockOffset;
    private readonly IClock _clock;
    private readonly ReconnectPolicy _reconnect;
    private readonly IAsyncDelay _delay;
    private readonly IClientLog _log;
    private readonly RealtimeClientOptions _options;

    private ChannelWriter<string>? _outbound;
    private CancellationToken _runToken = CancellationToken.None;
    private IReadOnlyList<SubscriptionEntry> _subscriptions = [];
    private string? _sessionId;
    private long _lastSeq;
    private int _protocolViolations;
    private int _connectionAttempt;
    private bool _sawReady;
    private bool _stopped;
    private double _staleThresholdSeconds;

    public RealtimeClient(
        Uri realtimeUrl,
        IRealtimeTicketSource tickets,
        IWebSocketChannelFactory channels,
        IRealtimeObserver observer,
        IPresenceSource presence,
        IBoardRevalidator revalidator,
        ISystemClockOffset clockOffset,
        IClock? clock = null,
        ReconnectPolicy? reconnect = null,
        IAsyncDelay? delay = null,
        IClientLog? log = null,
        RealtimeClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(realtimeUrl);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(revalidator);
        ArgumentNullException.ThrowIfNull(clockOffset);

        _url = TransportSecurity.RequireSecureWebSocket(realtimeUrl, nameof(realtimeUrl));
        _tickets = tickets;
        _channels = channels;
        _observer = observer;
        _presence = presence;
        _revalidator = revalidator;
        _clockOffset = clockOffset;
        _clock = clock ?? SystemClock.Instance;
        _options = options ?? new RealtimeClientOptions();
        _reconnect = reconnect ?? new ReconnectPolicy();
        _delay = delay ?? SystemDelay.Instance;
        _log = log ?? NullClientLog.Instance;
        _staleThresholdSeconds = _options.OutboxDrainStaleSeconds;
    }

    /// <summary>The socket's health. This is the amber dot and nothing else.</summary>
    public RealtimeConnectionState State { get; private set; } = RealtimeConnectionState.Idle;

    /// <summary>
    /// The event channel stalled while the socket stayed up. A different fault from
    /// <see cref="State"/>, and the reason the drain age rides the heartbeat.
    /// </summary>
    public bool BoardMayBeStale { get; private set; }

    /// <summary>True while another live session holds the same participant.</summary>
    public bool AnotherDeviceOnBoard { get; private set; }

    /// <summary>Server-derived. Empty is the normal cold-start state, not a fault.</summary>
    public IReadOnlyList<SubscriptionEntry> Subscriptions => _subscriptions;

    /// <summary>Connections opened so far. A test asserts 4003 leaves this at one.</summary>
    public int ConnectAttempts { get; private set; }

    /// <summary>The last scheduled HTTPS revalidation, so a host or a test can await it.</summary>
    public Task RevalidationInFlight { get; private set; } = Task.CompletedTask;

    /// <summary>Frames dropped because the per-connection send buffer was full.</summary>
    public int SendBufferDrops { get; private set; }

    /// <summary>Every deployment the current subscription set names.</summary>
    public IReadOnlyList<Guid> CurrentDeploymentIds =>
        [.. _subscriptions.Where(s => s.Deployment is not null).Select(s => s.Deployment!.Id)];

    /// <summary>
    /// Connects and keeps connecting until cancelled, or until a close code says to stop. Backoff
    /// is full jitter, base 500 ms, cap 30 s.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _runToken = cancellationToken;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested && !_stopped)
        {
            SetState(RealtimeConnectionState.Connecting);
            int? closeCode = null;
            _sawReady = false;

            try
            {
                var ticket = await _tickets.AcquireRealtimeTicketAsync(cancellationToken).ConfigureAwait(false);
                ConnectAttempts++;
                using var channel = _channels.Create();
                await channel.ConnectAsync(TicketUrl(ticket.Ticket), cancellationToken).ConfigureAwait(false);
                _connectionAttempt = attempt;
                await PumpAsync(channel, cancellationToken).ConfigureAwait(false);
                closeCode = channel.CloseCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WarCommandApiException ex)
            {
                if (ex.Code is ErrorCodes.DeviceRevoked)
                {
                    Revoked();
                    break;
                }

                _log.Warn($"Realtime ticket failed: {ex.Code}.");
            }
            catch (WebSocketException ex)
            {
                _log.Warn($"Realtime socket dropped: {ex.WebSocketErrorCode}.");
            }
            catch (IOException ex)
            {
                _log.Warn($"Realtime socket dropped: {ex.Message}");
            }

            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!ApplyCloseCode(closeCode))
            {
                break;
            }

            attempt = _sawReady ? 0 : attempt + 1;
            SetState(RealtimeConnectionState.Reconnecting);

            try
            {
                await _delay.WaitAsync(_reconnect.NextSocketDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(RealtimeConnectionState.Stopped);
    }

    /// <summary>Stops reconnecting. Panic and shutdown both use it.</summary>
    public void Stop(string reason)
    {
        _stopped = true;
        _log.Info($"Realtime stopped: {reason}");
    }

    // -----------------------------------------------------------------------
    // Client frames
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rate limited per connection on top of the concurrent-claim cap. Returns false when the
    /// socket is down, and the claim is NOT queued: a claim replayed later takes a mission somebody
    /// else has already handled.
    /// </summary>
    public bool Claim(Guid requestId, int version) =>
        Send(FrameTypes.RequestClaim, new RequestClaimCommand { RequestId = requestId, Version = version });

    public bool Start(Guid requestId, int version) =>
        Send(FrameTypes.RequestStart, new RequestStartCommand { RequestId = requestId, Version = version });

    /// <summary>Non-terminal. The server performs the optional start first if the row is still claimed.</summary>
    public bool RoundsAway(Guid requestId, int version) =>
        Send(FrameTypes.RequestRoundsAwayCommand, new RequestRoundsAwayCommand { RequestId = requestId, Version = version });

    public bool Adjust(Guid requestId, AdjustDirection direction, int? metres, int version) =>
        Send(FrameTypes.RequestAdjust, new RequestAdjustCommand
        {
            RequestId = requestId,
            Direction = direction,
            Metres = metres,
            Version = version,
        });

    public bool Complete(Guid requestId, Outcome outcome, int? quantityDelivered, string? reason, int version) =>
        Send(FrameTypes.RequestComplete, new RequestCompleteCommand
        {
            RequestId = requestId,
            Outcome = outcome,
            QuantityDelivered = quantityDelivered,
            Reason = reason,
            Version = version,
        });

    public bool Release(Guid requestId, int version) =>
        Send(FrameTypes.RequestRelease, new RequestReleaseCommand { RequestId = requestId, Version = version });

    public bool Cancel(Guid requestId, int version) =>
        Send(FrameTypes.RequestCancel, new RequestCancelCommand { RequestId = requestId, Version = version });

    /// <summary>The override. Pins the member until their reported server key changes.</summary>
    public bool EnterDeployment(Guid deploymentId) =>
        Send(FrameTypes.DeploymentEnter, new DeploymentEnterCommand { DeploymentId = deploymentId });

    /// <summary>Six digits. Valid from a connection with no memberships at all.</summary>
    public bool JoinDeployment(string inviteCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        return Send(FrameTypes.DeploymentJoin, new DeploymentJoinCommand { InviteCode = inviteCode });
    }

    /// <summary>Sends the heartbeat now, off the timer. It still releases nothing.</summary>
    public bool SendPresence() => Send(FrameTypes.Presence, new PresenceCommand
    {
        ClaimedRequestIds = _presence.ClaimedRequestIds,
        State = _presence.State,
        ServerKey = _presence.ServerKey,
    });

    public ValueTask DisposeAsync()
    {
        Stop("disposed");
        _outbound?.TryComplete();
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Connection
    // -----------------------------------------------------------------------

    private Uri TicketUrl(string ticket)
    {
        var separator = string.IsNullOrEmpty(_url.Query) ? "?" : "&";
        return new Uri($"{_url}{separator}ticket={Uri.EscapeDataString(ticket)}");
    }

    private async Task PumpAsync(IWebSocketChannel channel, CancellationToken cancellationToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.SendBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        _outbound = outbound.Writer;

        // Resume before ready: the server replays from last_seq or answers resync_required.
        if (_sessionId is { } session)
        {
            Send(FrameTypes.Resume, new ResumeCommand { SessionId = session, LastSeq = _lastSeq });
        }

        // Both loops start off this stack, so neither can starve the receive loop.
        var sender = Task.Run(() => SendLoopAsync(channel, outbound.Reader, connection.Token), CancellationToken.None);
        var heartbeat = Task.Run(() => PresenceLoopAsync(connection.Token), CancellationToken.None);

        try
        {
            while (!connection.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(connection.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection is going down; the close code decides what happens next.
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            outbound.Writer.TryComplete();
            _outbound = null;
            await Quiet(sender).ConfigureAwait(false);
            await Quiet(heartbeat).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(IWebSocketChannel channel, ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            _log.Warn($"Realtime send failed: {ex.WebSocketErrorCode}.");
        }
    }

    /// <summary>
    /// Reports presence immediately, then on every interval.
    /// </summary>
    /// <remarks>
    /// The FIRST send has to happen on connect, not one interval later. The stale-claim sweeper
    /// treats a claim whose device has no device_presence row as a dead device and releases it with
    /// no grace at all, so a claim made inside the first interval was taken and then dumped back
    /// into the pool a few seconds later, looking to everyone like the claim had simply failed.
    /// </remarks>
    private async Task PresenceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            SendPresence();

            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay.WaitAsync(_options.PresenceInterval, cancellationToken).ConfigureAwait(false);
                SendPresence();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Send(string type, object payload)
    {
        var writer = _outbound;
        if (writer is null)
        {
            return false;
        }

        var frame = FrameCodec.Write(Guid.NewGuid().ToString("N"), type, _clock.UtcNow, payload);
        if (writer.TryWrite(frame))
        {
            return true;
        }

        SendBufferDrops++;
        _log.Warn($"Send buffer full at {_options.SendBufferSize}; dropped a {type} frame.");
        return false;
    }

    private bool ApplyCloseCode(int? code)
    {
        switch (code)
        {
            case 4003:
                Revoked();
                return false;

            case 4004:
                _protocolViolations++;
                if (_protocolViolations > _options.MaxProtocolViolations)
                {
                    _log.Error("Realtime protocol violation twice. Not reconnecting again.");
                    _stopped = true;
                    return false;
                }

                _log.Warn("Realtime protocol violation. Reconnecting once.");
                return true;

            case 4008:
                // Slow consumer. Reconnect and resync rather than resume into a buffer we outran.
                _log.Warn("Realtime dropped as a slow consumer. Resyncing.");
                _sessionId = null;
                _lastSeq = 0;
                return true;

            case 4001:
            case 4002:
                _log.Info("Realtime credentials expired. The next ticket refreshes them.");
                return true;

            default:
                return true;
        }
    }

    private void Revoked()
    {
        _stopped = true;
        _log.Error("This device was revoked. Not retrying: an agent hammering after revocation is an attack.");
        _observer.OnDeviceRevoked();
        SetState(RealtimeConnectionState.Stopped);
    }

    private void SetState(RealtimeConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        _observer.OnConnectionStateChanged(state);
    }

    // -----------------------------------------------------------------------
    // Dispatch
    // -----------------------------------------------------------------------

    /// <summary>
    /// One frame. A payload this build cannot read is discarded, never fatal.
    /// </summary>
    /// <remarks>
    /// FrameCodec.TryRead only guards the envelope. PayloadAs deserializes lazily, so a payload
    /// whose shape or format this build does not accept threw from here, out of the receive pump,
    /// out of the connect loop, and killed the socket for the rest of the session. It happened on
    /// the very first frame the agent ever received: the server rendered ready.server_time with a
    /// space instead of an ISO T, and one JsonException took the whole socket down permanently.
    /// A frame is not worth a connection.
    /// </remarks>
    private void Dispatch(string message)
    {
        try
        {
            DispatchCore(message);
        }
        catch (JsonException ex)
        {
            _log.Warn($"Discarded a realtime frame this build could not read: {ex.Message}");
        }
    }

    private void DispatchCore(string message)
    {
        var envelope = FrameCodec.TryRead(message);
        if (envelope is null)
        {
            _log.Warn("Discarded an unreadable realtime frame.");
            return;
        }

        if (envelope.Seq is { } seq && seq > _lastSeq)
        {
            _lastSeq = seq;
        }

        switch (envelope.Type)
        {
            case FrameTypes.Ready:
                OnReady(envelope);
                break;

            case FrameTypes.Ping:
                Send(FrameTypes.Pong, new PongPayload());
                if (envelope.PayloadAs<PingPayload>()?.OutboxDrainAgeS is { } age)
                {
                    UpdateStaleness(age);
                }

                break;

            case FrameTypes.StreamDegraded:
                if (envelope.PayloadAs<StreamDegradedPayload>() is { } degraded)
                {
                    if (degraded.ThresholdS is { } threshold)
                    {
                        _staleThresholdSeconds = threshold;
                    }

                    UpdateStaleness(degraded.DrainAgeS);
                }

                break;

            case FrameTypes.RequestSubmitted:
                Deliver<RequestSubmittedPayload>(envelope, _observer.OnRequestSubmitted);
                break;

            case FrameTypes.RequestClaimed:
                Deliver<RequestClaimedPayload>(envelope, _observer.OnRequestClaimed);
                break;

            case FrameTypes.RequestStarted:
                Deliver<RequestStartedPayload>(envelope, _observer.OnRequestStarted);
                break;

            case FrameTypes.RequestRoundsAway:
                Deliver<RequestRoundsAwayPayload>(envelope, _observer.OnRequestRoundsAway);
                break;

            case FrameTypes.RequestAdjusted:
                Deliver<RequestAdjustedPayload>(envelope, _observer.OnRequestAdjusted);
                break;

            case FrameTypes.RequestCompleted:
                // The row on an unable completion arrives FLAT, beside request_id, because the
                // server does payload.update(request_body(...)). Read the same payload object a
                // second time as a row so the reopened request can be put back.
                Deliver<RequestCompletedPayload>(
                    envelope,
                    payload => _observer.OnRequestCompleted(
                        payload.Request is null && payload.ReturnsToOpen
                            ? payload with { Flat = envelope.PayloadAs<RequestBody>() }
                            : payload));
                break;

            case FrameTypes.RequestReleased:
                Deliver<RequestReleasedPayload>(envelope, _observer.OnRequestReleased);
                break;

            case FrameTypes.RequestSuperseded:
                Deliver<RequestSupersededPayload>(envelope, _observer.OnRequestSuperseded);
                break;

            case FrameTypes.RequestEscalated:
                Deliver<RequestEscalatedPayload>(envelope, _observer.OnRequestEscalated);
                break;

            case FrameTypes.RequestCancelled:
                Deliver<RequestCancelledPayload>(envelope, _observer.OnRequestCancelled);
                break;

            case FrameTypes.RequestExpired:
                Deliver<RequestExpiredPayload>(envelope, _observer.OnRequestExpired);
                break;

            case FrameTypes.ClaimsReconcile:
                Deliver<ClaimsReconcilePayload>(envelope, _observer.OnClaimsReconcile);
                break;

            case FrameTypes.SubscriptionsChanged:
                OnSubscriptionsChanged(envelope);
                break;

            case FrameTypes.ConfigChanged:
                Deliver<ConfigChangedPayload>(envelope, _observer.OnConfigChanged);
                break;

            case FrameTypes.DeploymentEntered:
                OnDeploymentEntered(envelope);
                break;

            case FrameTypes.DeploymentRoster:
                Deliver<DeploymentRosterPayload>(envelope, _observer.OnDeploymentRoster);
                break;

            case FrameTypes.DeploymentClosed:
                OnDeploymentClosed(envelope);
                break;

            case FrameTypes.MembershipEnded:
                Deliver<MembershipEndedPayload>(envelope, _observer.OnMembershipEnded);
                break;

            case FrameTypes.IdentityAccountLinked:
                Deliver<IdentityAccountLinkedPayload>(envelope, _observer.OnIdentityAccountLinked);
                break;

            case FrameTypes.GroupFrozen:
                Deliver<GroupFrozenPayload>(envelope, p => _observer.OnGroupFrozen(p, frozen: true));
                break;

            case FrameTypes.GroupUnfrozen:
                Deliver<GroupFrozenPayload>(envelope, p => _observer.OnGroupFrozen(p, frozen: false));
                break;

            case FrameTypes.ResyncRequired:
                OnResyncRequired(envelope);
                break;

            case FrameTypes.Error:
                Deliver<ErrorPayload>(envelope, _observer.OnErrorFrame);
                break;

            default:
                _observer.OnUnknownFrame(envelope);
                break;
        }
    }

    private void Deliver<T>(Envelope envelope, Action<T> handler)
        where T : class
    {
        var payload = envelope.PayloadAs<T>();
        if (payload is null)
        {
            _log.Warn($"Frame {envelope.Type} carried no readable payload.");
            return;
        }

        handler(payload);
    }

    private void OnReady(Envelope envelope)
    {
        var payload = envelope.PayloadAs<ReadyPayload>();
        if (payload is null)
        {
            _log.Warn("A ready frame carried no readable payload.");
            return;
        }

        // Re-derived on EVERY ready, including after every reconnect.
        _clockOffset.DeriveFrom(payload.ServerTime, _clock.UtcNow);

        if (!string.Equals(_sessionId, payload.SessionId, StringComparison.Ordinal))
        {
            _lastSeq = envelope.Seq ?? 0;
        }

        _sessionId = payload.SessionId;
        _sawReady = true;
        _subscriptions = payload.Subscriptions;
        _protocolViolations = 0;

        SetState(RealtimeConnectionState.Connected);

        if (AnotherDeviceOnBoard != payload.AnotherDeviceOnBoard)
        {
            AnotherDeviceOnBoard = payload.AnotherDeviceOnBoard;
            _observer.OnAnotherDeviceOnBoard(payload.AnotherDeviceOnBoard);
        }

        _observer.OnReady(payload);

        // On every reconnect, and on the first connect, re-seed over HTTPS using the deployment id
        // the server just sent rather than the one we remembered.
        ScheduleRevalidation(CurrentDeploymentIds);
    }

    private void OnSubscriptionsChanged(Envelope envelope)
    {
        var payload = envelope.PayloadAs<SubscriptionsChangedPayload>();
        if (payload is null)
        {
            return;
        }

        _subscriptions = payload.Subscriptions;
        _observer.OnSubscriptionsChanged(payload);
        ScheduleRevalidation(CurrentDeploymentIds);
    }

    private void OnDeploymentEntered(Envelope envelope)
    {
        var payload = envelope.PayloadAs<DeploymentEnteredPayload>();
        if (payload is null)
        {
            return;
        }

        // Order is the specification. Step 0 first, because a draft aborted after the board is
        // cleared has already been committed.
        _observer.OnPendingDraftAborted(DraftAbortReason.DeploymentChanged);
        _observer.OnBoardCleared(BoardClearReason.DeploymentEntered);
        _observer.OnDeploymentEntered(payload);

        _subscriptions =
        [
            .. _subscriptions.Select(s => s.GroupId == payload.GroupId
                ? s with
                {
                    Deployment = new DeploymentRef
                    {
                        Id = payload.DeploymentId,
                        Label = payload.Label,
                        MemberCount = payload.MemberCount,
                        InviteCode = s.Deployment?.Id == payload.DeploymentId ? s.Deployment.InviteCode : null,
                    },
                }
                : s),
        ];

        ScheduleRevalidation([payload.DeploymentId]);
    }

    private void OnDeploymentClosed(Envelope envelope)
    {
        var payload = envelope.PayloadAs<DeploymentClosedPayload>();
        if (payload is null)
        {
            return;
        }

        // One frame for the whole stand-down. The individual request.cancelled frames are
        // deliberately suppressed server-side, so nothing else is coming.
        _observer.OnPendingDraftAborted(DraftAbortReason.DeploymentClosed);
        _observer.OnBoardCleared(BoardClearReason.DeploymentClosed);
        _observer.OnDeploymentClosed(payload);
    }

    private void OnResyncRequired(Envelope envelope)
    {
        var payload = envelope.PayloadAs<ResyncRequiredPayload>() ?? new ResyncRequiredPayload();

        _sessionId = null;
        _lastSeq = 0;
        _observer.OnBoardCleared(BoardClearReason.ResyncRequired);
        _observer.OnResyncRequired(payload);
        ScheduleRevalidation(CurrentDeploymentIds);
    }

    private void UpdateStaleness(double drainAgeSeconds)
    {
        var stale = drainAgeSeconds >= _staleThresholdSeconds;
        if (stale == BoardMayBeStale)
        {
            return;
        }

        BoardMayBeStale = stale;
        _log.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Outbox drain age {drainAgeSeconds:F1}s against a {_staleThresholdSeconds:F0}s threshold."));
        _observer.OnBoardStalenessChanged(stale, drainAgeSeconds);
    }

    /// <summary>
    /// Delays the HTTPS re-seed by its own full-jitter draw. Sharing the socket's draw would spread
    /// the fleet's reconnects and then land all of it on Postgres at the same instant.
    /// </summary>
    private void ScheduleRevalidation(IReadOnlyList<Guid> deploymentIds)
    {
        if (deploymentIds.Count == 0)
        {
            // No deployment. The board stays empty, and that is the cold-start state, not a fault.
            return;
        }

        var wait = _reconnect.NextRevalidateDelay(_connectionAttempt);
        var token = _runToken;

        RevalidationInFlight = Task.Run(
            async () =>
            {
                try
                {
                    await _delay.WaitAsync(wait, token).ConfigureAwait(false);
                    foreach (var id in deploymentIds)
                    {
                        await _revalidator.RevalidateAsync(id, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (WarCommandApiException ex)
                {
                    _log.Warn($"Board revalidation failed: {ex.Code}.");
                }
            },
            CancellationToken.None);
    }

    private sealed record PongPayload;
}
