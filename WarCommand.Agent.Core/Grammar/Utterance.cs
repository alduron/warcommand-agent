using System.Globalization;

namespace WarCommand.Agent.Core.Grammar;

/// <summary>
/// One recognized token and the recognizer's confidence in that token alone.
/// </summary>
/// <remarks>
/// Per-token confidence exists because the utterance score cannot fail one bad digit: a confident
/// <c>mortar</c> plus one badly heard digit clears the intent floor and produces a well formed
/// wrong grid.
/// </remarks>
public sealed record RecognizedToken(string Text, double Confidence)
{
    /// <summary>The token's numeric reading, or null when it is not a number word or numeral.</summary>
    public int? Value => NumberWords.Value(Text);

    /// <summary>True when the token reads as a single digit 0-9, spoken or written.</summary>
    public bool IsDigit => NumberWords.IsSingleDigit(Text);
}

/// <summary>
/// What <c>ISpeechEngine.RecognizeAsync(AudioBuffer, Grammar, CancellationToken)</c> returns: the
/// tokens it heard, its confidence in the utterance, and the hypotheses it ranked below the top one.
/// </summary>
/// <remarks>
/// <see cref="Alternatives"/> exists so a near tie can become a menu instead of a guess. Nothing
/// here is ever written to disk: an utterance holds text and scores, never audio.
/// </remarks>
public sealed record Utterance
{
    /// <summary>Tokens in spoken order. Empty means a stray key press and no overlay noise.</summary>
    public required IReadOnlyList<RecognizedToken> Tokens { get; init; }

    /// <summary>Intent confidence over the whole utterance. Tested against min_intent_confidence.</summary>
    public required double Confidence { get; init; }

    /// <summary>Lower-ranked hypotheses, best first. Compared against ambiguity_margin.</summary>
    public IReadOnlyList<Utterance> Alternatives { get; init; } = [];

    public bool IsEmpty => Tokens.Count == 0;

    /// <summary>The recognized text, single spaced.</summary>
    public string Text => string.Join(' ', Tokens.Select(t => t.Text));

    /// <summary>
    /// The minimum per-token confidence over the tokens that read as digits. This is what
    /// request_points.confidence stores for a spoken grid, and the only number that moves when
    /// exactly one digit is wrong. Null when the utterance carries no digits.
    /// </summary>
    public decimal? MinDigitConfidence => MinConfidenceOver(0, Tokens.Count, digitsOnly: true);

    /// <summary>
    /// The minimum per-token confidence over a token span. <paramref name="digitsOnly"/> restricts
    /// it to the tokens that read as digits, which is the grid case.
    /// </summary>
    public decimal? MinConfidenceOver(int start, int count, bool digitsOnly = false)
    {
        decimal? min = null;
        var end = Math.Min(start + count, Tokens.Count);
        for (var i = Math.Max(start, 0); i < end; i++)
        {
            var token = Tokens[i];
            if (digitsOnly && !token.IsDigit)
            {
                continue;
            }

            var value = (decimal)token.Confidence;
            if (min is null || value < min)
            {
                min = value;
            }
        }

        return min;
    }

    /// <summary>Builds an utterance from a space-separated string with one confidence for every token.</summary>
    public static Utterance FromWords(string text, double confidence)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new Utterance
        {
            Tokens = [.. words.Select(w => new RecognizedToken(w, confidence))],
            Confidence = confidence,
        };
    }
}

/// <summary>
/// Digit and cardinal readings. Both are accepted because people use both under pressure, and the
/// parser tries digit by digit first: it is military convention and the recognizer is better at it.
/// </summary>
public static class NumberWords
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["oh"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
    };

    private static readonly Dictionary<string, int> Teens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90,
    };

    /// <summary>Every number word the grammar loads, in the order they are listed above.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. Units.Keys, .. Teens.Keys, .. Tens.Keys];

    /// <summary>The nine slot digits. Zero is excluded: 'oh' and 'zero' were not worth the ambiguity.</summary>
    public static IReadOnlyList<string> SlotDigitWords { get; } =
        ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>Every digit word including both readings of zero, for the join code.</summary>
    public static IReadOnlyList<string> CodeDigitWords { get; } =
        ["zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>The token's value as a spoken word or a written numeral, or null.</summary>
    public static int? Value(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (Units.TryGetValue(token, out var unit))
        {
            return unit;
        }

        if (Teens.TryGetValue(token, out var teen))
        {
            return teen;
        }

        if (Tens.TryGetValue(token, out var ten))
        {
            return ten;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var numeral)
            ? numeral
            : null;
    }

    /// <summary>True when the token reads as exactly one digit 0-9.</summary>
    public static bool IsSingleDigit(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (Units.ContainsKey(token))
        {
            return true;
        }

        return token.Length == 1 && token[0] is >= '0' and <= '9';
    }

    /// <summary>True when the token is a tens word, which starts a cardinal reading.</summary>
    public static bool IsTens(string token) => !string.IsNullOrEmpty(token) && Tens.ContainsKey(token);
}
