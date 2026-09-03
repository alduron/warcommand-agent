using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>
/// Every frame type on the realtime socket. One vocabulary for the whole system; an unknown type is
/// ignored rather than treated as an error.
/// </summary>
public static class FrameTypes
{
    // Server to client, connection protocol.
    public const string Ready = "ready";
    public const string Ping = "ping";
    public const string SubscriptionsChanged = "subscriptions.changed";
    public const string ConfigChanged = "config.changed";
    public const string ResyncRequired = "resync_required";
    public const string ClaimsReconcile = "claims.reconcile";
    public const string StreamDegraded = "stream.degraded";
    public const string Error = "error";

    // Server to client, domain.
    public const string RequestSubmitted = "request.submitted";
    public const string RequestClaimed = "request.claimed";
    public const string RequestStarted = "request.started";
    public const string RequestRoundsAway = "request.rounds_away";
    public const string RequestAdjusted = "request.adjusted";
    public const string RequestCompleted = "request.completed";
    public const string RequestReleased = "request.released";
    public const string RequestSuperseded = "request.superseded";
    public const string RequestEscalated = "request.escalated";
    public const string RequestCancelled = "request.cancelled";
    public const string RequestExpired = "request.expired";
    public const string DeploymentEntered = "deployment.entered";
    public const string DeploymentRoster = "deployment.roster";
    public const string DeploymentClosed = "deployment.closed";
    public const string MembershipEnded = "membership.ended";
    public const string IdentityAccountLinked = "identity.account.linked";
    public const string GroupFrozen = "group.frozen";
    public const string GroupUnfrozen = "group.unfrozen";

    // Client to server.
    public const string Pong = "pong";
    public const string Presence = "presence";
    public const string DeploymentEnter = "deployment.enter";
    public const string DeploymentJoin = "deployment.join";
    public const string Resume = "resume";
    public const string RequestClaim = "request.claim";
    public const string RequestRoundsAwayCommand = "request.rounds_away";
    public const string RequestAdjust = "request.adjust";
    public const string RequestComplete = "request.complete";
    public const string RequestRelease = "request.release";
    public const string RequestCancel = "request.cancel";

    /// <summary>
    /// Frames that put a row back on a board. Every one carries a full request body, so a consumer
    /// that treats one as a delta cannot render it: after request.claimed nobody holds the row.
    /// </summary>
    public static IReadOnlyList<string> RowReturning { get; } =
    [
        RequestReleased,
        RequestCompleted,
        RequestEscalated,
        ClaimsReconcile,
    ];
}

/// <summary>
/// The envelope every frame travels in, both directions and both transports.
/// </summary>
public sealed record Envelope
{
    /// <summary>Server-generated inbound; client-generated outbound, so the server can correlate.</summary>
    public required string Id { get; init; }

    /// <summary>A value from <see cref="FrameTypes"/>. Unknown types are ignored, never fatal.</summary>
    public required string Type { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset? SentAt { get; init; }

    /// <summary>Per-connection monotonic counter. Absent on client frames; always 0 on ready.</summary>
    public long? Seq { get; init; }

    public JsonElement Payload { get; init; }

    /// <summary>Reads the payload with <see cref="AgentJson.Options"/>.</summary>
    public T? PayloadAs<T>() =>
        Payload.ValueKind == JsonValueKind.Undefined ? default : Payload.Deserialize<T>(AgentJson.Options);
}

/// <summary>One point on the wire. Coordinates are strings, never floats, for the same reason money is.</summary>
public sealed record PointBody
{
    public required int Ordinal { get; init; }

    public required string Label { get; init; }

    public required string X { get; init; }

    public required string Y { get; init; }

    /// <summary>The winning coordinate source's id, verbatim.</summary>
    public required string Source { get; init; }

    public string? RawText { get; init; }

    /// <summary>Null where the source has no such concept. Never assume it is present.</summary>
    public decimal? Confidence { get; init; }

    public MapPoint ToMapPoint() => new(
        decimal.Parse(X, CultureInfo.InvariantCulture),
        decimal.Parse(Y, CultureInfo.InvariantCulture),
        Source,
        RawText,
        Confidence);

    public BoardPoint ToBoardPoint() => new(Ordinal, Label, ToMapPoint());
}

/// <summary>
/// A whole request row. This is the payload of request.submitted, and the base of every frame that
/// puts a row back on a board, so those frames are structurally incapable of being a delta.
/// </summary>
public record RequestBody
{
    public required Guid Id { get; init; }

    public required Guid GroupId { get; init; }

    public required Guid DeploymentId { get; init; }

    public required string TicketCode { get; init; }

    public required string TypeId { get; init; }

    public required IReadOnlyList<string> TargetRoleIds { get; init; }

    public required RequestState State { get; init; }

    public required Priority Priority { get; init; }

