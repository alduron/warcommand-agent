using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>
/// Reads a decimal from a JSON number OR a JSON string, and always writes a number.
/// </summary>
/// <remarks>
/// The server does not agree with itself. `confidence` comes back as a NUMBER over HTTP and as a
/// STRING over the websocket, because the realtime projection stringifies it. A strict
/// <c>decimal?</c> refuses the string, the frame is discarded, and the row never reaches the board
/// even though the request was created: a submit that succeeded looked to the user like every
/// submit failing.
/// <para>
/// Being liberal in what is accepted is the right side to be lenient on. The agent cannot deploy the
/// server, and a client that drops a whole frame over the spelling of one number is a client that
/// breaks every time a field's serialisation is tightened somewhere upstream. Writing stays strict:
/// what the agent SENDS is always a number, so it never contributes to the ambiguity.
/// </para>
/// </remarks>
public sealed class LenientDecimalConverter : JsonConverter<decimal>
{
    /// <inheritdoc />
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDecimal(),
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException(
                $"expected a number or a numeric string for {typeToConvert.Name}, got {reader.TokenType}"),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value);
    }

    private static decimal Parse(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a number");
}

/// <summary>The nullable half of <see cref="LenientDecimalConverter"/>. Null stays null.</summary>
public sealed class LenientNullableDecimalConverter : JsonConverter<decimal?>
{
    private static readonly LenientDecimalConverter Inner = new();

    /// <inheritdoc />
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(decimal), options);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is { } number)
        {
            writer.WriteNumberValue(number);
            return;
        }

        writer.WriteNullValue();
    }
}
