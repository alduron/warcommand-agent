using System.Reflection;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Offline;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// Submits queue and replay. Claims do not, and that asymmetry is enforced by the type system
/// rather than by a comment somebody will delete.
/// </summary>
public class SubmitQueueTests
{
    [Fact]
    public async Task A_queued_submit_whose_deployment_is_no_longer_current_is_dropped_and_named()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var queue = new SubmitQueue(paths, clock);

        var current = Guid.NewGuid();
        var abandoned = Guid.NewGuid();
        var group = Guid.NewGuid();

        var live = queue.Create(group, Body(current));
        var stale = queue.Create(group, Body(abandoned));
        queue.Enqueue(live);
        queue.Enqueue(stale);
        Assert.Equal(2, queue.Count);

        var sent = new List<QueuedSubmit>();
        var result = await queue.ReplayAsync(
            current,
            (item, ct) =>
            {
                sent.Add(item);
                return Task.FromResult(Row(item));
            },
            CancellationToken.None);

        Assert.Single(result.Sent);
        Assert.Single(result.Dropped);
        Assert.Empty(result.Retained);
        Assert.Equal(stale.IdempotencyKey, result.Dropped[0].Item.IdempotencyKey);
        Assert.Equal(DropReason.StaleDeployment, result.Dropped[0].Reason);

        // Named on the overlay, never discarded silently.
        Assert.Contains("MATCH YOU HAVE LEFT", result.Dropped[0].Describe(clock.UtcNow), StringComparison.Ordinal);

        Assert.Single(sent);
        Assert.Equal(live.IdempotencyKey, sent[0].IdempotencyKey);
        Assert.Equal(0, queue.Count);
        Assert.Empty(Directory.GetFiles(paths.QueueDirectory));
    }

    [Fact]
    public async Task With_no_current_deployment_nothing_queued_is_current()
    {
        using var temp = new TempDirectory();
        var queue = new SubmitQueue(new AgentPaths(temp.Path), new FixedClock(DateTimeOffset.UtcNow));
        queue.Enqueue(queue.Create(Guid.NewGuid(), Body(Guid.NewGuid())));

        var result = await queue.ReplayAsync(
            currentDeploymentId: null,
            (_, _) => throw new InvalidOperationException("nothing may be sent"),
            CancellationToken.None);

        Assert.Single(result.Dropped);
        Assert.Equal(DropReason.StaleDeployment, result.Dropped[0].Reason);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_submit_for_the_next_reconnect()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var queue = new SubmitQueue(paths, new FixedClock(DateTimeOffset.UtcNow));
        var deployment = Guid.NewGuid();
        queue.Enqueue(queue.Create(Guid.NewGuid(), Body(deployment)));

        var result = await queue.ReplayAsync(
            deployment,
            (_, _) => throw new WarCommandApiException(new ApiError { Code = ErrorCodes.RateLimited, Status = 429 }),
            CancellationToken.None);

        Assert.Single(result.Retained);
        Assert.Equal(1, result.Retained[0].Attempts);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task A_refusal_drops_the_submit_and_names_the_code()
    {
        using var temp = new TempDirectory();
        var queue = new SubmitQueue(new AgentPaths(temp.Path), new FixedClock(DateTimeOffset.UtcNow));
        var deployment = Guid.NewGuid();
        queue.Enqueue(queue.Create(Guid.NewGuid(), Body(deployment)));

        var result = await queue.ReplayAsync(
            deployment,
            (_, _) => throw new WarCommandApiException(new ApiError { Code = ErrorCodes.DeploymentMismatch, Status = 409 }),
            CancellationToken.None);

        Assert.Single(result.Dropped);
        Assert.Equal(ErrorCodes.DeploymentMismatch, result.Dropped[0].Code);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void The_queue_survives_a_restart()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var deployment = Guid.NewGuid();

        var first = new SubmitQueue(paths, new FixedClock(DateTimeOffset.UtcNow));
        var item = first.Create(Guid.NewGuid(), Body(deployment));
        first.Enqueue(item);

        var second = new SubmitQueue(paths, new FixedClock(DateTimeOffset.UtcNow));
        Assert.Equal(1, second.Count);
        Assert.Equal(item.IdempotencyKey, second.Pending[0].IdempotencyKey);
        Assert.Equal(deployment, second.Pending[0].CapturedInDeploymentId);
    }

    [Fact]
    public void Only_a_submit_can_be_made_durable()
    {
        var durable = typeof(SubmitQueue).Assembly
            .GetTypes()
            .Where(t => typeof(IOfflineDurable).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToList();

        Assert.Equal([typeof(QueuedSubmit)], durable);

        // A claim carries a request id and a version. It has no idempotency key and no captured
        // deployment id, so it cannot satisfy the interface the queue accepts.
        var claim = typeof(RequestClaimCommand);
        Assert.False(typeof(IOfflineDurable).IsAssignableFrom(claim));
        Assert.Null(claim.GetProperty("IdempotencyKey"));
        Assert.Null(claim.GetProperty("CapturedInDeploymentId"));

        var accepts = typeof(SubmitQueue)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(claim, accepts);
    }

    [Fact]
    public void The_socket_cannot_reach_the_queue()
    {
        var fields = typeof(RealtimeClient)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType)
            .ToList();

        Assert.DoesNotContain(typeof(SubmitQueue), fields);
        Assert.DoesNotContain(typeof(IOfflineDurable), fields);
    }

    private static SubmitRequestBody Body(Guid deploymentId) => new()
    {
        TypeId = "mortar_fire",
        CapturedInDeploymentId = deploymentId,
        Points =
        [
            new PointBody { Ordinal = 0, Label = "target", X = "85.53", Y = "69.42", Source = "spoken_grid" },
        ],
        ClientSubmittedAt = DateTimeOffset.UtcNow,
    };

    private static RequestBody Row(QueuedSubmit item) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = item.GroupId,
        DeploymentId = item.CapturedInDeploymentId,
        TicketCode = "MTR-1",
        TypeId = item.Body.TypeId,
        TargetRoleIds = ["mortar"],
        State = RequestState.Open,
        Priority = Priority.Normal,
        RequestedByParticipantId = Guid.NewGuid(),
        RequestedByCallsign = "Ghost",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        CreatedAt = DateTimeOffset.UtcNow,
        Version = 1,
        Points = item.Body.Points,
    };
}
