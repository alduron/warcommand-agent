using System.Text.Json.Serialization;
using WarCommand.Agent.Core;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Client.Http;

/// <summary>Cursor page. Never a bare array at the top level.</summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed record Page<T>
{
    public IReadOnlyList<T> Data { get; init; } = [];

    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }
}

// ---------------------------------------------------------------------------
// Devices and pairing
// ---------------------------------------------------------------------------

/// <summary>
/// First run. <c>ptt_binding</c> is the key the user actually picked, as a display label from a
/// fixed set: never a raw key code, and never logged.
/// </summary>
public sealed record DeviceRegisterRequest
{
    /// <summary>128-bit random hex from install.id. Not a hardware fingerprint.</summary>
    public required string InstallId { get; init; }

    public required string MachineLabel { get; init; }

    public required string AgentVersion { get; init; }

    /// <summary>Null before the first-run picker has run. There is no shipped default.</summary>
    public string? PttBinding { get; init; }
}

/// <summary>
/// The unpaired device credential. It can activate, poll its own pairing state, and redeem a
/// ticket, and nothing else.
/// </summary>
public sealed record DeviceRegistration
{
    public required Guid DeviceId { get; init; }

    public required string DeviceToken { get; init; }

    /// <summary>Redacted. A device token is a credential and never reaches a log line.</summary>
    public override string ToString() => $"DeviceRegistration {{ DeviceId = {DeviceId}, DeviceToken = <redacted> }}";
}

/// <summary>An agent token plus the refresh token that rotates it.</summary>
public sealed record TokenPair
{
    public required string AgentToken { get; init; }

    public required string RefreshToken { get; init; }

    /// <summary>Seconds. The agent token is 15 minutes; refresh before it lapses, not after.</summary>
    public int? ExpiresIn { get; init; }

    public override string ToString() => "TokenPair { <redacted> }";
}

/// <summary>
/// The cold-start path: a guest user and an agent token out of a bare device token, with no
/// membership. Zero memberships afterwards is normal and permanent, not a fault.
/// </summary>
public sealed record DeviceActivation
{
    public required ConfigUser User { get; init; }

    public required string AgentToken { get; init; }

    public required string RefreshToken { get; init; }

    public required AgentConfig Config { get; init; }

    public TokenPair Tokens => new() { AgentToken = AgentToken, RefreshToken = RefreshToken };

    public override string ToString() => $"DeviceActivation {{ User = {User.Id}, Tokens = <redacted> }}";
}

