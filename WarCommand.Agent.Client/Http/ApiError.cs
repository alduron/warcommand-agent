using System.Globalization;
using System.Text.Json;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Http;

/// <summary>
/// The registry in 07-api-http.md. <c>code</c> is the contract and never changes; <c>detail</c> is
/// for humans and may change freely, so nothing branches on it.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationError = "validation_error";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string RateLimited = "rate_limited";
    public const string JoinCodeInvalid = "join_code_invalid";
    public const string JoinCodeBanned = "join_code_banned";
    public const string CallsignTaken = "callsign_taken";
    public const string AlreadyMember = "already_member";
    public const string GroupFrozen = "group_frozen";
    public const string DeploymentClosed = "deployment_closed";
    public const string DeploymentMismatch = "deployment_mismatch";
    public const string PickAMatch = "pick_a_match";
    public const string InviteCodeInvalid = "invite_code_invalid";
    public const string InvitesDisabled = "invites_disabled";
    public const string VisitorNotPermitted = "visitor_not_permitted";
    public const string VisitorCannotModerate = "visitor_cannot_moderate";
    public const string CannotRemoveMember = "cannot_remove_member";
    public const string UnknownSupplyKind = "unknown_supply_kind";
    public const string RequestAlreadyClaimed = "request_already_claimed";
    public const string RequestNotClaimable = "request_not_claimable";
    public const string RequestWrongState = "request_wrong_state";
    public const string PointCountMismatch = "point_count_mismatch";
    public const string OpenRequestLimit = "open_request_limit";
    public const string UnknownRequestType = "unknown_request_type";
    public const string RoleNotEnabled = "role_not_enabled";
    public const string PairingTicketInvalid = "pairing_ticket_invalid";
    public const string DeviceRevoked = "device_revoked";
    public const string UnknownProvider = "unknown_provider";
    public const string AuthStateInvalid = "auth_state_invalid";
    public const string AuthProviderFailed = "auth_provider_failed";
    public const string AuthIdentityInUse = "auth_identity_in_use";
    public const string AuthAlreadyLinked = "auth_already_linked";
    public const string UpgradeTokenInvalid = "upgrade_token_invalid";
    public const string VersionConflict = "version_conflict";
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>The transport could not produce a body. Not a server code; never sent by the API.</summary>
    public const string TransportFailure = "transport_failure";

    /// <summary>A response arrived that is not the documented error shape.</summary>
    public const string MalformedErrorBody = "malformed_error_body";
}

/// <summary>One entry in <c>errors</c>. Present only on field validation.</summary>
public sealed record ApiFieldError
{
    public required string Field { get; init; }

    public required string Code { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// The one error body, every endpoint, every failure. Extra members that only some codes carry are
/// reachable through <see cref="Body"/> rather than being modelled once per code.
/// </summary>
public sealed record ApiError
{
    public string? Type { get; init; }

    public string? Title { get; init; }

    public int Status { get; init; }

    /// <summary>The contract. Branch on this and nothing else.</summary>
    public required string Code { get; init; }

    public string? Detail { get; init; }

    public string? Instance { get; init; }

    /// <summary>Server-side trace id. Report it; it is the only handle support has.</summary>
    public string? CorrelationId { get; init; }

    public IReadOnlyList<ApiFieldError> Errors { get; init; } = [];

    /// <summary>The whole parsed body, for the members a specific code adds.</summary>
    public JsonElement Body { get; init; }

    private T? Member<T>(string name) =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var value)
            ? value.Deserialize<T>(AgentJson.Options)
            : default;

    /// <summary>request_already_claimed: who won the race.</summary>
    public string? ClaimedByCallsign => Member<string>("claimed_by_callsign");

    /// <summary>version_conflict and request_already_claimed: the row's current version.</summary>
    public int? CurrentVersion => Member<int?>("current_version");

    /// <summary>deployment_mismatch: the deployment the coordinate was read in.</summary>
    public Guid? CapturedInDeploymentId => Member<Guid?>("captured_in_deployment_id");

    /// <summary>deployment_mismatch: the caller's current deployment.</summary>
    public Guid? CurrentDeploymentId => Member<Guid?>("deployment_id");

    /// <summary>pick_a_match: the live list, so the overlay renders the picker with no second call.</summary>
    public IReadOnlyList<PickAMatchDeployment> Deployments =>
        Member<IReadOnlyList<PickAMatchDeployment>>("deployments") ?? [];

    /// <summary>Builds the error from a response body, falling back when the shape is not ours.</summary>
    public static ApiError Parse(string? body, int status, string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ApiError
            {
                Code = status == 429 ? ErrorCodes.RateLimited : ErrorCodes.MalformedErrorBody,
                Status = status,
                CorrelationId = correlationId,
                Detail = "The server returned no body.",
            };
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                return new ApiError
                {
                    Code = ErrorCodes.MalformedErrorBody,
                    Status = status,
                    CorrelationId = correlationId,
                    Body = root.Clone(),
                };
            }

            var errors = new List<ApiFieldError>();
            if (root.TryGetProperty("errors", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                errors.AddRange(fields.Deserialize<List<ApiFieldError>>(AgentJson.Options) ?? []);
            }

            return new ApiError
            {
                Code = code.GetString()!,
                Status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var i) ? i : status,
                Type = Text(root, "type"),
                Title = Text(root, "title"),
                Detail = Text(root, "detail"),
                Instance = Text(root, "instance"),
                CorrelationId = Text(root, "correlation_id") ?? correlationId,
                Errors = errors,
                Body = root.Clone(),
            };
        }
        catch (JsonException)
        {
            return new ApiError
            {
                Code = ErrorCodes.MalformedErrorBody,
                Status = status,
                CorrelationId = correlationId,
                Detail = "The error body was not JSON.",
            };
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Code} ({Status}) correlation_id={CorrelationId ?? "none"}");
}

/// <summary>One row of the live deployment list carried by a pick_a_match body.</summary>
public sealed record PickAMatchDeployment
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public int MemberCount { get; init; }

    public int OpenRequests { get; init; }
}
