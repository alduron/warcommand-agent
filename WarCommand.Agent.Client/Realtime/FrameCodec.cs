using System.Text.Json;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Realtime;

/// <summary>
/// Reads and writes the envelope. Serialising a client frame goes through here rather than through
/// <see cref="Envelope"/>, because <c>seq</c> is assigned by the server and must be absent outbound,
/// not present and null.
/// </summary>
internal static class FrameCodec
{
    /// <summary>
    /// Parses one inbound frame leniently: a missing <c>id</c> or <c>seq</c> yields a frame rather
    /// than an exception, so one malformed field cannot take the connection down.
    /// </summary>
    public static Envelope? TryRead(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new Envelope
            {
                Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : string.Empty,
                Type = type.GetString()!,
                SchemaVersion = root.TryGetProperty("schema_version", out var v) && v.TryGetInt32(out var version)
                    ? version
                    : 1,
                SentAt = root.TryGetProperty("sent_at", out var sentAt) && sentAt.ValueKind == JsonValueKind.String
                    && sentAt.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : null,
                Seq = root.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number
                    && seq.TryGetInt64(out var sequence)
                    ? sequence
                    : null,
                Payload = root.TryGetProperty("payload", out var payload) ? payload.Clone() : default,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialises a client frame. No <c>seq</c>: the server owns it.</summary>
    public static string Write(string id, string type, DateTimeOffset sentAt, object? payload)
    {
        var frame = new ClientFrame
        {
            Id = id,
            Type = type,
            SentAt = sentAt,
            Payload = payload is null
                ? JsonSerializer.SerializeToElement(new EmptyPayload(), AgentJson.Options)
                : JsonSerializer.SerializeToElement(payload, payload.GetType(), AgentJson.Options),
        };

        return JsonSerializer.Serialize(frame, AgentJson.Options);
    }

    private sealed record EmptyPayload;

    private sealed record ClientFrame
    {
        public required string Id { get; init; }

        public required string Type { get; init; }

        public int SchemaVersion { get; init; } = 1;

        public required DateTimeOffset SentAt { get; init; }

        public required JsonElement Payload { get; init; }
    }
}
