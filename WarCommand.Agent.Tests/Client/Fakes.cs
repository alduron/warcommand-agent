using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Http;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Client.Tokens;
using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Client;

/// <summary>Records every line, so a test can assert a credential never reached one.</summary>
internal sealed class RecordingLog : IClientLog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Info(string message) => _lines.Enqueue(message);

    public void Warn(string message) => _lines.Enqueue(message);

    public void Error(string message, Exception? error = null) =>
        _lines.Enqueue(error is null ? message : $"{message} {error}");
}

internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }
}

/// <summary>
/// Completes instantly except for the durations it is told to park on, so a presence or reconnect
/// timer can be held still while the rest of the client runs.
/// </summary>
internal sealed class TestDelay : IAsyncDelay
{
    private readonly ConcurrentQueue<TimeSpan> _requested = new();

    public TestDelay(params TimeSpan[] park) => Park = [.. park];

    public IReadOnlyList<TimeSpan> Park { get; }

    public IReadOnlyList<TimeSpan> Requested => [.. _requested];

    public async Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        _requested.Enqueue(duration);
        if (Park.Contains(duration))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A socket a test drives: push inbound frames, read what was sent, close with a code.</summary>
internal sealed class FakeWebSocketChannel : IWebSocketChannel
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<string> _sent = new();

    public Uri? ConnectedTo { get; private set; }

    public int? CloseCode { get; private set; }

    public string? CloseDescription { get; private set; }

    /// <summary>Closed with this code the moment the client connects.</summary>
    public int? CloseOnConnect { get; init; }

    public IReadOnlyList<string> Sent => [.. _sent];

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedTo = uri;
        if (CloseOnConnect is { } code)
        {
            CloseWith(code);
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task CloseAsync(int code, string reason, CancellationToken cancellationToken)
    {
        CloseWith(code, reason);
        return Task.CompletedTask;
    }

    public void Push(string frame) => _inbound.Writer.TryWrite(frame);

    public void CloseWith(int code, string? reason = null)
    {
        CloseCode = code;
        CloseDescription = reason;
        _inbound.Writer.TryComplete();
    }

    public void Dispose()
    {
    }

    /// <summary>Frames of one type, as parsed JSON.</summary>
    public IReadOnlyList<JsonElement> SentOfType(string type)
    {
        var matches = new List<JsonElement>();
        foreach (var raw in Sent)
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("type", out var t) && t.GetString() == type)
            {
                matches.Add(document.RootElement.Clone());
            }
        }

        return matches;
    }
}

internal sealed class FakeChannelFactory : IWebSocketChannelFactory
{
    private readonly Queue<FakeWebSocketChannel> _queued;

    public FakeChannelFactory(params FakeWebSocketChannel[] channels) => _queued = new Queue<FakeWebSocketChannel>(channels);

    public List<FakeWebSocketChannel> Created { get; } = [];

    public IWebSocketChannel Create()
    {
        var channel = _queued.Count > 0 ? _queued.Dequeue() : new FakeWebSocketChannel();
        Created.Add(channel);
        return channel;
    }
}

internal sealed class FakeTicketSource : IRealtimeTicketSource
{
    public int Calls { get; private set; }

    public Task<RealtimeTicket> AcquireRealtimeTicketAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new RealtimeTicket { Ticket = "rt_test", ExpiresIn = 30 });
    }
}

internal sealed class FakePresenceSource : IPresenceSource
{
    public List<Guid> Claims { get; } = [];

    public IReadOnlyList<Guid> ClaimedRequestIds => Claims;

    public PresenceState State { get; set; } = PresenceState.InGame;

    public string? ServerKey { get; set; }
}

internal sealed class FakeRevalidator : IBoardRevalidator
{
    private readonly ConcurrentQueue<Guid> _calls = new();

    public IReadOnlyList<Guid> Calls => [.. _calls];