    public IReadOnlyList<string> Modifiers { get; init; } = [];

    public string? Note { get; init; }

    public required Guid RequestedByParticipantId { get; init; }

    /// <summary>The wire key is `requested_by_callsign`, beside `requested_by_participant_id`.
    ///
    /// Nullable because the server only resolves it where it knows it. Three names existed for
    /// this one field: the prose said 'requester callsign', the realtime body emitted
    /// `requested_by_callsign`, and this record demanded `requester_callsign` and was `required`,
    /// so a submit response the agent had just caused was unreadable.
    /// </summary>
    public string? RequestedByCallsign { get; init; }

    public int CoRequesterCount { get; init; }

    public int? QuantityRequested { get; init; }

    public int? QuantityDelivered { get; init; }

    public Guid? ClaimedByParticipantId { get; init; }

    public string? ClaimedByCallsign { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public int ReleaseCount { get; init; }

    public Guid? RelatedRequestId { get; init; }

    public Guid? SupersedesRequestId { get; init; }

    /// <summary>Server wall clock. Render it through the clock offset, never raw.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required int Version { get; init; }

    public required IReadOnlyList<PointBody> Points { get; init; }

    /// <summary>
    /// Builds the board row. The overlay label comes from the catalog because the wire does not
    /// carry it, and the row starts with no slot: the allocator assigns one.
    /// </summary>
    public BoardRow ToBoardRow(string overlayLabel) => new()
    {
        Id = Id,
        DeploymentId = DeploymentId,
        TicketCode = TicketCode,
        TypeId = TypeId,
        OverlayLabel = overlayLabel,
        TargetRoleIds = TargetRoleIds,
        Priority = Priority,
        Modifiers = Modifiers,
        QuantityRequested = QuantityRequested,
        QuantityDelivered = QuantityDelivered,
        Points = [.. Points.Select(p => p.ToBoardPoint())],
        RequestedByParticipantId = RequestedByParticipantId,
        // The board always renders a name. A row the server did not resolve one for shows the
        // ticket rather than an empty gap, which is what the overlay treats as unknown.
        RequestedByCallsign = RequestedByCallsign ?? TicketCode,
        CoRequesterCount = CoRequesterCount,
        State = State,
        ClaimantCallsign = ClaimedByCallsign,
        ClaimantParticipantId = ClaimedByParticipantId,
        ExpiresAt = ExpiresAt,
        CreatedAt = CreatedAt,
        Version = Version,
        ReleaseCount = ReleaseCount,
        RelatedRequestId = RelatedRequestId,
        SupersedesRequestId = SupersedesRequestId,
        Note = Note,
    };
}

// ---------------------------------------------------------------------------
// Server to client
// ---------------------------------------------------------------------------

/// <summary>The deployment named in a subscription entry.</summary>
public sealed record DeploymentRef
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public int MemberCount { get; init; }

    /// <summary>Six digits. Rotating it re-emits deployment.roster.</summary>
    public string? InviteCode { get; init; }
}

/// <summary>One group's derived topic set. The client never names a topic.</summary>
public sealed record SubscriptionEntry
{
    public required Guid GroupId { get; init; }

    /// <summary>Null when the member is on no deployment. The board stays empty; that is not a fault.</summary>
    public DeploymentRef? Deployment { get; init; }

    public IReadOnlyList<string> Topics { get; init; } = [];
}

/// <summary>
/// Always seq 0. <see cref="ServerTime"/> re-derives the clock offset on every ready, including
/// after every reconnect. An empty subscription set is the normal cold-start state.
/// </summary>
public sealed record ReadyPayload
{
    public required string SessionId { get; init; }

    public required int ConfigVersion { get; init; }

    /// <summary>Server wall clock at send. The only source of the clock offset.</summary>
    public required DateTimeOffset ServerTime { get; init; }

    public IReadOnlyList<SubscriptionEntry> Subscriptions { get; init; } = [];

    /// <summary>True when another live session holds the same participant. The digits differ per device.</summary>
    public bool AnotherDeviceOnBoard { get; init; }
}

/// <summary>Liveness. Carries the outbox drain age so a stalled event channel is visible.</summary>
public sealed record PingPayload
{
    public double? OutboxDrainAgeS { get; init; }
}

/// <summary>New topic set after a role, membership or deployment change. No reconnect.</summary>
public sealed record SubscriptionsChangedPayload
{
    public IReadOnlyList<SubscriptionEntry> Subscriptions { get; init; } = [];
}

/// <summary>The agent refetches config over HTTPS. The frame carries no config.</summary>
public sealed record ConfigChangedPayload
{
    public required int ConfigVersion { get; init; }
}

/// <summary>The replay buffer missed. Drop local state and re-seed over HTTPS.</summary>
public sealed record ResyncRequiredPayload
{
    public string? Reason { get; init; }
}

/// <summary>Outbox drain age. Past the threshold the tray goes amber and the overlay reads BOARD MAY BE STALE.</summary>
public sealed record StreamDegradedPayload
{
    public required double DrainAgeS { get; init; }

