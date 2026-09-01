using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarCommand.Agent.Client.Diagnostics;

namespace WarCommand.Agent.Client.Link;

/// <summary>What the page learns before it decides whether to hand over a ticket.</summary>
public sealed record LocalPairingHello
{
    [JsonPropertyName("agent")]
    public bool Agent { get; init; } = true;

    [JsonPropertyName("paired")]
    public required bool Paired { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The account the agent holds, so a page can tell whether it matches its own.</summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    /// <summary>This device's registration, so the devices page can mark which row is this machine.</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }
}

/// <summary>Settings for the loopback listener. Origins are matched exactly, never by suffix.</summary>
public sealed record LocalPairingOptions
{
    /// <summary>First port tried. Three are tried in turn, so two agents on one machine still bind.</summary>
    public const int FirstPort = 47821;

    /// <summary>How many consecutive ports to try.</summary>
    public const int PortCount = 3;

    /// <summary>
    /// Exact origins allowed to talk to this listener. A suffix match would accept
    /// <c>https://warcommand.app.evil.test</c>, so this list is compared whole.
    /// </summary>
    public required IReadOnlyList<string> AllowedOrigins { get; init; }

    public required string AgentVersion { get; init; }
}

/// <summary>
/// The zero-step link. A page open in a browser on this machine finds the agent on loopback and
/// hands it a pairing ticket, so linking costs the user no click, no code and no browser prompt.
/// </summary>
/// <remarks>
/// <para>
/// This is not a way into the agent. It binds 127.0.0.1 only, answers two routes, runs only while
/// the agent is unpaired, and stops the moment it is. The ticket it accepts is worthless on its own:
/// it is redeemed against the API, which decides whose account it belongs to.
/// </para>
/// <para>
/// A hostile page cannot use it. The origin is checked against an exact allowlist, and both routes
/// require a JSON content type or a GET, so the browser must send a preflight the allowlist can
/// refuse. A page that is not on the list gets 403 and no CORS headers, so it cannot even read the
/// answer to <c>hello</c>.
/// </para>
/// </remarks>
public sealed class LocalPairingListener : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly LocalPairingOptions _options;
    private readonly Func<string, CancellationToken, Task> _redeem;
    private readonly Func<string?> _currentUserId;
    private readonly Func<string?> _currentDeviceId;
    private readonly IClientLog _log;
    private readonly CancellationTokenSource _stopping = new();

    private HttpListener? _listener;
    private Task? _loop;

    public LocalPairingListener(
        LocalPairingOptions options,
        Func<string, CancellationToken, Task> redeem,
        Func<string?> currentUserId,
        IClientLog? log = null,
        Func<string?>? currentDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redeem);
        ArgumentNullException.ThrowIfNull(currentUserId);

        _options = options;
        _redeem = redeem;
        _currentUserId = currentUserId;
        _currentDeviceId = currentDeviceId ?? (static () => null);
        _log = log ?? NullClientLog.Instance;
    }

    /// <summary>The port that bound, or null when every candidate was taken.</summary>
    public int? Port { get; private set; }

    /// <summary>Raised after a ticket is redeemed, so the host can stop waiting.</summary>
    public event EventHandler? Paired;

    /// <summary>
    /// Binds the first free port in the range. Failure is not fatal: the deep link and the typed
    /// code both still work, so a machine that will not let anything listen just costs a click.
    /// </summary>
    public bool Start()
    {
        for (var offset = 0; offset < LocalPairingOptions.PortCount; offset++)
        {
            var port = LocalPairingOptions.FirstPort + offset;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            Port = port;
            _loop = Task.Run(() => AcceptAsync(_stopping.Token));
            _log.Info($"Local pairing listener on 127.0.0.1:{port}.");
            return true;
        }

        _log.Warn("No loopback port was free: pairing falls back to the deep link or a typed code.");
        return false;
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _log.Warn($"Local pairing request failed: {ex.GetType().Name}");
                TryFail(context);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var origin = request.Headers["Origin"];

        // No origin header at all is a non-browser caller: curl, a script, anything local. It gets
        // nothing, because the only client this exists for is a page.
        if (origin is null || !_options.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            await WriteAsync(context, HttpStatusCode.Forbidden, null, origin: null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.Ordinal))
        {
            await WriteAsync(context, HttpStatusCode.NoContent, null, origin, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = request.Url?.AbsolutePath ?? string.Empty;

        if (string.Equals(path, "/wc/v1/hello", StringComparison.Ordinal)
            && string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
        {
            var userId = _currentUserId();
            var hello = new LocalPairingHello
            {
                Paired = userId is not null,
                Version = _options.AgentVersion,
                UserId = userId,
                DeviceId = _currentDeviceId(),
            };
            await WriteAsync(context, HttpStatusCode.OK, JsonSerializer.Serialize(hello, Json), origin, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/wc/v1/pair", StringComparison.Ordinal)
            && string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
        {
            await PairAsync(context, origin, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, HttpStatusCode.NotFound, null, origin, cancellationToken).ConfigureAwait(false);
    }

    private async Task PairAsync(HttpListenerContext context, string origin, CancellationToken cancellationToken)
    {
        // A JSON content type forces the browser to preflight, so a form post from a page that is
        // not on the allowlist never reaches this method at all.
        if (context.Request.ContentType is not { } contentType
            || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context, HttpStatusCode.UnsupportedMediaType, null, origin, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        string? ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<PairBody>(body, Json)?.Ticket;
        }
        catch (JsonException)
        {
            ticket = null;
        }

        if (string.IsNullOrWhiteSpace(ticket))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, null, origin, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _redeem(ticket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Never the ticket, never the exception message: both can carry the credential.
            _log.Warn($"A handed-over ticket did not redeem: {ex.GetType().Name}");
            await WriteAsync(context, HttpStatusCode.BadRequest, """{"paired":false}""", origin, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _log.Info("Paired from the browser on this machine. No user action was needed.");
        await WriteAsync(context, HttpStatusCode.OK, """{"paired":true}""", origin, cancellationToken)
            .ConfigureAwait(false);
        Paired?.Invoke(this, EventArgs.Empty);
    }

    private static async Task WriteAsync(
        HttpListenerContext context, HttpStatusCode status, string? json, string? origin,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = (int)status;

        if (origin is not null)
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            response.Headers["Access-Control-Max-Age"] = "600";
            response.Headers["Vary"] = "Origin";
        }

        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    private static void TryFail(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
        }
        catch (ObjectDisposedException)
        {
            // The response was already closed by the handler that threw.
        }
        catch (HttpListenerException)
        {
            // The peer is gone.
        }
    }

    public void Dispose()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }

        _listener?.Close();
        _listener = null;
        Port = null;

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The accept loop was torn down mid-await.
        }

        _stopping.Dispose();
    }

    private sealed record PairBody
    {
        public string? Ticket { get; init; }

        /// <summary>A ticket is a credential. The generated ToString would print it.</summary>
        public override string ToString() => "PairBody { Ticket = <redacted> }";
    }
}
