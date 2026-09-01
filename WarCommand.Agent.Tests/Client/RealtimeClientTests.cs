using System.Text.Json;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// The socket rules that cost a match when they are wrong: 4003 is terminal, presence releases
/// nothing, a row-returning frame carries the row, and the clock offset is re-derived on every
/// ready.
/// </summary>
public class RealtimeClientTests
{
    private static readonly Uri Url = new("wss://api.warcommand.app/v1/realtime");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Server-derived. The client never names one; it only reports what it was given.</summary>
    private static readonly string[] Topics = ["deployment.all", "role.mortar"];

    [Fact]
    public async Task Close_4003_device_revoked_is_never_retried()
    {
        var tickets = new FakeTicketSource();
        var factory = new FakeChannelFactory(new FakeWebSocketChannel { CloseOnConnect = 4003 });
        var observer = new RecordingObserver();
        var client = Build(tickets, factory, observer, new TestDelay(PresenceInterval), out _);

        using var cts = new CancellationTokenSource(Timeout);
        await client.RunAsync(cts.Token);

        Assert.True(observer.DeviceRevoked);
        Assert.Single(factory.Created);
        Assert.Equal(1, tickets.Calls);
        Assert.Equal(RealtimeConnectionState.Stopped, client.State);
    }

    [Fact]
    public async Task Any_other_close_code_reconnects()
    {
        var first = new FakeWebSocketChannel { CloseOnConnect = 4009 };
        var second = new FakeWebSocketChannel();
        second.Push(Ready("s-1", DateTimeOffset.UtcNow));

        var factory = new FakeChannelFactory(first, second);
        var observer = new RecordingObserver();
        var client = Build(new FakeTicketSource(), factory, observer, new TestDelay(PresenceInterval), out _);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);

        Assert.True(observer.ReadySignal.Wait(Timeout));
        await cts.CancelAsync();
        await run;

