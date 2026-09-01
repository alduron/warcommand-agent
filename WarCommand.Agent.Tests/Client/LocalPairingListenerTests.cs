using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WarCommand.Agent.Client.Link;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// The loopback listener is the zero-step link, and it is also a door on the user's machine. These
/// tests are mostly about what it refuses.
/// </summary>
public sealed class LocalPairingListenerTests : IDisposable
{
    private const string Allowed = "https://warcommand.app";
    private const string Hostile = "https://warcommand.app.evil.test";
    private const string LinkedUserId = "018f4c2a-0000-0000-0000-000000000001";

    private readonly List<string> _redeemed = [];
    private readonly HttpClient _http = new();
    private readonly LocalPairingListener _listener;
    private readonly bool _bound;

    private string? _userId;
    private bool _redeemThrows;

    public LocalPairingListenerTests()
    {
        _listener = new LocalPairingListener(
            new LocalPairingOptions { AllowedOrigins = [Allowed], AgentVersion = "0.0.0-test" },
            (ticket, _) =>
            {
                if (_redeemThrows)
                {
                    throw new InvalidOperationException("refused");
                }

                _redeemed.Add(ticket);
                _userId = LinkedUserId;
                return Task.CompletedTask;
            },
            () => _userId);

        _bound = _listener.Start();
    }

    private string Url(string path) => $"http://127.0.0.1:{_listener.Port}{path}";

    private HttpRequestMessage Hello(string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url("/wc/v1/hello"));
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private HttpRequestMessage Pair(string? origin, string ticket, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url("/wc/v1/pair"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { ticket }), Encoding.UTF8, contentType),
        };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    [Fact]
    public void It_binds_a_loopback_port_in_its_own_range()
    {
        Assert.True(_bound);
        Assert.NotNull(_listener.Port);
        Assert.InRange(
            _listener.Port!.Value,
            LocalPairingOptions.FirstPort,
            LocalPairingOptions.FirstPort + LocalPairingOptions.PortCount - 1);
    }

    [Fact]
    public async Task Hello_tells_an_allowed_page_the_agent_is_here_and_unpaired()
    {
        var response = await _http.SendAsync(Hello(Allowed));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var hello = await response.Content.ReadFromJsonAsync<LocalPairingHello>();
        Assert.True(hello!.Agent);
        Assert.False(hello.Paired);
        Assert.Null(hello.UserId);
    }

    [Fact]
    public async Task An_origin_that_only_looks_right_is_refused()
    {
        var response = await _http.SendAsync(Hello(Hostile));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_caller_with_no_origin_at_all_is_refused()
    {
        var response = await _http.SendAsync(Hello(origin: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_allowed_page_pairs_the_agent_with_no_user_action()
    {
        var response = await _http.SendAsync(Pair(Allowed, "pt_zero_step"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pt_zero_step", Assert.Single(_redeemed));
        Assert.Equal(LinkedUserId, _userId);
    }

    [Fact]
    public async Task Hello_names_the_account_the_agent_holds_so_a_page_can_compare_it()
    {
        _ = await _http.SendAsync(Pair(Allowed, "pt_first"));

        var hello = await (await _http.SendAsync(Hello(Allowed))).Content
            .ReadFromJsonAsync<LocalPairingHello>();

        Assert.True(hello!.Paired);
        Assert.Equal(LinkedUserId, hello.UserId);
    }

    [Fact]
    public async Task An_already_linked_agent_still_accepts_a_link_to_another_account()
    {
        _ = await _http.SendAsync(Pair(Allowed, "pt_first"));

        var response = await _http.SendAsync(Pair(Allowed, "pt_second"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["pt_first", "pt_second"], _redeemed);
    }

    [Fact]
    public async Task A_hostile_page_cannot_hand_over_a_ticket()
    {
        var response = await _http.SendAsync(Pair(Hostile, "pt_hostile"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_redeemed);
    }

    [Fact]
    public async Task A_form_content_type_is_refused_so_the_browser_must_preflight()
    {
        var response = await _http.SendAsync(
            Pair(Allowed, "pt_form", contentType: "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_redeemed);
    }

    [Fact]
    public async Task A_ticket_the_api_refuses_reports_unpaired_rather_than_throwing()
    {
        _redeemThrows = true;

        var response = await _http.SendAsync(Pair(Allowed, "pt_stale"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_userId);
    }

    [Fact]
    public async Task An_empty_ticket_is_refused()
    {
        var response = await _http.SendAsync(Pair(Allowed, string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_redeemed);
    }

    [Fact]
    public async Task Preflight_is_answered_for_an_allowed_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, Url("/wc/v1/pair"));
        request.Headers.Add("Origin", Allowed);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task An_unknown_route_is_not_found()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url("/wc/v1/anything-else"));
        request.Headers.Add("Origin", Allowed);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(request)).StatusCode);
    }

    public void Dispose()
    {
        _http.Dispose();
        _listener.Dispose();
    }
}
