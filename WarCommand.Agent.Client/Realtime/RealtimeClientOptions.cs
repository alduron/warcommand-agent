using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Client.Realtime;

/// <summary>What the agent reports on the heartbeat. Read at each tick, never cached.</summary>
public interface IPresenceSource
{
    /// <summary>
    /// A report, never an instruction. Omitting an id releases nothing; the server answers a
    /// divergence with claims.reconcile.
    /// </summary>
    IReadOnlyList<Guid> ClaimedRequestIds { get; }

    /// <summary>Idle sustained past one grace interval releases claims with reason not_in_game.</summary>
    PresenceState State { get; }

    /// <summary>Null means the detector has no answer right now. It never moves anybody.</summary>
    string? ServerKey { get; }
}

/// <summary>
/// Re-seeds the board over HTTPS with <c>GET /v1/deployments/{id}/board</c>, the only seed there is.
/// The socket calls it with an id it took from a frame, never a remembered one.
/// </summary>
public interface IBoardRevalidator
{
    Task RevalidateAsync(Guid deploymentId, CancellationToken cancellationToken);
}

/// <summary>Timings the protocol fixes, and the two it does not.</summary>
public sealed class RealtimeClientOptions
{
    /// <summary>Every 15 s, per the lifecycle.</summary>
    public TimeSpan PresenceInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The server pings every 20 s and drops an idle connection at 90 s.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Per-connection send buffer. The server bounds its own at 256 and drops a slow consumer with
    /// 4008; queueing more here than the server will hold buys nothing.
    /// </summary>
    public int SendBufferSize { get; init; } = 256;

    /// <summary>
    /// Outbox drain age past which the overlay reads BOARD MAY BE STALE. A local default, because
    /// the ping frame carries the age and not a threshold; a stream.degraded frame that carries one
    /// replaces it.
    /// </summary>
    public double OutboxDrainStaleSeconds { get; init; } = 30;

    /// <summary>4004 is retried once and then abandoned, per the close code table.</summary>
    public int MaxProtocolViolations { get; init; } = 1;
}
