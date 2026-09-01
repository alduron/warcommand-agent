using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Core.Abstractions;

namespace WarCommand.Agent.Client.Tokens;

/// <summary>
/// Hands the HTTP client a live agent token, refreshing it once, under a single flight, when it is
/// inside the margin of expiry.
/// </summary>
/// <remarks>
/// The refresh call is passed as a delegate rather than the API client, because the API client
/// takes this type: the composition root closes over the client it is building.
/// </remarks>
public sealed class RefreshingAgentTokenSource : IAgentTokenSource, IDisposable
{
    private readonly ITokenStore _store;
    private readonly Func<string, CancellationToken, Task<TokenPair>> _refresh;
    private readonly IClock _clock;
    private readonly IClientLog _log;
    private readonly TimeSpan _margin;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RefreshingAgentTokenSource(
        ITokenStore store,
        Func<string, CancellationToken, Task<TokenPair>> refresh,
        IClock? clock = null,
        IClientLog? log = null,
        TimeSpan? margin = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(refresh);
        _store = store;
        _refresh = refresh;
        _clock = clock ?? SystemClock.Instance;
        _log = log ?? NullClientLog.Instance;
        _margin = margin ?? TimeSpan.FromMinutes(2);
    }

    public async ValueTask<string> GetAgentTokenAsync(CancellationToken cancellationToken)
    {
        var current = _store.Current
            ?? throw new InvalidOperationException("This device holds no agent token: it is unpaired.");

        if (!current.NeedsRefresh(_clock.UtcNow, _margin))
        {
            return current.AgentToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _store.Current
                ?? throw new InvalidOperationException("This device holds no agent token: it is unpaired.");
            if (!current.NeedsRefresh(_clock.UtcNow, _margin))
            {
                return current.AgentToken;
            }

            var presented = _store.BeginRotation();
            var pair = await _refresh(presented, cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            _store.CompleteRotation(presented, new AgentTokens
            {
                AgentToken = pair.AgentToken,
                RefreshToken = pair.RefreshToken,
                ExpiresAt = pair.ExpiresIn is { } seconds ? now.AddSeconds(seconds) : null,
                UpdatedAt = now,
            });

            _log.Info("Agent token refreshed.");
            return pair.AgentToken;
        }
        catch (WarCommandApiException ex) when (ex.Code is ErrorCodes.DeviceRevoked)
        {
            _store.Clear("the device was revoked");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
