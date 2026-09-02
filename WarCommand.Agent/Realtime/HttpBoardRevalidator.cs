using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Realtime;

namespace WarCommand.Agent.Realtime;

/// <summary>
/// Re-seeds the board over HTTPS with <c>GET /v1/deployments/{id}/board</c>, the only seed there is.
/// </summary>
/// <remarks>
/// Called by the socket with an id it took from a frame, never a remembered one. The unfiltered
/// form is deliberate: <c>?state=open</c> hides an agent's own claims after a restart, so it would
/// come back from a resync holding rows the board does not show.
/// </remarks>
public sealed class HttpBoardRevalidator : IBoardRevalidator
{
    private readonly Func<Guid, CancellationToken, Task> _seed;

    /// <summary>Creates the revalidator over the composition root's own seed path.</summary>
    public HttpBoardRevalidator(Func<Guid, CancellationToken, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _seed = seed;
    }

    /// <inheritdoc />
    public Task RevalidateAsync(Guid deploymentId, CancellationToken cancellationToken) =>
        _seed(deploymentId, cancellationToken);
}
