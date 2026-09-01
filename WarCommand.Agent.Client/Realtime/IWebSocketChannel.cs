using System.Net.WebSockets;
using System.Text;
using WarCommand.Agent.Client.Http;

namespace WarCommand.Agent.Client.Realtime;

/// <summary>
/// One WebSocket connection, reduced to whole text messages so a test can drive the protocol with
/// no socket. Fragment reassembly belongs to the implementation.
/// </summary>
public interface IWebSocketChannel : IDisposable
{
    /// <summary>The close code the peer sent, once the connection has ended.</summary>
    int? CloseCode { get; }

    string? CloseDescription { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Sends one complete text frame.</summary>
    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>One complete message, or null once the peer has closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(int code, string reason, CancellationToken cancellationToken);
}

/// <summary>Creates a channel per connection attempt. A channel is never reconnected.</summary>
public interface IWebSocketChannelFactory
{
    IWebSocketChannel Create();
}

/// <summary>The real socket.</summary>
public sealed class ClientWebSocketChannel : IWebSocketChannel
{
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[ReceiveBufferSize];

    public int? CloseCode => _socket.CloseStatus is { } status ? (int)status : null;

    public string? CloseDescription => _socket.CloseStatusDescription;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        // wss only. A ticket in a ws:// query string is a credential in cleartext.
        return _socket.ConnectAsync(
            TransportSecurity.RequireSecureWebSocket(uri, nameof(uri)),
            cancellationToken);
    }

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            text.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return text.ToString();
            }
        }
    }

    public Task CloseAsync(int code, string reason, CancellationToken cancellationToken) =>
        _socket.CloseAsync((WebSocketCloseStatus)code, reason, cancellationToken);

    public void Dispose() => _socket.Dispose();
}

/// <summary>Builds real sockets.</summary>
public sealed class ClientWebSocketChannelFactory : IWebSocketChannelFactory
{
    public static ClientWebSocketChannelFactory Instance { get; } = new();

    public IWebSocketChannel Create() => new ClientWebSocketChannel();
}
