using System.Net;
using System.Text;
using System.Text.Json;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// The HTTPS surface: https only, a correlation id on every call, one typed error, and a board
/// endpoint with no state filter.
/// </summary>
public class WarCommandApiClientTests
{
    [Fact]
    public async Task Every_call_carries_a_correlation_id_and_the_bearer()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"user":{"id":"018f4c2a-0000-0000-0000-000000000001","callsign":"aldur","auth_provider":"discord","created_at":"2026-01-01T00:00:00Z"},"memberships":[],"devices":[]}""");

        using var client = Build(handler);
        await client.GetMeAsync(CancellationToken.None);

        var request = handler.Requests[0];
        Assert.True(request.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("agent-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A_plaintext_base_address_is_refused()
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentException>(() => new ApiClientOptions { BaseAddress = new Uri("http://api.warcommand.app") });
    }

    [Fact]
    public async Task The_error_body_becomes_one_typed_exception_carrying_the_code()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Conflict, """
            {"type":"https://errors.warcommand.app/request_already_claimed",
             "title":"Already claimed","status":409,"code":"request_already_claimed",
             "detail":"Bear claimed this 200ms before you.","instance":"/v1/requests/1",
             "correlation_id":"01J8ZQ4T7K","claimed_by_callsign":"Bear","current_version":2}
            """);

        using var client = Build(handler);
        var thrown = await Assert.ThrowsAsync<WarCommandApiException>(
            () => client.GetMeAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestAlreadyClaimed, thrown.Code);
        Assert.Equal(409, thrown.Status);
        Assert.Equal("01J8ZQ4T7K", thrown.CorrelationId);
        Assert.Equal("Bear", thrown.Error.ClaimedByCallsign);
        Assert.Equal(2, thrown.Error.CurrentVersion);
        Assert.False(thrown.IsTransient);
    }

    [Fact]
    public async Task A_pick_a_match_body_carries_the_picker()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Conflict, """
            {"code":"pick_a_match","status":409,"detail":"You are not on a match. Pick the one you are in, or open one.",
             "deployments":[{"id":"018f4c2a-0000-0000-0000-00000000000a","label":"ALPHA","member_count":31,"open_requests":4}]}
            """);

        using var client = Build(handler);
        var thrown = await Assert.ThrowsAsync<WarCommandApiException>(
            () => client.SubmitRequestAsync(Guid.NewGuid(), "key-1", Body(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.PickAMatch, thrown.Code);
        Assert.Single(thrown.Error.Deployments);
        Assert.Equal("ALPHA", thrown.Error.Deployments[0].Label);
    }

    [Fact]
    public async Task A_deployment_mismatch_names_both_deployments()
    {
        var captured = Guid.NewGuid();
        var current = Guid.NewGuid();

        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Conflict, $$"""
            {"code":"deployment_mismatch","status":409,
             "captured_in_deployment_id":"{{captured}}","deployment_id":"{{current}}"}
            """);

        using var client = Build(handler);
        var thrown = await Assert.ThrowsAsync<WarCommandApiException>(
            () => client.SubmitRequestAsync(Guid.NewGuid(), "key-1", Body(captured), CancellationToken.None));

        Assert.Equal(captured, thrown.Error.CapturedInDeploymentId);
        Assert.Equal(current, thrown.Error.CurrentDeploymentId);
    }

    [Fact]
    public async Task A_submit_carries_an_idempotency_key_and_the_captured_deployment()
    {
        var captured = Guid.NewGuid();
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Created, SubmitJson(captured));

        using var client = Build(handler);
        await client.SubmitRequestAsync(Guid.NewGuid(), "idem-77", Body(captured), CancellationToken.None);

        var request = handler.Requests[0];
        Assert.Equal("idem-77", request.Headers.GetValues("Idempotency-Key").Single());

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(captured.ToString(), body.RootElement.GetProperty("captured_in_deployment_id").GetString());
        Assert.Equal("mortar_fire", body.RootElement.GetProperty("type_id").GetString());
        Assert.Equal("85.53", body.RootElement.GetProperty("points")[0].GetProperty("x").GetString());
    }

    [Fact]
    public async Task The_board_is_the_only_seed_and_it_takes_no_state_filter()
    {
        var deployment = Guid.NewGuid();
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.OK, $$"""{"data":[{{RowJson(deployment)}}],"next_cursor":"c2","has_more":true}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""{"data":[{{RowJson(deployment)}}],"next_cursor":null,"has_more":false}""");

        using var client = Build(handler);
        var rows = await client.GetBoardAsync(deployment, new BoardQuery { RoleId = "mortar" }, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Contains($"/v1/deployments/{deployment}/board", r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("state=", r.RequestUri.Query, StringComparison.Ordinal);
        });
        Assert.Contains("cursor=c2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_three_catalogs_are_three_conditional_calls()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.NotModified, string.Empty);
        handler.Enqueue(HttpStatusCode.OK, """{"data":{"version":1},"version":4}""", etag: "\"7d3f9a\"");
        handler.Enqueue(HttpStatusCode.OK, """{"data":{"version":1},"version":2}""", etag: "\"aa11\"");

        using var client = Build(handler);
        var types = await client.GetRequestTypesAsync("\"old\"", CancellationToken.None);
        var profile = await client.GetGameProfileAsync(null, CancellationToken.None);
        var ballistics = await client.GetBallisticsAsync(null, CancellationToken.None);

        Assert.True(types.NotModified);
        Assert.Null(types.Json);
        Assert.Equal("\"old\"", types.ETag);
        Assert.Equal("\"old\"", handler.Requests[0].Headers.GetValues("If-None-Match").Single());

        Assert.False(profile.NotModified);
        Assert.Equal("\"7d3f9a\"", profile.ETag);
        Assert.False(ballistics.NotModified);

        Assert.Equal("/v1/catalog/request-types", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/v1/catalog/game-profile", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/v1/catalog/ballistics", handler.Requests[2].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Registration_reports_the_ptt_binding_and_the_install_id()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Created, """{"device_id":"018f4c2a-0000-0000-0000-00000000000b","device_token":"wc_dev_x"}""");

        using var client = Build(handler);
        var registration = await client.RegisterDeviceAsync(
            new DeviceRegisterRequest
            {
                InstallId = "0123456789abcdef0123456789abcdef",
                MachineLabel = "ALDUR-DESKTOP",
                AgentVersion = "1.0.0",
                PttBinding = "Mouse5",
            },
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("Mouse5", body.RootElement.GetProperty("ptt_binding").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", body.RootElement.GetProperty("install_id").GetString());

        // The response holds a credential, so printing it redacts.
        Assert.DoesNotContain("wc_dev_x", registration.ToString(), StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Pairing_claims_by_ticket_or_by_pair_code_and_never_both()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.OK, ClaimJson());
        handler.Enqueue(HttpStatusCode.OK, ClaimJson());

        using var client = Build(handler);
        await client.ClaimPairingByTicketAsync("wc_dev_x", "pt_9f3c", "Mouse5", CancellationToken.None);
        await client.ClaimPairingByPairCodeAsync("wc_dev_x", "K7M2P4G3", "Mouse5", CancellationToken.None);

        using var first = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("pt_9f3c", first.RootElement.GetProperty("ticket").GetString());
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("pair_code").ValueKind);

        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("K7M2P4G3", second.RootElement.GetProperty("pair_code").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("ticket").ValueKind);

        Assert.All(handler.Requests, r => Assert.Equal("wc_dev_x", r.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task A_429_carries_retry_after()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"code":"rate_limited","status":429}""", retryAfterSeconds: 30);

        using var client = Build(handler);
        var thrown = await Assert.ThrowsAsync<WarCommandApiException>(
            () => client.JoinDeploymentAsync("921585", CancellationToken.None));

        Assert.Equal(ErrorCodes.RateLimited, thrown.Code);
        Assert.Equal(TimeSpan.FromSeconds(30), thrown.RetryAfter);
        Assert.True(thrown.IsTransient);
    }

    [Fact]
    public async Task A_realtime_ticket_never_prints_itself()
    {
        using var handler = new StubHandler();
        handler.Enqueue(HttpStatusCode.Created, """{"ticket":"rt_secret","expires_in":30}""");

        using var client = Build(handler);
        var ticket = await client.AcquireRealtimeTicketAsync(CancellationToken.None);

        Assert.Equal("rt_secret", ticket.Ticket);
        Assert.DoesNotContain("rt_secret", ticket.ToString(), StringComparison.Ordinal);
    }

    private static WarCommandApiClient Build(StubHandler handler)
    {
        var options = new ApiClientOptions { BaseAddress = new Uri("https://api.warcommand.app") };
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = options.BaseAddress };
        return new WarCommandApiClient(http, options, new StubTokenSource(), new RecordingLog());
    }

    private static SubmitRequestBody Body(Guid deploymentId) => new()
    {
        TypeId = "mortar_fire",
        CapturedInDeploymentId = deploymentId,
        Priority = Priority.Urgent,
        Points =
        [
            new PointBody { Ordinal = 0, Label = "target", X = "85.53", Y = "69.42", Source = "map_readout" },
        ],
        ClientSubmittedAt = DateTimeOffset.UtcNow,
    };

    private static string RowJson(Guid deploymentId) => JsonSerializer.Serialize(
        ServerFrame.RequestRow(Guid.NewGuid(), deploymentId),
        AgentJson.Options);

    /// <summary>What `POST /v1/groups/{gid}/requests` actually returns: a wrapper, not a row.
    ///
    /// A submit does not always create one. Coalescing attaches the caller to an existing open
    /// request at nearly the same grid and returns THAT, and a repeat by the same participant
    /// supersedes. This fixture returned a bare row and the client was written to match it, so
    /// both sides agreed with each other and neither agreed with the server.
    /// </summary>
    private static string SubmitJson(Guid deploymentId) =>
        $$"""
        {"request": {{RowJson(deploymentId)}}, "created": true, "coalesced": false,
         "superseded_request_id": null, "co_requester_count": 1}
        """;

    private static string ClaimJson() => """
        {"agent_token":"wc_agt_x","refresh_token":"wc_rft_x",
         "config":{"config_version":1,
                   "user":{"id":"018f4c2a-0000-0000-0000-000000000001","callsign":"aldur","auth_provider":"discord","created_at":"2026-01-01T00:00:00Z"},
                   "memberships":[],
                   "realtime_url":"wss://api.warcommand.app/v1/realtime"}}
        """;

    private sealed class StubTokenSource : IAgentTokenSource
    {
        public ValueTask<string> GetAgentTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("agent-token");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public void Enqueue(HttpStatusCode status, string body, string? etag = null, int? retryAfterSeconds = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"code":"not_found","status":500}""", Encoding.UTF8, "application/json"),
                };
        }
    }
}
