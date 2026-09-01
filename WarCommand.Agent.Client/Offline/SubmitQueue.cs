using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Offline;

/// <summary>Why a queued submit never reached the server.</summary>
public enum DropReason
{
    /// <summary>Its deployment is no longer current. Named on the overlay, never discarded silently.</summary>
    StaleDeployment,

    /// <summary>The server refused it for good, so retrying would only refuse it again.</summary>
    Refused,

    /// <summary>The queue was at its cap and this was the oldest item.</summary>
    QueueFull,
}

/// <summary>One submit that did not make it, with the text the overlay names it by.</summary>
public sealed record DroppedSubmit
{
    public required QueuedSubmit Item { get; init; }

    public required DropReason Reason { get; init; }

    /// <summary>The contract error code when the server refused it, otherwise null.</summary>
    public string? Code { get; init; }

    /// <summary>One line for the overlay. Never silent.</summary>
    public string Describe(DateTimeOffset now) => Reason switch
    {
        DropReason.StaleDeployment => string.Create(
            CultureInfo.InvariantCulture,
            $"DROPPED {Item.Body.TypeId.ToUpperInvariant()} - CAPTURED ON A MATCH YOU HAVE LEFT ({Item.AgeAt(now).TotalSeconds:F0}s AGO)"),
        DropReason.QueueFull => $"DROPPED {Item.Body.TypeId.ToUpperInvariant()} - OFFLINE QUEUE FULL",
        _ => $"DROPPED {Item.Body.TypeId.ToUpperInvariant()} - {Code ?? "REFUSED"}",
    };
}

/// <summary>What one drain did. Every item is in exactly one list.</summary>
public sealed record SubmitReplayResult
{
    public IReadOnlyList<RequestBody> Sent { get; init; } = [];

    /// <summary>Named on the overlay by the caller.</summary>
    public IReadOnlyList<DroppedSubmit> Dropped { get; init; } = [];

    /// <summary>Still queued: the failure was transient, so the next reconnect tries again.</summary>
    public IReadOnlyList<QueuedSubmit> Retained { get; init; } = [];

    public static SubmitReplayResult Empty { get; } = new();
}

/// <summary>
/// Submits queued to disk while the socket is down, replayed on reconnect.
/// </summary>
/// <remarks>
/// Claims are absent by construction rather than by convention: the queue takes
/// <see cref="QueuedSubmit"/>, the only <see cref="IOfflineDurable"/>, and a claim cannot satisfy
/// that interface without inventing an idempotency key and a captured deployment id it does not
/// have. A fire mission that arrives forty seconds late is usually still useful; a claim that
/// arrives forty seconds late takes work somebody else already finished.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a queue of submits, and 10-agent-spec.md calls it that.")]
public sealed class SubmitQueue
{
    private const string FileExtension = ".submit.json";

    private readonly AgentPaths _paths;
    private readonly IClock _clock;
    private readonly IClientLog _log;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, QueuedSubmit> _pending = new(StringComparer.Ordinal);