    public double? ThresholdS { get; init; }
}

/// <summary>Correlates to a client frame id.</summary>
public sealed record ErrorPayload
{
    public required string Code { get; init; }

    public string? Detail { get; init; }

    public string? InReplyTo { get; init; }
}

/// <summary>The hot path. A full row for a request nobody has seen.</summary>
public sealed record RequestSubmittedPayload : RequestBody;

/// <summary>
/// Sent to everyone who could see the row, so losing boards clear instantly. Every non-claimant
/// drops the row, which is why the four row-returning frames must carry a whole body.
/// </summary>
public sealed record RequestClaimedPayload
{
    public required Guid RequestId { get; init; }

    public required Guid ClaimedByParticipantId { get; init; }

    public required string Callsign { get; init; }

    public required int Version { get; init; }
}

public sealed record RequestStartedPayload
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }
}

/// <summary>Non-terminal. State stays in_progress and the adjust loop begins.</summary>
public sealed record RequestRoundsAwayPayload
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }

    /// <summary>Time of flight when the claimant computed one locally.</summary>
    public double? EtaS { get; init; }
}

/// <summary>Non-terminal. The spotter correction loop; the state does not move.</summary>
public sealed record RequestAdjustedPayload
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }

    public required AdjustDirection Direction { get; init; }

    public int? Metres { get; init; }

    public string? ActorCallsign { get; init; }
}

/// <summary>
/// Terminal for serviced and no_longer_required. On <see cref="Model.Outcome.Unable"/> the request
/// returns to open and <see cref="Request"/> carries the whole row.
/// </summary>
public sealed record RequestCompletedPayload
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }

    public double? DurationS { get; init; }

    /// <summary>No default. An old agent that assumes one gets unable wrong.</summary>
    public required Outcome Outcome { get; init; }

    public int? QuantityDelivered { get; init; }

    /// <summary>Present on unable only.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The full row when the outcome is unable. NESTED form, kept for older servers.
    /// </summary>
    /// <remarks>
    /// The live server flattens it: complete_request does payload.update(request_body(updated)),
    /// so id, points and state sit BESIDE request_id rather than under a request key. This stayed
    /// null on every real unable completion and ReopenedRow threw, so the reopened row never
    /// returned to any board.
    /// </remarks>
    public RequestBody? Request { get; init; }



    [JsonIgnore]
    public bool ReturnsToOpen => Outcome == Outcome.Unable;

    /// <summary>The row to put back on the board, from whichever shape the server used.</summary>
    public RequestBody ReopenedRow => Request ?? Flat
        ?? throw new InvalidOperationException(
            $"request.completed outcome {Outcome} for {RequestId} carries no request body to re-render.");

    /// <summary>The flat row the live server sends, filled by the reader when there is one.</summary>
    [JsonIgnore]
    public RequestBody? Flat { get; init; }
}

/// <summary>
/// The claimant handed it back. A full row, because every board dropped it on request.claimed.
/// Upsert and reacquire a slot; this is never a delta.
/// </summary>
public sealed record RequestReleasedPayload : RequestBody
{
    public required ReleaseReason Reason { get; init; }
}

/// <summary>Terminal. Drop the row and follow the new ticket.</summary>
public sealed record RequestSupersededPayload
{
    public required Guid RequestId { get; init; }

    public required Guid SupersededByRequestId { get; init; }

    public required int Version { get; init; }
}

/// <summary>
/// A wider audience, not a state change. At level deployment the recipients are precisely the
/// people who never had the row, so it carries the whole one.
/// </summary>
public sealed record RequestEscalatedPayload : RequestBody
{
    public required EscalationLevel Level { get; init; }

    public double? UnclaimedS { get; init; }
}

public sealed record RequestCancelledPayload
{
    public required Guid RequestId { get; init; }

    public string? ActorCallsign { get; init; }

    public required int Version { get; init; }
}

public sealed record RequestExpiredPayload
{
    public required Guid RequestId { get; init; }
}

/// <summary>
/// The database's answer to a divergent presence. Full bodies, restore exactly these rows, and
/// never a release: presence is a report and the database wins.
/// </summary>
public sealed record ClaimsReconcilePayload
{
    /// <summary>
    /// The rows the database says this participant holds. The wire key is <c>claims</c>.
    /// </summary>
    /// <remarks>
    /// It was <c>requests</c> here and <c>claims</c> on the server, so the required member was
    /// always absent, deserialization threw and the frame was dropped: a restarted agent never got
    /// its claims back, which is the one job this frame has. The agent's own test hand-built the
    /// frame with the agent's spelling, so it agreed with the DTO and never with the server.
    /// <para>
    /// Both spellings are accepted. The server is deployed and older builds are in the wild, so the
    /// reading end is the one that gives.
    /// </para>
    /// </remarks>
    [JsonPropertyName("claims")]
    public IReadOnlyList<RequestBody> Claims { get; init; } = [];

