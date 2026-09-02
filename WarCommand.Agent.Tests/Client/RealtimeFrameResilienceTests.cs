using System.Threading;
using System.Threading.Tasks;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// A frame is not worth a connection.
/// </summary>
/// <remarks>
/// FrameCodec.TryRead only guards the envelope; PayloadAs deserializes lazily, so a payload this
/// build cannot read threw out of the receive pump and out of the connect loop, and the socket
/// stayed dead for the rest of the session. It happened on the very first frame the agent ever
/// received in production.
/// </remarks>
public class RealtimeFrameResilienceTests
{
    /// <summary>The exact bytes that killed it: str(datetime), with a space where ISO needs a T.</summary>
    private const string ReadyWithUnparsableTime =
        """
        {"message_id":"9f1d0f2e-2b5e-4a1a-9c7f-1f0b2a3c4d5e","type":"ready","seq":0,
        "sent_at":"2026-09-02T03:50:53.259438+00:00","payload":{
        "session_id":"1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f","config_version":1,
        "server_time":"2026-09-02 03:50:53.259438+00:00","another_device_on_board":false,
        "subscriptions":[]}}
        """;

    [Fact]
    public void A_payload_this_build_cannot_read_does_not_kill_the_socket()
    {
        var observer = new CountingObserver();
        var client = Build(observer);

        var dispatch = typeof(RealtimeClient).GetMethod(
            "Dispatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // Throws before the fix. The frame is dropped, the connection is not.
        dispatch.Invoke(client, [ReadyWithUnparsableTime.ReplaceLineEndings(string.Empty)]);

        Assert.Equal(0, observer.Readies);
    }

    [Fact]
    public void A_readable_ready_still_lands()
    {
        var observer = new CountingObserver();
        var client = Build(observer);

        var dispatch = typeof(RealtimeClient).GetMethod(
            "Dispatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        dispatch.Invoke(client, [ReadyWithUnparsableTime
            .ReplaceLineEndings(string.Empty)
            .Replace("2026-09-02 03:50:53.259438+00:00", "2026-09-02T03:50:53.259438+00:00", StringComparison.Ordinal)]);

        Assert.Equal(1, observer.Readies);
    }

    private static RealtimeClient Build(IRealtimeObserver observer) => new(
        new Uri("wss://api.warcommand.app/v1/realtime"),
        new FakeTicketSource(),
        new FakeChannelFactory(),
        observer,
        new FakePresence(),
        new FakeRevalidator(),
        new WarCommand.Agent.Core.Abstractions.SystemClockOffset());

    private sealed class CountingObserver : IRealtimeObserver
    {
        public int Readies { get; private set; }

        public void OnReady(ReadyPayload payload) => Readies++;
    }

    private sealed class FakePresence : IPresenceSource
    {
        public IReadOnlyList<Guid> ClaimedRequestIds => [];

        public WarCommand.Agent.Core.Model.PresenceState State =>
            WarCommand.Agent.Core.Model.PresenceState.Idle;

        public string? ServerKey => null;
    }

    private sealed class FakeRevalidator : IBoardRevalidator
    {
        public Task RevalidateAsync(Guid deploymentId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeChannelFactory : IWebSocketChannelFactory
    {
        public IWebSocketChannel Create() => throw new NotSupportedException("never connected");
    }
}