/// <summary>Path A: the eight characters the tray shows for somebody to type into the web.</summary>
public sealed record PairingCode
{
    public required string Code { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Redeems a pairing intent. Exactly one of <see cref="Ticket"/> and <see cref="PairCode"/> is set:
/// the deep link carries a ticket, the tray takes the web's stable 10-minute code.
/// </summary>
public sealed record PairingClaimRequest
{
    public string? Ticket { get; init; }

    public string? PairCode { get; init; }

    /// <summary>Resent here, because the web success screen has to name the key the user chose.</summary>
    public string? PttBinding { get; init; }

    public override string ToString() =>
        $"PairingClaimRequest {{ Kind = {(Ticket is null ? "pair_code" : "ticket")}, Value = <redacted> }}";
}

/// <summary>The paired credential set plus the config payload.</summary>
public sealed record PairingClaim
{
    public required string AgentToken { get; init; }

    public required string RefreshToken { get; init; }

    public required AgentConfig Config { get; init; }

    public TokenPair Tokens => new() { AgentToken = AgentToken, RefreshToken = RefreshToken };

    public override string ToString() => "PairingClaim { Tokens = <redacted> }";
}

/// <summary>
/// The unpaired-mode poll. <c>paired: false</c> alone until somebody's live web session claims this
/// device, then the same body <c>POST /v1/pairing/claim</c> returns.
/// </summary>
/// <remarks>
/// The account it pairs to is whoever is signed in on the web, and a guest account is a signed-in
/// account: nothing here asks for a linked provider.
/// </remarks>
public sealed record PairingPoll
{
    public required bool Paired { get; init; }

    public string? AgentToken { get; init; }

    public string? RefreshToken { get; init; }

    public AgentConfig? Config { get; init; }

    /// <summary>The claim, once one has landed. Null while still unpaired.</summary>
    public PairingClaim? Claim => Paired && AgentToken is { } agent && RefreshToken is { } refresh && Config is { } config
        ? new PairingClaim { AgentToken = agent, RefreshToken = refresh, Config = config }
        : null;

    public override string ToString() => $"PairingPoll {{ Paired = {Paired}, Tokens = <redacted> }}";
}

/// <summary>A PTT rebind is reported so no web surface names a key the device is not bound to.</summary>
public sealed record DeviceUpdate
{
    public string? MachineLabel { get; init; }

    public string? PttBinding { get; init; }
}

public sealed record DeviceInfo
{
    public required Guid Id { get; init; }

    public string? MachineLabel { get; init; }

    public string? AgentVersion { get; init; }

    /// <summary>A display label from a fixed set, never a key code.</summary>
    public string? PttBinding { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }
}

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------

/// <summary>
/// A one-time web sign-in link for a device whose owner has no browser session. The tray opens the
/// URL; nobody types it.
/// </summary>
public sealed record HandoffLink
{
    public required Uri Url { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The URL is a single-use credential, so it is redacted here too.</summary>
    public override string ToString() => "HandoffLink { Url = <redacted> }";
}

/// <summary>Guest, three successful requests, not dismissed. It decides when to show a nag.</summary>
public sealed record LinkPrompt
{
    public bool Eligible { get; init; }

    public string? Reason { get; init; }
}

public sealed record MeResponse
{
    public required ConfigUser User { get; init; }

    public IReadOnlyList<ConfigMembership> Memberships { get; init; } = [];

    public IReadOnlyList<DeviceInfo> Devices { get; init; } = [];

    public LinkPrompt? LinkPrompt { get; init; }
}

// ---------------------------------------------------------------------------
// Catalog
// ---------------------------------------------------------------------------

/// <summary>
/// One conditional catalog fetch. The three documents move on different clocks, so each is fetched,
/// ETagged and adopted on its own.
/// </summary>
public sealed record CatalogFetch
{
    /// <summary>304. Keep the document in force and do not touch the store.</summary>
    public bool NotModified { get; init; }

    /// <summary>The served body, envelope and all. Null on 304. Hand it to ContractStore.TryAdopt.</summary>
    public string? Json { get; init; }

    public string? ETag { get; init; }

    public static CatalogFetch Unchanged(string? etag) => new() { NotModified = true, ETag = etag };
}

// ---------------------------------------------------------------------------
// Deployments
// ---------------------------------------------------------------------------

public sealed record DeploymentSummary
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public int MemberCount { get; init; }

    public int OpenRequests { get; init; }

    /// <summary>True when this is the caller's current deployment.</summary>
    public bool Mine { get; init; }

    /// <summary>Six digits, present for a deployment the caller is standing in.</summary>
    public string? InviteCode { get; init; }
}

public sealed record ParticipantInfo
{
    public required Guid Id { get; init; }

    public required string Callsign { get; init; }

    /// <summary>'member' or 'visitor'.</summary>
    public string? Kind { get; init; }

    public IReadOnlyList<string> RoleIds { get; init; } = [];
}

/// <summary>
/// The widest-open endpoint in the product. A member redeeming a code ENTERS
/// (<see cref="Entered"/> true, 200); a stranger becomes a visitor (201).
/// </summary>
public sealed record DeploymentJoinResult
{
    public required ParticipantInfo Participant { get; init; }

    public required DeploymentSummary Deployment { get; init; }

    public string? GroupName { get; init; }

    /// <summary>True when the caller already held a membership, so no visitor row was created.</summary>
    public bool Entered { get; init; }
}

/// <summary>The body of `POST /v1/groups/{gid}/requests`.
///
/// A wrapper, because a submit does not always create a row. Coalescing attaches the caller to an
/// existing open request of the same type at nearly the same grid and returns THAT one, and a
/// second submit by the same participant supersedes instead. The requester's strip has to tell
/// those apart: `coalesced` renders the count qualifier, `superseded_request_id` follows the new
/// ticket. Reading this as a bare request threw on every required property it has.
/// </summary>
public sealed record SubmitResult
{
    public required RequestBody Request { get; init; }

    /// <summary>False when the caller was attached to an existing request instead.</summary>
    public bool Created { get; init; }

    /// <summary>True when this attached to an open request inside the coalesce window.</summary>
    public bool Coalesced { get; init; }

    /// <summary>Set when the caller's own earlier request was replaced by this one.</summary>
    public Guid? SupersededRequestId { get; init; }

    /// <summary>Renders as MORTAR x5. One is just the requester.</summary>
    public int CoRequesterCount { get; init; }
}

/// <summary>The deployment row itself, as `enter` and `close` return it.</summary>
public sealed record DeploymentDetail
{
    public required Guid Id { get; init; }

    public required Guid GroupId { get; init; }

    public required string Label { get; init; }

    /// <summary>Created with the group and never closed.</summary>
    public bool IsDefault { get; init; }

    public string? ServerKey { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset LastActiveAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public string? CloseReason { get; init; }
}

/// <summary>The body of `POST /v1/deployments/{id}/enter`.
///
/// It is a WRAPPER, not a bare deployment, and it is not a DeploymentSummary either: the summary
/// carries member_count, open_requests and mine, which only the list endpoint computes. Reading
/// this as a bare summary threw on the two required properties it does not have.
/// </summary>
public sealed record DeploymentEntered
{
    public required DeploymentDetail Deployment { get; init; }

    /// <summary>detector, override, default, or invite.</summary>
    public string? Source { get; init; }
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>
/// The submit body. <c>captured_in_deployment_id</c> is required and the server compares it rather
/// than re-stamping, so a coordinate read on one server can never land on another's board.
/// </summary>
public sealed record SubmitRequestBody
{
    public required string TypeId { get; init; }

    /// <summary>The deployment the coordinate was read in. Compared, never re-stamped.</summary>
    public required Guid CapturedInDeploymentId { get; init; }

    public Priority Priority { get; init; } = Priority.Normal;

    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>120 characters or fewer, rendered as text.</summary>
    public string? Note { get; init; }

    /// <summary>Null on a type with no takes_quantity.</summary>
    public int? QuantityRequested { get; init; }

    /// <summary>Exactly the type's arity, in ordinal order. Coordinates are strings, never floats.</summary>
    public required IReadOnlyList<PointBody> Points { get; init; }

    /// <summary>Latency telemetry only. Never trusted for ordering.</summary>
    public required DateTimeOffset ClientSubmittedAt { get; init; }
}

/// <summary>
/// Allowlisted board filters. There is no state filter: <c>GET /v1/deployments/{id}/requests</c>
/// was deleted outright, so <c>?state=</c> is a 404 rather than a variant of the seed.
/// </summary>
public sealed record BoardQuery
{
    public string? RoleId { get; init; }

    public bool? Mine { get; init; }

    public string? TypeId { get; init; }

    public Guid? ClaimedBy { get; init; }

    /// <summary>Defaults to 50 server-side, maximum 200.</summary>
    public int? Limit { get; init; }

    public string? Cursor { get; init; }
}

// ---------------------------------------------------------------------------
// Realtime and updates
// ---------------------------------------------------------------------------

/// <summary>Single-use, 30 second TTL, worthless once redeemed. Never logged, never persisted.</summary>
public sealed record RealtimeTicket
{
    public required string Ticket { get; init; }

    /// <summary>Seconds.</summary>
    public int ExpiresIn { get; init; }

    [JsonIgnore]
    public TimeSpan Lifetime => TimeSpan.FromSeconds(ExpiresIn);

    public override string ToString() => $"RealtimeTicket {{ ExpiresIn = {ExpiresIn}, Ticket = <redacted> }}";
}

/// <summary>GET /v1/agent/latest. Never auto-installed while the game is running.</summary>
public sealed record AgentRelease
{
    public required string Version { get; init; }

    public string? Notes { get; init; }

    /// <summary>Signed installer. The installer preserves install.id.</summary>
    public Uri? Url { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>The published floor. Below it the API refuses this agent.</summary>
    public string? MinimumSupportedVersion { get; init; }
}
