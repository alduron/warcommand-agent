using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Http;

/// <summary>
/// The agent's whole HTTPS surface. Pinned to https, one correlation id per call, and one typed
/// exception carrying the contract's error code.
/// </summary>
public sealed class WarCommandApiClient : IWarCommandApiClient, IDisposable
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string JsonMediaType = "application/json";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ApiClientOptions _options;
    private readonly IAgentTokenSource _tokens;
    private readonly IClientLog _log;
    private readonly Func<string> _newCorrelationId;

    /// <summary>
    /// Takes an <see cref="HttpClient"/> so a test can supply a handler. The base address must be
    /// https; a plaintext one throws here rather than downgrading at the first call.
    /// </summary>
    public WarCommandApiClient(
        HttpClient http,
        ApiClientOptions options,
        IAgentTokenSource tokens,
        IClientLog? log = null,
        Func<string>? newCorrelationId = null)
        : this(http, ownsHttp: false, options, tokens, log, newCorrelationId)
    {
    }

    private WarCommandApiClient(
        HttpClient http,
        bool ownsHttp,
        ApiClientOptions options,
        IAgentTokenSource tokens,
        IClientLog? log,
        Func<string>? newCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);

        _http = http;
        _ownsHttp = ownsHttp;
        _options = options;
        _tokens = tokens;
        _log = log ?? NullClientLog.Instance;
        _newCorrelationId = newCorrelationId ?? (() => Guid.NewGuid().ToString("N"));

        _http.BaseAddress ??= options.BaseAddress;
        TransportSecurity.RequireHttps(_http.BaseAddress!, nameof(http));
        if (_http.Timeout != options.Timeout)
        {
            _http.Timeout = options.Timeout;
        }
    }

    /// <summary>Builds a client that owns its own <see cref="HttpClient"/>.</summary>
    public static WarCommandApiClient Create(
        ApiClientOptions options,
        IAgentTokenSource tokens,
        IClientLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var http = new HttpClient { BaseAddress = options.BaseAddress, Timeout = options.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WarCommandAgent/{options.AgentVersion}");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
        return new WarCommandApiClient(http, ownsHttp: true, options, tokens, log, newCorrelationId: null);
    }

    // -----------------------------------------------------------------------
    // Devices and pairing
    // -----------------------------------------------------------------------

    public Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<DeviceRegistration>(
            HttpMethod.Post, "v1/devices/register", request, Auth.None(), cancellationToken: cancellationToken);
    }

    public Task<DeviceActivation> ActivateDeviceAsync(Guid deviceId, string deviceToken, string? callsignHint, CancellationToken cancellationToken) =>
        SendJsonAsync<DeviceActivation>(
            HttpMethod.Post,
            $"v1/devices/{deviceId}/activate",
            new ActivateBody { CallsignHint = callsignHint },
            Auth.Bearer(deviceToken),
            cancellationToken: cancellationToken);

    public Task<PairingCode> CreatePairingCodeAsync(Guid deviceId, string deviceToken, CancellationToken cancellationToken) =>
        SendJsonAsync<PairingCode>(
            HttpMethod.Post,
            $"v1/devices/{deviceId}/pairing-code",
            body: null,
            Auth.Bearer(deviceToken),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Unpaired-mode poll, on the bare device token. Answers <c>paired: false</c> until a live web
    /// session claims this device, then carries the same body a claim returns.
    /// </summary>
    public Task<PairingPoll> PollPairingAsync(string deviceToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        return SendJsonAsync<PairingPoll>(
            HttpMethod.Get,
            "v1/devices/me/pairing",
            body: null,
            Auth.Bearer(deviceToken),
            cancellationToken: cancellationToken);
    }

    public Task<PairingClaim> ClaimPairingByTicketAsync(string deviceToken, string ticket, string? pttBinding, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        return SendJsonAsync<PairingClaim>(
            HttpMethod.Post,
            "v1/pairing/claim",
            new PairingClaimRequest { Ticket = ticket, PttBinding = pttBinding },
            Auth.Bearer(deviceToken),
            cancellationToken: cancellationToken);
    }

    public Task<PairingClaim> ClaimPairingByPairCodeAsync(string deviceToken, string pairCode, string? pttBinding, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairCode);
        return SendJsonAsync<PairingClaim>(
            HttpMethod.Post,
            "v1/pairing/claim",
            new PairingClaimRequest { PairCode = pairCode, PttBinding = pttBinding },
            Auth.Bearer(deviceToken),
            cancellationToken: cancellationToken);
    }

    public Task<DeviceInfo> UpdateDeviceAsync(Guid deviceId, DeviceUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return SendJsonAsync<DeviceInfo>(
            HttpMethod.Patch, $"v1/devices/{deviceId}", update, Auth.Agent(), cancellationToken: cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Identity
    // -----------------------------------------------------------------------

    public Task<HandoffLink> CreateWebHandoffAsync(CancellationToken cancellationToken) =>
        SendJsonAsync<HandoffLink>(
            HttpMethod.Post, "v1/auth/handoff", body: null, Auth.Agent(), cancellationToken: cancellationToken);

    public Task DismissLinkPromptAsync(CancellationToken cancellationToken) =>
        SendNoContentAsync(
            HttpMethod.Post, "v1/auth/link-prompt/dismiss", body: null, Auth.Agent(), cancellationToken);

    public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken) =>
        SendJsonAsync<MeResponse>(HttpMethod.Get, "v1/me", body: null, Auth.Agent(), cancellationToken: cancellationToken);

    public Task<TokenPair> RefreshTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return SendJsonAsync<TokenPair>(
            HttpMethod.Post,
            "v1/auth/refresh",
            new RefreshBody { RefreshToken = refreshToken },
            Auth.None(),
            cancellationToken: cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Catalog
    // -----------------------------------------------------------------------

    public Task<CatalogFetch> GetRequestTypesAsync(string? etag, CancellationToken cancellationToken) =>
        GetCatalogAsync("v1/catalog/request-types", etag, cancellationToken);

    public Task<CatalogFetch> GetGameProfileAsync(string? etag, CancellationToken cancellationToken) =>
        GetCatalogAsync("v1/catalog/game-profile", etag, cancellationToken);

    public Task<CatalogFetch> GetBallisticsAsync(string? etag, CancellationToken cancellationToken) =>
        GetCatalogAsync("v1/catalog/ballistics", etag, cancellationToken);

    // -----------------------------------------------------------------------
    // Deployments
    // -----------------------------------------------------------------------

    public Task<Page<DeploymentSummary>> GetDeploymentsAsync(Guid groupId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var query = new QueryString()
            .Add("cursor", cursor)
            .Add("limit", limit);
        return SendJsonAsync<Page<DeploymentSummary>>(
            HttpMethod.Get, $"v1/groups/{groupId}/deployments{query}", body: null, Auth.Agent(), cancellationToken: cancellationToken);
    }

    public Task<Page<RequestBody>> GetBoardPageAsync(Guid deploymentId, BoardQuery? query, CancellationToken cancellationToken)
    {
        var q = new QueryString()
            .Add("role", query?.RoleId)
            .Add("mine", query?.Mine)
            .Add("type_id", query?.TypeId)
            .Add("claimed_by", query?.ClaimedBy)
            .Add("limit", query?.Limit)
            .Add("cursor", query?.Cursor);
        return SendJsonAsync<Page<RequestBody>>(
            HttpMethod.Get, $"v1/deployments/{deploymentId}/board{q}", body: null, Auth.Agent(), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<RequestBody>> GetBoardAsync(Guid deploymentId, BoardQuery? query, CancellationToken cancellationToken)
    {
        var rows = new List<RequestBody>();
        var cursor = query?.Cursor;
        for (var page = 0; page < _options.MaxBoardPages; page++)
        {
            var current = (query ?? new BoardQuery()) with { Cursor = cursor };
            var result = await GetBoardPageAsync(deploymentId, current, cancellationToken).ConfigureAwait(false);
            rows.AddRange(result.Data);
            if (!result.HasMore || string.IsNullOrEmpty(result.NextCursor))
            {
                return rows;
            }

            cursor = result.NextCursor;
        }

        _log.Warn($"Board seed for {deploymentId} stopped at {_options.MaxBoardPages} pages.");
        return rows;
    }

    public Task<DeploymentJoinResult> JoinDeploymentAsync(string inviteCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        return SendJsonAsync<DeploymentJoinResult>(
            HttpMethod.Post,
            "v1/deployments/join",
            new JoinBody { InviteCode = inviteCode },
            Auth.Agent(),
            cancellationToken: cancellationToken);
    }

    public Task<DeploymentEntered> EnterDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken) =>
        SendJsonAsync<DeploymentEntered>(
            HttpMethod.Post, $"v1/deployments/{deploymentId}/enter", body: null, Auth.Agent(), cancellationToken: cancellationToken);

    public async Task<string> RotateInviteCodeAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        var rotated = await SendJsonAsync<InviteRotation>(
            HttpMethod.Post,
            $"v1/deployments/{deploymentId}/invite/rotate",
            body: null,
            Auth.Agent(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return rotated.InviteCode;
    }

    // -----------------------------------------------------------------------
    // Requests
    // -----------------------------------------------------------------------

    public Task<SubmitResult> SubmitRequestAsync(Guid groupId, string idempotencyKey, SubmitRequestBody body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(body);
        return SendJsonAsync<SubmitResult>(
            HttpMethod.Post,
            $"v1/groups/{groupId}/requests",
            body,
            Auth.Agent(),
            idempotencyKey: idempotencyKey,
            cancellationToken: cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Realtime and updates
    // -----------------------------------------------------------------------

    public Task<RealtimeTicket> AcquireRealtimeTicketAsync(CancellationToken cancellationToken) =>
        SendJsonAsync<RealtimeTicket>(
            HttpMethod.Post, "v1/realtime/tickets", body: null, Auth.Agent(), cancellationToken: cancellationToken);

    public Task<AgentRelease> GetLatestAgentAsync(CancellationToken cancellationToken) =>
        SendJsonAsync<AgentRelease>(
            HttpMethod.Get, "v1/agent/latest", body: null, Auth.None(), cancellationToken: cancellationToken);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------

    private async Task<CatalogFetch> GetCatalogAsync(string path, string? etag, CancellationToken cancellationToken)
    {
        var correlationId = _newCorrelationId();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await ExecuteAsync(request, Auth.None(), correlationId, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return CatalogFetch.Unchanged(etag);
        }

        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(response, json, correlationId);
        return new CatalogFetch
        {
            NotModified = false,
            Json = json,
            ETag = response.Headers.ETag?.ToString() ?? etag,
        };
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        Auth auth,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = _newCorrelationId();
        using var request = BuildRequest(method, path, body, idempotencyKey);
        using var response = await ExecuteAsync(request, auth, correlationId, cancellationToken).ConfigureAwait(false);
        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(response, json, correlationId);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new WarCommandApiException(
                new ApiError
                {
                    Code = ErrorCodes.MalformedErrorBody,
                    Status = (int)response.StatusCode,
                    Detail = $"{method} {path} returned success with no body.",
                },
                correlationId);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, AgentJson.Options)
                ?? throw new WarCommandApiException(
                    new ApiError
                    {
                        Code = ErrorCodes.MalformedErrorBody,
                        Status = (int)response.StatusCode,
                        Detail = $"{method} {path} deserialised to null.",
                    },
                    correlationId);
        }
        catch (JsonException ex)
        {
            throw new WarCommandApiException(
                new ApiError
                {
                    Code = ErrorCodes.MalformedErrorBody,
                    Status = (int)response.StatusCode,
                    Detail = $"{method} {path}: {ex.Message}",
                },
                correlationId);
        }
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string path,
        object? body,
        Auth auth,
        CancellationToken cancellationToken)
    {
        var correlationId = _newCorrelationId();
        using var request = BuildRequest(method, path, body, idempotencyKey: null);
        using var response = await ExecuteAsync(request, auth, correlationId, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(response, json, correlationId);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), AgentJson.Options);
            request.Content = new StringContent(json, Encoding.UTF8, JsonMediaType);
        }

        return request;
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        HttpRequestMessage request,
        Auth auth,
        string correlationId,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        var bearer = auth.Kind switch
        {
            AuthKind.Agent => await _tokens.GetAgentTokenAsync(cancellationToken).ConfigureAwait(false),
            AuthKind.Explicit => auth.Token,
            _ => null,
        };

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WarCommandApiException(
                new ApiError
                {
                    Code = ErrorCodes.TransportFailure,
                    Status = 0,
                    Detail = $"{request.Method} {request.RequestUri}: {ex.Message}",
                    CorrelationId = correlationId,
                },
                correlationId,
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WarCommandApiException(
                new ApiError
                {
                    Code = ErrorCodes.TransportFailure,
                    Status = 408,
                    Detail = $"{request.Method} {request.RequestUri} timed out.",
                    CorrelationId = correlationId,
                },
                correlationId,
                ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private static void ThrowIfFailed(HttpResponseMessage response, string body, string correlationId)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var headerCorrelation = response.Headers.TryGetValues(CorrelationHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        var error = ApiError.Parse(body, (int)response.StatusCode, headerCorrelation ?? correlationId);
        throw new WarCommandApiException(error, correlationId)
        {
            RetryAfter = RetryAfterOf(response),
        };
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private enum AuthKind
    {
        None,
        Agent,
        Explicit,
    }

    private readonly struct Auth
    {
        private Auth(AuthKind kind, string? token)
        {
            Kind = kind;
            Token = token;
        }

        public AuthKind Kind { get; }

        public string? Token { get; }

        public static Auth None() => new(AuthKind.None, null);

        /// <summary>The agent token, refreshed by the token source when it is close to lapsing.</summary>
        public static Auth Agent() => new(AuthKind.Agent, null);

        /// <summary>A device token. It is not the agent token and the token source never holds it.</summary>
        public static Auth Bearer(string token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            return new Auth(AuthKind.Explicit, token);
        }
    }

    private sealed record ActivateBody
    {
        public string? CallsignHint { get; init; }
    }

    private sealed record RefreshBody
    {
        public required string RefreshToken { get; init; }

        public override string ToString() => "RefreshBody { <redacted> }";
    }

    private sealed record JoinBody
    {
        // No callsign: it is on the account and changes at PATCH /v1/me only.
        public required string InviteCode { get; init; }
    }

    private sealed record InviteRotation
    {
        public required string InviteCode { get; init; }
    }

    /// <summary>Query string builder that escapes and omits nulls.</summary>
    private sealed class QueryString
    {
        private readonly StringBuilder _builder = new();

        public QueryString Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return this;
            }

            _builder.Append(_builder.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));
            return this;
        }

        public QueryString Add(string name, int? value) =>
            value is null ? this : Add(name, value.Value.ToString(CultureInfo.InvariantCulture));

        public QueryString Add(string name, bool? value) =>
            value is null ? this : Add(name, value.Value ? "true" : "false");

        public QueryString Add(string name, Guid? value) =>
            value is null ? this : Add(name, value.Value.ToString());

        public override string ToString() => _builder.ToString();
    }
}
