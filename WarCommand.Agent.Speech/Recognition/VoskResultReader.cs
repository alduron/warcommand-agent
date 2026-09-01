using System.Text.Json;
using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Speech.Recognition;

/// <summary>
/// Turns one Vosk result document into an <see cref="Utterance"/>.
/// </summary>
/// <remarks>
/// Vosk emits two shapes. With word timings on and alternatives off it emits
/// <c>{"text":..,"result":[{"word":..,"conf":..}]}</c>, and <c>conf</c> is the per-token confidence
/// <c>request_points.confidence</c> needs. With alternatives on it emits
/// <c>{"alternatives":[{"text":..,"confidence":..,"result":[..]}]}</c> and drops <c>conf</c>
/// entirely, leaving only a lattice score for the whole hypothesis. This reader never invents a
/// per-token number from that score: a hypothesis whose words carry no confidence scores zero and
/// is rejected, which is why <see cref="VoskSpeechEngine"/> does not turn alternatives on.
/// </remarks>
public static class VoskResultReader
{
    /// <summary>An utterance with no tokens. A stray key press, and no overlay noise.</summary>
    public static Utterance Empty { get; } = new() { Tokens = [], Confidence = 0 };

    /// <summary>Reads a <c>Result()</c> or <c>FinalResult()</c> document. Malformed input is empty.</summary>
    public static Utterance Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            if (root.TryGetProperty("alternatives", out var alternatives)
                && alternatives.ValueKind == JsonValueKind.Array
                && alternatives.GetArrayLength() > 0)
            {
                var ranked = alternatives.EnumerateArray().Select(ReadHypothesis).ToList();
                return ranked[0] with { Alternatives = [.. ranked.Skip(1)] };
            }

            return ReadHypothesis(root);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static Utterance ReadHypothesis(JsonElement element)
    {
        var tokens = new List<RecognizedToken>();

        if (element.TryGetProperty("result", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in words.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("word", out var word)
                    || word.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = word.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                tokens.Add(new RecognizedToken(text, ConfidenceOf(entry)));
            }
        }
        else if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            // No word timings. Every token scores zero rather than borrowing a number it does not
            // have, so the utterance falls below the intent floor and nothing is sent.
            foreach (var word in (text.GetString() ?? string.Empty).Split(
                         ' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                tokens.Add(new RecognizedToken(word, 0));
            }
        }

        return tokens.Count == 0
            ? Empty
            : new Utterance
            {
                Tokens = tokens,
                Confidence = tokens.Average(t => t.Confidence),
            };
    }

    /// <summary>Per-word confidence, clamped to 0..1. Absent means zero, never a guess.</summary>
    private static double ConfidenceOf(JsonElement entry) =>
        entry.TryGetProperty("conf", out var confidence)
        && confidence.ValueKind == JsonValueKind.Number
        && confidence.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1)
            : 0;
}
