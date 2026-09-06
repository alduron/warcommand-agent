using System.Windows.Threading;
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
/// <para>
/// The seed runs on the dispatcher. The socket schedules revalidation with Task.Run, and the seed
/// mutates BoardState and calls the presenter, neither of which leaves the UI thread anywhere else
/// in the agent. Off the dispatcher it raced the frame handlers over a plain Dictionary and threw
/// on the first WPF touch, into a Task whose catch filter names only cancellation and API errors,
/// so the socket's own recovery from a hop or a resync died in silence every time.
/// </para>
/// </remarks>
public sealed class HttpBoardRevalidator : IBoardRevalidator
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<Guid, CancellationToken, Task> _seed;

    /// <summary>Creates the revalidator over the composition root's own seed path.</summary>
    public HttpBoardRevalidator(Dispatcher dispatcher, Func<Guid, CancellationToken, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(seed);
        _dispatcher = dispatcher;
        _seed = seed;
    }

    /// <inheritdoc />
    public Task RevalidateAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            return _seed(deploymentId, cancellationToken);
        }

        return _dispatcher
            .InvokeAsync(() => _seed(deploymentId, cancellationToken), DispatcherPriority.Normal, cancellationToken)
            .Task
            .Unwrap();
    }
}