    [JsonPropertyName("requests")]
    public IReadOnlyList<RequestBody> Requests { get; init; } = [];

    /// <summary>Whichever the server actually sent.</summary>
    [JsonIgnore]
    public IReadOnlyList<RequestBody> Rows => Claims.Count > 0 ? Claims : Requests;
}

/// <summary>
/// The hop. Abort the pending draft, then drop every row, reset the allocator including its reissue
/// order, and revalidate with the id from this frame rather than a remembered one.
/// </summary>
public sealed record DeploymentEnteredPayload
{
    public required Guid GroupId { get; init; }

    public required Guid DeploymentId { get; init; }

    public required string Label { get; init; }

    public int MemberCount { get; init; }

    public required DeploymentEnterSource Source { get; init; }
}

/// <summary>
/// Headcount, or an invite code rotation. Header only, no board effect.
/// </summary>
public sealed record DeploymentRosterPayload
{
    public required Guid DeploymentId { get; init; }

    public int MemberCount { get; init; }

    /// <summary>Re-emitted on rotation, because the old six digits become poison the moment it happens.</summary>
    public required string InviteCode { get; init; }

    /// <summary>Null unless this frame is a rotation.</summary>
    public string? RotatedByCallsign { get; init; }
}

/// <summary>
/// One frame for the whole stand-down, never N request.cancelled. Abort the draft, clear the board,
/// reset the allocator.
/// </summary>
public sealed record DeploymentClosedPayload
{
    public required Guid GroupId { get; init; }

    public required Guid DeploymentId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Stop rendering that group and say why. Never closes the socket.</summary>
public sealed record MembershipEndedPayload
{
    public required Guid GroupId { get; init; }

    public required MembershipEndReason Reason { get; init; }
}

/// <summary>The user bound a provider on the web. Carries no token and no subject.</summary>
public sealed record IdentityAccountLinkedPayload
{
    public required string Provider { get; init; }

    public required string Display { get; init; }
}

/// <summary>Used by both group.frozen and group.unfrozen.</summary>
public sealed record GroupFrozenPayload
{
    public required Guid GroupId { get; init; }
}

// ---------------------------------------------------------------------------
// Client to server
// ---------------------------------------------------------------------------

/// <summary>
/// The heartbeat, every 15 s. A presence frame never releases anything: an omitted claim is
/// answered with claims.reconcile, never treated as a hand-back.
/// </summary>
public sealed record PresenceCommand
{
    public required IReadOnlyList<Guid> ClaimedRequestIds { get; init; }

    /// <summary>Idle sustained past one grace interval releases this participant's claims.</summary>
    public required PresenceState State { get; init; }

    /// <summary>Null means the detector has no answer right now. It never moves anybody.</summary>
    public string? ServerKey { get; init; }
}

/// <summary>The manual override. Pins the participant until the reported server key changes.</summary>
public sealed record DeploymentEnterCommand
{
    public required Guid DeploymentId { get; init; }
}

/// <summary>Six digits. Valid from a connection with no memberships at all.</summary>
public sealed record DeploymentJoinCommand
{
    public required string InviteCode { get; init; }
}

/// <summary>Replay from last_seq, or resync_required.</summary>
public sealed record ResumeCommand
{
    public required string SessionId { get; init; }

    public required long LastSeq { get; init; }
}

/// <summary>Rate limited per connection on top of the concurrent-claim cap.</summary>
public sealed record RequestClaimCommand
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }
}

/// <summary>Non-terminal. Starts a row claimed before claim-starts-it on the way through.</summary>
public sealed record RequestRoundsAwayCommand
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }
}

/// <summary>Non-terminal. Sender holds the paired spotter request or is the requester.</summary>
public sealed record RequestAdjustCommand
{
    public required Guid RequestId { get; init; }

    public required AdjustDirection Direction { get; init; }

    public int? Metres { get; init; }

    public required int Version { get; init; }
}

/// <summary>Unable returns the request to open and the answering frame carries the full body.</summary>
public sealed record RequestCompleteCommand
{
    public required Guid RequestId { get; init; }

    public required Outcome Outcome { get; init; }

    public int? QuantityDelivered { get; init; }

    /// <summary>Short reason, on unable.</summary>
    public string? Reason { get; init; }

    public required int Version { get; init; }
}

/// <summary>Voluntary hand-back. Answered with request.released carrying the full body.</summary>
public sealed record RequestReleaseCommand
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }
}

public sealed record RequestCancelCommand
{
    public required Guid RequestId { get; init; }

    public required int Version { get; init; }
}
