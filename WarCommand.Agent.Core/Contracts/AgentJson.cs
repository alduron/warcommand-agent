using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>
/// The one serializer configuration for every contract and wire frame in the agent.
/// Property names and enum members map to snake_case; unknown fields are ignored so an older agent
/// keeps working against a newer profile.
/// </summary>
public static class AgentJson
{
    /// <summary>Use these options everywhere. A default JsonSerializerOptions will not match the wire.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,

            // Nulls are written, not omitted: 08-api-realtime.md documents `"server_key": null` as
            // a value, and absent must never be mistaken for a different statement than null.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));

        // The server sends confidence as a number over HTTP and as a string over the socket. A
        // strict decimal? discards the whole frame over that, so a created request never reaches
        // the board. Read either; always write a number.
        options.Converters.Add(new LenientDecimalConverter());
        options.Converters.Add(new LenientNullableDecimalConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
