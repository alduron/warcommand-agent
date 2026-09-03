using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Realtime;

/// <summary>Why the board was told to clear.</summary>
public enum BoardClearReason
{
    /// <summary>The hop. Nothing from the old match is coming back to reclaim a digit.</summary>
    DeploymentEntered,

    /// <summary>The match ended. One frame, not N cancels.</summary>
    DeploymentClosed,

    /// <summary>The replay buffer missed. Drop local state and re-seed over HTTPS.</summary>
    ResyncRequired,
}

/// <summary>Why a half-captured request was thrown away.</summary>
public enum DraftAbortReason
{
    /// <summary>A second point tapped after the hop would carry the old server's map.</summary>
    DeploymentChanged,

    /// <summary>The match ended under a draft.</summary>
    DeploymentClosed,
}

/// <summary>The socket's health, which is the amber dot. Board staleness is a separate signal.</summary>
public enum RealtimeConnectionState
{
    /// <summary>Never connected yet.</summary>
    Idle,

    Connecting,

    /// <summary>Ready has landed.</summary>
    Connected,

    /// <summary>Backing off. The overlay shows the amber dot.</summary>
    Reconnecting,

    /// <summary>4003, or the host stopped it. Nothing will retry.</summary>
    Stopped,
}

/// <summary>
/// Everything the socket hands the composition root. Every member has a no-op default, so a host
/// implements only what it renders and a new frame type does not break the build.
/// </summary>
/// <remarks>
/// The four row-returning frames - <c>request.released</c>, <c>request.completed</c> with outcome
/// <c>unable</c>, <c>request.escalated</c> and <c>claims.reconcile</c> - all deliver a whole
/// <see cref="RequestBody"/>. Treat every one as an upsert: the agent may never assume it holds
/// prior state for a request id, because <c>request.claimed</c> made every non-claimant drop it.
/// </remarks>
public interface IRealtimeObserver
{
    /// <summary>
    /// Always seq 0. The clock offset has already been re-derived from
    /// <see cref="ReadyPayload.ServerTime"/> before this is called. An empty subscription set is
    /// the normal cold-start state: no error, no retry loop, no nag.
    /// </summary>
    void OnReady(ReadyPayload payload)
    {
    }

    void OnConnectionStateChanged(RealtimeConnectionState state)
    {
    }

    /// <summary>
    /// The event channel stalled while the socket stayed up. A different fault from the amber dot:
    /// past the threshold the overlay carries a persistent BOARD MAY BE STALE.
    /// </summary>
    void OnBoardStalenessChanged(bool stale, double drainAgeSeconds)
    {
    }

    /// <summary>Another live session holds the same participant, so the digits differ per device.</summary>
    void OnAnotherDeviceOnBoard(bool present)
    {
    }

    void OnRequestSubmitted(RequestSubmittedPayload payload)
    {
    }

    /// <summary>Every non-claimant drops the row. This is the fastest slot-release path in the design.</summary>
    void OnRequestClaimed(RequestClaimedPayload payload)
    {
    }

    void OnRequestStarted(RequestStartedPayload payload)
    {
    }

    void OnRequestRoundsAway(RequestRoundsAwayPayload payload)
    {
    }

    void OnRequestAdjusted(RequestAdjustedPayload payload)
    {
    }

    /// <summary>On outcome unable the row returns to open and the payload carries the whole body.</summary>
    void OnRequestCompleted(RequestCompletedPayload payload)
    {
    }

    /// <summary>A full row. Upsert it and reacquire a slot; it is never a delta.</summary>
    void OnRequestReleased(RequestReleasedPayload payload)
    {
    }

    void OnRequestSuperseded(RequestSupersededPayload payload)
    {
    }

    /// <summary>A full row: at level deployment the recipients never had it.</summary>
    void OnRequestEscalated(RequestEscalatedPayload payload)
    {
    }

    void OnRequestCancelled(RequestCancelledPayload payload)
    {
    }

    void OnRequestExpired(RequestExpiredPayload payload)
    {
    }

    /// <summary>
    /// The database's answer to a divergent presence. Restore exactly these rows, reacquire their
    /// slots, and drop any held claim that is not in the set. Never a release.
    /// </summary>
    void OnClaimsReconcile(ClaimsReconcilePayload payload)
    {
    }

    /// <summary>Server-derived. The client never names a topic and never asks to subscribe.</summary>
    void OnSubscriptionsChanged(SubscriptionsChangedPayload payload)
    {
    }

    /// <summary>Refetch the config payload over HTTPS. The frame carries no config.</summary>
    void OnConfigChanged(ConfigChangedPayload payload)
    {
    }

    /// <summary>Step 0 of the hop, and it happens before the board is cleared.</summary>
    void OnPendingDraftAborted(DraftAbortReason reason)
    {
    }

    /// <summary>Drop every row and reset the slot allocator, including its reissue order.</summary>
    void OnBoardCleared(BoardClearReason reason)
    {
    }

    /// <summary>
    /// Called after the draft was aborted and the board cleared. Revalidation follows, with the
    /// deployment id from this frame and never a remembered one.
    /// </summary>
    void OnDeploymentEntered(DeploymentEnteredPayload payload)
    {
    }

    /// <summary>Header only. Carries the invite code, re-emitted whenever it rotates.</summary>
    void OnDeploymentRoster(DeploymentRosterPayload payload)
    {
    }

    /// <summary>The whole stand-down. The individual cancels are deliberately not sent.</summary>
    void OnDeploymentClosed(DeploymentClosedPayload payload)
    {
    }

    /// <summary>Stop rendering that group and say why. The socket stays up.</summary>
    void OnMembershipEnded(MembershipEndedPayload payload)
    {
    }

    void OnIdentityAccountLinked(IdentityAccountLinkedPayload payload)
    {
    }

    void OnGroupFrozen(GroupFrozenPayload payload, bool frozen)
    {
    }

    void OnResyncRequired(ResyncRequiredPayload payload)
    {
    }

    /// <summary>Correlated to a client frame id. rate_limited and too_many_claims arrive here.</summary>
    void OnErrorFrame(ErrorPayload payload)
    {
    }

    /// <summary>The device was revoked with close 4003. Nothing retries; the tray says so.</summary>
    void OnDeviceRevoked()
    {
    }

    /// <summary>
    /// The realtime ticket was refused for a reason retrying cannot fix, and the credentials are
    /// the reason. The composition root re-registers; the socket keeps backing off meanwhile.
    /// </summary>
    /// <remarks>
    /// The clear-and-re-register recovery existed only at startup. A device whose registration went
    /// away mid-session backed off forever with an amber dot and no explanation at all.
    /// </remarks>
    void OnCredentialsRejected(string code)
    {
    }

    /// <summary>An unknown type is ignored, never fatal: additive changes stay non-breaking.</summary>
    void OnUnknownFrame(Envelope envelope)
    {
    }
}