    public Task RevalidateAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        _calls.Enqueue(deploymentId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A board built only from what the socket delivers, so a row-returning frame has to carry the
/// whole row for this to render anything.
/// </summary>
internal sealed class RecordingObserver : IRealtimeObserver
{
    public Dictionary<Guid, RequestBody> Board { get; } = [];

    public List<string> Order { get; } = [];

    public List<ReadyPayload> Readies { get; } = [];

    public bool DeviceRevoked { get; private set; }

    public bool Stale { get; private set; }

    public bool AnotherDevice { get; private set; }

    public ManualResetEventSlim ReadySignal { get; } = new(false);

    public void OnReady(ReadyPayload payload)
    {
        Readies.Add(payload);
        Order.Add("ready");
        ReadySignal.Set();
    }

    public void OnRequestSubmitted(RequestSubmittedPayload payload)
    {
        Board[payload.Id] = payload;
        Order.Add("submitted");
    }

    public void OnRequestClaimed(RequestClaimedPayload payload)
    {
        // Every non-claimant drops the row. This is what makes an id-only reopen unrenderable.
        Board.Remove(payload.RequestId);
        Order.Add("claimed");
    }

    public void OnRequestReleased(RequestReleasedPayload payload)
    {
        Board[payload.Id] = payload;
        Order.Add("released");
    }

    public void OnRequestCompleted(RequestCompletedPayload payload)
    {
        if (payload.ReturnsToOpen)
        {
            Board[payload.RequestId] = payload.ReopenedRow;
        }
        else
        {
            Board.Remove(payload.RequestId);
        }

        Order.Add("completed");
    }

    public void OnRequestEscalated(RequestEscalatedPayload payload)
    {
        Board[payload.Id] = payload;
        Order.Add("escalated");
    }

    public void OnClaimsReconcile(ClaimsReconcilePayload payload)
    {
        foreach (var row in payload.Requests)
        {
            Board[row.Id] = row;
        }

        Order.Add("reconcile");
    }

    public void OnPendingDraftAborted(DraftAbortReason reason) => Order.Add($"draft:{reason}");

    public void OnBoardCleared(BoardClearReason reason)
    {
        Board.Clear();
        Order.Add($"cleared:{reason}");
    }

    public void OnDeploymentEntered(DeploymentEnteredPayload payload) => Order.Add("entered");

    public void OnDeploymentClosed(DeploymentClosedPayload payload) => Order.Add("closed");

    public void OnBoardStalenessChanged(bool stale, double drainAgeSeconds)
    {
        Stale = stale;
        Order.Add($"stale:{stale}");
    }

    public void OnAnotherDeviceOnBoard(bool present) => AnotherDevice = present;

    public void OnDeviceRevoked()
    {
        DeviceRevoked = true;
        Order.Add("revoked");
    }

    public void OnUnknownFrame(Envelope envelope) => Order.Add($"unknown:{envelope.Type}");
}

/// <summary>Reversible, so a store round trip is testable without touching the user's DPAPI keys.</summary>
internal sealed class XorProtector : ITokenProtector
{
    public byte[] Protect(byte[] plaintext) => Flip(plaintext);

    public byte[] Unprotect(byte[] ciphertext) => Flip(ciphertext);

    private static byte[] Flip(byte[] input)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = (byte)(input[i] ^ 0x5A);
        }

        return output;
    }
}

/// <summary>A directory that cleans itself up.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warcommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Serialises a server frame the way the API would.</summary>
internal static class ServerFrame
{
    public static string Of(string type, object payload, long? seq = null)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["type"] = type,
            ["schema_version"] = 1,
            ["sent_at"] = DateTimeOffset.UtcNow,
            ["payload"] = payload,
        };

        if (seq is { } value)
        {
            envelope["seq"] = value;
        }

        return JsonSerializer.Serialize(envelope, AgentJson.Options);
    }

    /// <summary>A whole request row, the shape every row-returning frame carries.</summary>
    public static Dictionary<string, object?> RequestRow(Guid id, Guid deploymentId, string state = "open")
    {
        var now = DateTimeOffset.UtcNow;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["group_id"] = Guid.NewGuid(),
            ["deployment_id"] = deploymentId,
            ["ticket_code"] = "MTR-14",
            ["type_id"] = "mortar_fire",
            ["target_role_ids"] = new[] { "mortar" },
            ["state"] = state,
            ["priority"] = "urgent",
            ["modifiers"] = Array.Empty<string>(),
            ["note"] = null,
            ["requested_by_participant_id"] = Guid.NewGuid(),
            ["requested_by_callsign"] = "Bear",
            ["co_requester_count"] = 0,
            ["release_count"] = 1,
            ["expires_at"] = now.AddMinutes(5),
            ["created_at"] = now,
            ["version"] = 4,
            ["points"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ordinal"] = 0,
                    ["label"] = "target",
                    ["x"] = "85.53",
                    ["y"] = "69.42",
                    ["source"] = "map_readout",
                    ["raw_text"] = "x85.53 y69.42",
                    ["confidence"] = 0.97m,
                },
            },
        };
    }
}
