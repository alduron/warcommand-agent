using WarCommand.Agent.Client.Http;

namespace WarCommand.Agent.Client.Offline;

/// <summary>
/// Work that may survive a disconnect and be replayed later.
/// </summary>
/// <remarks>
/// The two members are the whole gate, and they are why a claim can never be queued. A durable item
/// must name an idempotency key, so a replay is a replay and not a second request, and it must name
/// the deployment its coordinate was read in, so a drain onto a different match can be refused. A
/// claim has neither: it carries a request id and a version, it is meaningful only at the instant it
/// is made, and replaying a stale one takes a mission somebody else already handled. Widening this
/// interface to fit a claim is the change this type exists to stop.
/// </remarks>
public interface IOfflineDurable
{
    /// <summary>Retained by the server for 24 hours. A replay returns the original response.</summary>
    string IdempotencyKey { get; }

    /// <summary>Compared by the server, never re-stamped. Mismatched submits are refused.</summary>
    Guid CapturedInDeploymentId { get; }

    /// <summary>Local instant it was queued. The requester's pinned row shows its age.</summary>
    DateTimeOffset QueuedAt { get; }
}

/// <summary>
/// One submit waiting for the socket to come back. The only implementation of
/// <see cref="IOfflineDurable"/> in the agent, and the only thing the queue accepts.
/// </summary>
public sealed record QueuedSubmit : IOfflineDurable
{
    public required string IdempotencyKey { get; init; }

    public required Guid GroupId { get; init; }

    public required SubmitRequestBody Body { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }

    /// <summary>Drain attempts so far. A retained item is tried again on the next reconnect.</summary>
    public int Attempts { get; init; }

    /// <summary>Taken from the body, so the two can never disagree.</summary>
    public Guid CapturedInDeploymentId => Body.CapturedInDeploymentId;

    /// <summary>How long the requester has been waiting, for the pinned row.</summary>
    public TimeSpan AgeAt(DateTimeOffset now) => now - QueuedAt;
}
