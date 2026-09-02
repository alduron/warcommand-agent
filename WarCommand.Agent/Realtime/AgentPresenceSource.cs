using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Realtime;

/// <summary>
/// What the agent reports on the 15 second heartbeat: which rows it holds, whether the game is up,
/// and which server it thinks it is on.
/// </summary>
/// <remarks>
/// A report, never an instruction. Omitting a claim releases nothing: the server answers a
/// divergence with <c>claims.reconcile</c> and the database wins.
/// <para>
/// Read at each tick and never cached, so a claim taken a moment ago is in the next heartbeat.
/// </para>
/// </remarks>
public sealed class AgentPresenceSource : IPresenceSource
{
    private readonly Func<IReadOnlyList<Guid>> _claimed;
    private readonly Func<bool> _gameRunning;

    /// <summary>Creates the source over the two live readings it reports.</summary>
    public AgentPresenceSource(Func<IReadOnlyList<Guid>> claimed, Func<bool> gameRunning)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(gameRunning);

        _claimed = claimed;
        _gameRunning = gameRunning;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> ClaimedRequestIds => _claimed();

    /// <summary>
    /// Idle once the game is gone. Without it the game dies, the agent lives, heartbeats keep
    /// flowing, and providers read a claim on a mission nobody is running.
    /// </summary>
    public PresenceState State => _gameRunning() ? PresenceState.InGame : PresenceState.Idle;

    /// <summary>
    /// Null: no server detector exists yet. Null means the detector has no answer right now, which
    /// is the honest reading, and it never moves anybody.
    /// </summary>
    public string? ServerKey => null;
}