    /// <summary>Loads whatever survived the last run.</summary>
    public SubmitQueue(AgentPaths paths, IClock? clock = null, IClientLog? log = null, int capacity = 200)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _paths = paths;
        _clock = clock ?? SystemClock.Instance;
        _log = log ?? NullClientLog.Instance;
        _capacity = capacity;
        Load();
    }

    /// <summary>Oldest first, which is the order they replay in.</summary>
    public IReadOnlyList<QueuedSubmit> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Values.OrderBy(i => i.QueuedAt).ThenBy(i => i.IdempotencyKey, StringComparer.Ordinal)];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Persists a submit. Re-queuing the same idempotency key replaces the entry rather than
    /// doubling it, so a retry cannot become two fire missions.
    /// </summary>
    public IReadOnlyList<DroppedSubmit> Enqueue(QueuedSubmit item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var evicted = new List<DroppedSubmit>();

        lock (_gate)
        {
            _pending[item.IdempotencyKey] = item;
            Write(item);

            while (_pending.Count > _capacity)
            {
                var oldest = _pending.Values.OrderBy(i => i.QueuedAt).First();
                Forget(oldest);
                evicted.Add(new DroppedSubmit { Item = oldest, Reason = DropReason.QueueFull });
            }
        }

        return evicted;
    }

    /// <summary>Removes one entry without sending it.</summary>
    public void Remove(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_gate)
        {
            if (_pending.TryGetValue(idempotencyKey, out var item))
            {
                Forget(item);
            }
        }
    }

    /// <summary>
    /// Drains the queue against <paramref name="currentDeploymentId"/>. Anything captured on
    /// another match is dropped and named; the coordinate was read off a different map and the
    /// server would refuse it anyway with 409 deployment_mismatch.
    /// </summary>
    /// <param name="currentDeploymentId">
    /// The deployment the agent is on right now, from the last ready or deployment.entered frame.
    /// Null means the agent is on no deployment, so nothing queued is current.
    /// </param>
    /// <param name="send">Submits one item. Normally the API client's submit call.</param>
    /// <param name="cancellationToken">Stops the drain; whatever is left stays queued.</param>
    public async Task<SubmitReplayResult> ReplayAsync(
        Guid? currentDeploymentId,
        Func<QueuedSubmit, CancellationToken, Task<RequestBody>> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        var sent = new List<RequestBody>();
        var dropped = new List<DroppedSubmit>();
        var retained = new List<QueuedSubmit>();

        foreach (var item in Pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                retained.Add(item);
                continue;
            }

            if (currentDeploymentId is null || item.CapturedInDeploymentId != currentDeploymentId.Value)
            {
                lock (_gate)
                {
                    Forget(item);
                }

                dropped.Add(new DroppedSubmit { Item = item, Reason = DropReason.StaleDeployment });
                _log.Warn($"Dropped a queued {item.Body.TypeId}: captured on a match the agent has left.");
                continue;
            }

            try
            {
                var result = await send(item, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    Forget(item);
                }

                sent.Add(result);
            }
            catch (WarCommandApiException ex) when (ex.IsTransient)
            {
                var attempted = item with { Attempts = item.Attempts + 1 };
                lock (_gate)
                {
                    _pending[attempted.IdempotencyKey] = attempted;
                    Write(attempted);
                }

                retained.Add(attempted);
            }
            catch (WarCommandApiException ex)
            {
                lock (_gate)
                {
                    Forget(item);
                }

                dropped.Add(new DroppedSubmit { Item = item, Reason = DropReason.Refused, Code = ex.Code });
                _log.Warn($"Dropped a queued {item.Body.TypeId}: {ex.Code}.");
            }
        }

        return new SubmitReplayResult { Sent = sent, Dropped = dropped, Retained = retained };
    }

    /// <summary>Builds an entry with a fresh idempotency key and this machine's clock.</summary>
    public QueuedSubmit Create(Guid groupId, SubmitRequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new QueuedSubmit
        {
            IdempotencyKey = Guid.NewGuid().ToString("D"),
            GroupId = groupId,
            Body = body,
            QueuedAt = _clock.UtcNow,
        };
    }

    private string PathFor(QueuedSubmit item) =>
        Path.Combine(_paths.QueueDirectory, SafeName(item.IdempotencyKey) + FileExtension);

    private static string SafeName(string key) =>
        string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private void Write(QueuedSubmit item)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(item, AgentJson.Options);
        var path = PathFor(item);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void Forget(QueuedSubmit item)
    {
        _pending.Remove(item.IdempotencyKey);
        var path = PathFor(item);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void Load()
    {
        if (!Directory.Exists(_paths.QueueDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_paths.QueueDirectory, "*" + FileExtension))
        {
            try
            {
                var item = JsonSerializer.Deserialize<QueuedSubmit>(File.ReadAllText(path), AgentJson.Options);
                if (item is not null)
                {
                    _pending[item.IdempotencyKey] = item;
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (JsonException)
            {
                _log.Warn($"Discarded an unreadable queued submit: {Path.GetFileName(path)}");
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _log.Warn($"Could not read a queued submit: {ex.Message}");
            }
        }
    }
}
