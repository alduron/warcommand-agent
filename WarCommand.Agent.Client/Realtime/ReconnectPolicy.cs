namespace WarCommand.Agent.Client.Realtime;

/// <summary>A pause. Injected so a test never sleeps.</summary>
public interface IAsyncDelay
{
    Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The real timer.</summary>
public sealed class SystemDelay : IAsyncDelay
{
    public static SystemDelay Instance { get; } = new();

    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(duration, cancellationToken);
}

/// <summary>
/// Exponential backoff with FULL jitter, base 500 ms, cap 30 s: the delay is drawn uniformly from
/// zero to the ceiling rather than sitting on it.
/// </summary>
/// <remarks>
/// Both legs are jittered and they are drawn independently. Every deploy, and every Redis restart,
/// disconnects every agent at once. Jittering only the socket spreads the reconnects and then lands
/// the whole fleet on Postgres at the same instant, because each agent revalidates its board over
/// HTTPS the moment its own ready arrives.
/// </remarks>
public sealed class ReconnectPolicy
{
    private readonly Func<double> _jitter;

    /// <param name="baseDelay">The first ceiling. 500 ms.</param>
    /// <param name="cap">The largest ceiling. 30 s.</param>
    /// <param name="jitter">Draws in [0,1). Injected so the distribution is testable.</param>
    public ReconnectPolicy(TimeSpan? baseDelay = null, TimeSpan? cap = null, Func<double>? jitter = null)
    {
        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
        Cap = cap ?? TimeSpan.FromSeconds(30);
        _jitter = jitter ?? DefaultJitter;
    }

    public TimeSpan BaseDelay { get; }

    public TimeSpan Cap { get; }

    /// <summary>The socket reconnect delay for <paramref name="attempt"/>, counted from zero.</summary>
    public TimeSpan NextSocketDelay(int attempt) => Draw(attempt);

    /// <summary>
    /// The delay before the HTTPS board revalidation. A separate draw from
    /// <see cref="NextSocketDelay"/> on purpose: sharing one value would re-synchronise the fleet
    /// on the database after spreading it on the socket.
    /// </summary>
    public TimeSpan NextRevalidateDelay(int attempt) => Draw(attempt);

    /// <summary>The upper bound a draw at <paramref name="attempt"/> can return.</summary>
    public TimeSpan Ceiling(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var shift = Math.Min(attempt, 30);
        var ticks = BaseDelay.Ticks * (1L << shift);
        return ticks <= 0 || ticks > Cap.Ticks ? Cap : TimeSpan.FromTicks(ticks);
    }

    private TimeSpan Draw(int attempt) => TimeSpan.FromTicks((long)(Ceiling(attempt).Ticks * _jitter()));

    // Jitter spreads load; it is not a secret and does not need a CSPRNG.
#pragma warning disable CA5394
    private static double DefaultJitter() => Random.Shared.NextDouble();
#pragma warning restore CA5394
}
