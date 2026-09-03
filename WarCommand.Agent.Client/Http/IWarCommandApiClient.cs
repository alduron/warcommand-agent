using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Http;

/// <summary>Supplies a live agent token, refreshing it before it lapses.</summary>
public interface IAgentTokenSource
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when the device holds no token.</summary>
    ValueTask<string> GetAgentTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Mints the single-use realtime ticket. Split out so the socket needs no HTTP client.</summary>
public interface IRealtimeTicketSource
{
    Task<RealtimeTicket> AcquireRealtimeTicketAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Every HTTPS call the agent makes. Failures are <see cref="WarCommandApiException"/> carrying the
/// contract's <c>code</c> and the correlation id.
/// </summary>
public interface IWarCommandApiClient : IRealtimeTicketSource
{
    // Devices and pairing. These take the device token explicitly: it is not the agent token and
    // it is not held by the token source.
    Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegisterRequest request, CancellationToken cancellationToken);

    Task<DeviceActivation> ActivateDeviceAsync(Guid deviceId, string deviceToken, string? callsignHint, CancellationToken cancellationToken);

    Task<PairingCode> CreatePairingCodeAsync(Guid deviceId, string deviceToken, CancellationToken cancellationToken);

    Task<PairingClaim> ClaimPairingByTicketAsync(string deviceToken, string ticket, string? pttBinding, CancellationToken cancellationToken);

    Task<PairingClaim> ClaimPairingByPairCodeAsync(string deviceToken, string pairCode, string? pttBinding, CancellationToken cancellationToken);

    Task<DeviceInfo> UpdateDeviceAsync(Guid deviceId, DeviceUpdate update, CancellationToken cancellationToken);

    // Identity.
    Task<HandoffLink> CreateWebHandoffAsync(CancellationToken cancellationToken);

    Task DismissLinkPromptAsync(CancellationToken cancellationToken);

    Task<MeResponse> GetMeAsync(CancellationToken cancellationToken);

    /// <summary>Rotating refresh. The presented token is spent; the store records that it rotated.</summary>
    Task<TokenPair> RefreshTokensAsync(string refreshToken, CancellationToken cancellationToken);

    // Catalog. Three calls, because the three documents change on different clocks.
    Task<CatalogFetch> GetRequestTypesAsync(string? etag, CancellationToken cancellationToken);

    Task<CatalogFetch> GetGameProfileAsync(string? etag, CancellationToken cancellationToken);

    Task<CatalogFetch> GetBallisticsAsync(string? etag, CancellationToken cancellationToken);

    // Deployments.
    Task<Page<DeploymentSummary>> GetDeploymentsAsync(Guid groupId, string? cursor, int? limit, CancellationToken cancellationToken);

    /// <summary>One page of THE board. There is no filtered form and no group-scoped equivalent.</summary>
    Task<Page<RequestBody>> GetBoardPageAsync(Guid deploymentId, BoardQuery? query, CancellationToken cancellationToken);

    /// <summary>
    /// The whole board: open plus claimed plus in_progress, with claimant callsigns. The only seed
    /// and revalidation call, at startup, on reconnect, on resync_required, on deployment.entered
    /// and on Panic resume. Recovering the agent's own claims is its primary job.
    /// </summary>
    Task<IReadOnlyList<RequestBody>> GetBoardAsync(Guid deploymentId, BoardQuery? query, CancellationToken cancellationToken);

    Task<DeploymentJoinResult> JoinDeploymentAsync(string inviteCode, CancellationToken cancellationToken);

    /// <summary>The override. Pins until `server_key` changes. Returns a wrapper, not a bare row.</summary>
    Task<DeploymentEntered> EnterDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken);

    /// <summary>Members only. Returns the new six digits; the server re-emits them on deployment.roster.</summary>
    Task<string> RotateInviteCodeAsync(Guid deploymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Stands the match down and reopens it under the same label. Admin and owner only.
    /// </summary>
    /// <remarks>
    /// Restart IS close with supersede: there is no second endpoint, and inventing one in the
    /// client would be a second way to ask the same question that could answer differently.
    /// </remarks>
    Task RestartDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken);

    /// <summary>Who is on this match: callsigns, roles and kind. The overlay's PEOPLE page.</summary>
    Task<Page<ParticipantInfo>> GetParticipantsAsync(Guid deploymentId, string? cursor, int? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Toggles one role subscription for the caller on this deployment. The server decides.
    /// </summary>
    /// <remarks>
    /// The overlay's ROLES page is the only caller. Subscriptions decide what reaches you, so this
    /// is the one control on the surface that changes the board rather than reading it.
    /// </remarks>
    /// <summary>Toggles one role and returns the subscription the server now holds for you.</summary>
    Task<IReadOnlyList<string>> ToggleMyRoleAsync(
        Guid deploymentId,
        string roleId,
        CancellationToken cancellationToken);

    // Requests.
    /// <summary>
    /// Carries an Idempotency-Key and a required captured_in_deployment_id. A replay returns the
    /// original response; the same key with a different body is 409 idempotency_key_reused.
    /// </summary>
    /// <summary>Returns a wrapper: a submit may coalesce onto an existing request or supersede.</summary>
    Task<SubmitResult> SubmitRequestAsync(Guid groupId, string idempotencyKey, SubmitRequestBody body, CancellationToken cancellationToken);

    // Updates.
    Task<AgentRelease> GetLatestAgentAsync(CancellationToken cancellationToken);
}