        Assert.Equal(2, factory.Created.Count);
        Assert.False(observer.DeviceRevoked);
    }

    [Fact]
    public async Task Presence_reports_its_claims_and_never_releases_anything()
    {
        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow));

        var presence = new FakePresenceSource { ServerKey = "srv-a" };
        var observer = new RecordingObserver();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _, presence);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));

        // The agent restarted and holds nothing. The database says it holds one claim.
        Assert.True(client.SendPresence());
        await Until(() => channel.SentOfType(FrameTypes.Presence).Count == 1);

        var held = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        channel.Push(ServerFrame.Of(
            FrameTypes.ClaimsReconcile,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["requests"] = new[] { ServerFrame.RequestRow(held, deployment, "claimed") },
            },
            seq: 2));

        await Until(() => observer.Board.ContainsKey(held));
        await cts.CancelAsync();
        await run;

        var frame = channel.SentOfType(FrameTypes.Presence)[0];
        var payload = frame.GetProperty("payload");
        Assert.Empty(payload.GetProperty("claimed_request_ids").EnumerateArray());
        Assert.Equal("in_game", payload.GetProperty("state").GetString());
        Assert.Equal("srv-a", payload.GetProperty("server_key").GetString());

        // A divergent presence is answered with reconcile. It never turns into a release.
        Assert.Empty(channel.SentOfType(FrameTypes.RequestRelease));
        Assert.Equal("Bear", observer.Board[held].RequestedByCallsign);
    }

    [Theory]
    [InlineData(FrameTypes.RequestReleased)]
    [InlineData(FrameTypes.RequestEscalated)]
    public async Task A_row_returning_frame_upserts_a_row_the_client_never_held(string type)
    {
        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow));

        var observer = new RecordingObserver();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));

        var id = Guid.NewGuid();
        var row = ServerFrame.RequestRow(id, Guid.NewGuid());
        row["reason"] = "timeout";
        row["level"] = "deployment";
        row["unclaimed_s"] = 90;

        // The board has never seen this id: request.claimed made every non-claimant drop it.
        Assert.Empty(observer.Board);
        channel.Push(ServerFrame.Of(type, row, seq: 2));

        await Until(() => observer.Board.ContainsKey(id));
        await cts.CancelAsync();
        await run;

        var rendered = observer.Board[id];
        Assert.Equal("MTR-14", rendered.TicketCode);
        Assert.Equal("Bear", rendered.RequestedByCallsign);
        Assert.Single(rendered.Points);
        Assert.Equal("85.53", rendered.Points[0].X);
    }

    [Fact]
    public async Task Completed_with_outcome_unable_carries_the_row_back_onto_the_board()
    {
        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow));

        var observer = new RecordingObserver();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));

        var id = Guid.NewGuid();
        channel.Push(ServerFrame.Of(
            FrameTypes.RequestCompleted,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request_id"] = id,
                ["version"] = 5,
                ["outcome"] = "unable",
                ["duration_s"] = 30.0,
                ["request"] = ServerFrame.RequestRow(id, Guid.NewGuid()),
            },
            seq: 2));

        await Until(() => observer.Board.ContainsKey(id));
        await cts.CancelAsync();
        await run;

        Assert.Equal("Bear", observer.Board[id].RequestedByCallsign);
    }

    [Fact]
    public async Task The_clock_offset_is_re_derived_on_every_ready_including_after_a_reconnect()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);

        var first = new FakeWebSocketChannel();
        first.Push(Ready("s-1", now.AddSeconds(-10)));
        var second = new FakeWebSocketChannel();
        second.Push(Ready("s-2", now.AddSeconds(-30)));

        var observer = new RecordingObserver();
        var offset = new SystemClockOffset();
        var client = new RealtimeClient(
            Url,
            new FakeTicketSource(),
            new FakeChannelFactory(first, second),
            observer,
            new FakePresenceSource(),
            new FakeRevalidator(),
            offset,
            clock,
            new ReconnectPolicy(),
            new TestDelay(PresenceInterval));

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);

        await Until(() => offset.Offset == TimeSpan.FromSeconds(10));
        first.CloseWith(1000);

        await Until(() => offset.Offset == TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        await run;

        Assert.Equal(2, observer.Readies.Count);
    }

    [Fact]
    public async Task Deployment_entered_aborts_the_draft_then_clears_then_revalidates_with_the_frames_id()
    {
        var remembered = Guid.NewGuid();
        var arrived = Guid.NewGuid();

        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow, Guid.NewGuid(), remembered));

        var observer = new RecordingObserver();
        var revalidator = new FakeRevalidator();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _, revalidator: revalidator);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));
        await Until(() => revalidator.Calls.Count == 1);

        channel.Push(ServerFrame.Of(
            FrameTypes.DeploymentEntered,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["group_id"] = Guid.NewGuid(),
                ["deployment_id"] = arrived,
                ["label"] = "BRAVO",
                ["member_count"] = 18,
                ["source"] = "invite",
            },
            seq: 2));

        await Until(() => observer.Order.Contains("entered"));
        await Until(() => revalidator.Calls.Count == 2);
        await cts.CancelAsync();
        await run;

        var draft = observer.Order.IndexOf("draft:DeploymentChanged");
        var cleared = observer.Order.IndexOf("cleared:DeploymentEntered");
        var entered = observer.Order.IndexOf("entered");
        Assert.True(draft >= 0 && draft < cleared && cleared < entered);

        Assert.Equal(remembered, revalidator.Calls[0]);
        Assert.Equal(arrived, revalidator.Calls[^1]);
    }

    [Fact]
    public async Task An_empty_subscription_set_is_normal_and_seeds_nothing()
    {
        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow, groupId: null, deploymentId: null));

        var observer = new RecordingObserver();
        var revalidator = new FakeRevalidator();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _, revalidator: revalidator);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));
        await client.RevalidationInFlight;
        await cts.CancelAsync();
        await run;

        Assert.Empty(client.Subscriptions);
        Assert.Empty(revalidator.Calls);
        Assert.False(client.BoardMayBeStale);
        Assert.DoesNotContain(observer.Order, o => o.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stalled_outbox_on_the_heartbeat_is_a_different_fault_from_the_socket()
    {
        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow));

        var observer = new RecordingObserver();
        var client = Build(new FakeTicketSource(), new FakeChannelFactory(channel), observer, new TestDelay(PresenceInterval), out _);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));

        channel.Push(ServerFrame.Of(
            FrameTypes.Ping,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["outbox_drain_age_s"] = 120.0 },
            seq: 2));

        await Until(() => client.BoardMayBeStale);
        await Until(() => channel.SentOfType(FrameTypes.Pong).Count == 1);

        // The socket is up: the amber dot is not the signal here.
        Assert.Equal(RealtimeConnectionState.Connected, client.State);

        channel.Push(ServerFrame.Of(
            FrameTypes.Ping,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["outbox_drain_age_s"] = 1.0 },
            seq: 3));

        await Until(() => !client.BoardMayBeStale);
        await cts.CancelAsync();
        await run;

        Assert.Contains("stale:True", observer.Order);
    }

    [Fact]
    public async Task The_revalidation_fetch_is_jittered_independently_of_the_socket()
    {
        var draws = new Queue<double>([0.1, 0.9]);
        var policy = new ReconnectPolicy(jitter: draws.Dequeue);

        var channel = new FakeWebSocketChannel();
        channel.Push(Ready("s-1", DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid()));

        var delay = new TestDelay(PresenceInterval);
        var observer = new RecordingObserver();
        var client = new RealtimeClient(
            Url,
            new FakeTicketSource(),
            new FakeChannelFactory(channel),
            observer,
            new FakePresenceSource(),
            new FakeRevalidator(),
            new SystemClockOffset(),
            new FixedClock(DateTimeOffset.UtcNow),
            policy,
            delay);

        using var cts = new CancellationTokenSource(Timeout);
        var run = client.RunAsync(cts.Token);
        Assert.True(observer.ReadySignal.Wait(Timeout));

        // 0.1 of the 500 ms ceiling, drawn for the HTTPS fetch and nothing else.
        await Until(() => delay.Requested.Contains(TimeSpan.FromMilliseconds(50)));
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public void A_plaintext_realtime_url_is_refused()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new RealtimeClient(
            new Uri("ws://api.warcommand.app/v1/realtime"),
            new FakeTicketSource(),
            new FakeChannelFactory(),
            new RecordingObserver(),
            new FakePresenceSource(),
            new FakeRevalidator(),
            new SystemClockOffset()));

        Assert.Contains("plaintext", thrown.Message, StringComparison.Ordinal);
    }

    private static TimeSpan PresenceInterval => new RealtimeClientOptions().PresenceInterval;

    private static RealtimeClient Build(
        FakeTicketSource tickets,
        FakeChannelFactory factory,
        RecordingObserver observer,
        TestDelay delay,
        out SystemClockOffset offset,
        FakePresenceSource? presence = null,
        FakeRevalidator? revalidator = null)
    {
        offset = new SystemClockOffset();
        return new RealtimeClient(
            Url,
            tickets,
            factory,
            observer,
            presence ?? new FakePresenceSource(),
            revalidator ?? new FakeRevalidator(),
            offset,
            new FixedClock(DateTimeOffset.UtcNow),
            new ReconnectPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)),
            delay);
    }

    private static string Ready(string sessionId, DateTimeOffset serverTime, Guid? groupId = null, Guid? deploymentId = null)
    {
        var subscriptions = new List<object>();
        if (groupId is not null || deploymentId is not null)
        {
            subscriptions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["group_id"] = groupId ?? Guid.NewGuid(),
                ["deployment"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = deploymentId ?? Guid.NewGuid(),
                    ["label"] = "ALPHA",
                    ["member_count"] = 31,
                },
                ["topics"] = Topics,
            });
        }

        return ServerFrame.Of(
            FrameTypes.Ready,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["session_id"] = sessionId,
                ["config_version"] = 4,
                ["server_time"] = serverTime,
                ["another_device_on_board"] = false,
                ["subscriptions"] = subscriptions,
            },
            seq: 0);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Condition was never met.");
    }
}
