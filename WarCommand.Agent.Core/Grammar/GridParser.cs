namespace WarCommand.Agent.Core.Grammar;

/// <summary>A parsed spoken grid: two axes, the digit confidence, and how many tokens it ate.</summary>
public sealed record GridParse(decimal X, decimal Y, decimal MinDigitConfidence, int Start, int TokensConsumed)
{
    public int End => Start + TokensConsumed;
}

/// <summary>One axis of a spoken grid.</summary>
public sealed record AxisParse(decimal Value, decimal MinDigitConfidence, int TokensConsumed);

/// <summary>
/// The spoken-grid fallback. Both readings are accepted because people use both under pressure, and
/// digit by digit is tried first: it is military convention and the recognizer is better at it.
/// </summary>
/// <remarks>
/// <c>request_points.confidence</c> for a spoken grid stores the minimum per-token confidence over
/// the grid digits, never the utterance-level intent score.
/// </remarks>
public static class GridParser
{
    private const string PointWord = "point";

    /// <summary>Two axes in a row. Null when the tokens are not a grid.</summary>
    public static GridParse? TryParse(IReadOnlyList<RecognizedToken> tokens, int start)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var x = TryParseAxis(tokens, start);
        if (x is null)
        {
            return null;
        }

        var y = TryParseAxis(tokens, start + x.TokensConsumed);
        if (y is null)
        {
            return null;
        }

        var consumed = x.TokensConsumed + y.TokensConsumed;
        var confidence = Math.Min(x.MinDigitConfidence, y.MinDigitConfidence);
        return new GridParse(x.Value, y.Value, confidence, start, consumed);
    }

    /// <summary>
    /// One axis: <c>digit+ "point" digit digit</c> or <c>tens "point" digit digit</c>. Null when
    /// the tokens do not read as an axis.
    /// </summary>
    public static AxisParse? TryParseAxis(IReadOnlyList<RecognizedToken> tokens, int start)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (start < 0 || start >= tokens.Count)
        {
            return null;
        }

        var pointAt = -1;
        for (var i = start; i < tokens.Count && i < start + 4; i++)
        {
            if (string.Equals(tokens[i].Text, PointWord, StringComparison.OrdinalIgnoreCase))
            {
                pointAt = i;
                break;
            }
        }

        if (pointAt <= start || pointAt + 2 >= tokens.Count)
        {
            return null;
        }

        if (!TryReadWhole(tokens, start, pointAt, out var whole))
        {
            return null;
        }

        if (!tokens[pointAt + 1].IsDigit || !tokens[pointAt + 2].IsDigit)
        {
            return null;
        }

        var tenths = tokens[pointAt + 1].Value!.Value;
        var hundredths = tokens[pointAt + 2].Value!.Value;
        var value = whole + (((tenths * 10) + hundredths) / 100m);

        decimal min = 1m;
        for (var i = start; i <= pointAt + 2; i++)
        {
            if (i == pointAt)
            {
                continue;
            }

            min = Math.Min(min, (decimal)tokens[i].Confidence);
        }

        return new AxisParse(value, min, pointAt + 3 - start);
    }

    /// <summary>
    /// Exactly <paramref name="count"/> digit tokens, as a digit string. Both "zero" and "oh" map to
    /// 0. Anything else, including too few, returns null: fewer digits is never a slot action.
    /// </summary>
    public static string? TryParseDigits(
        IReadOnlyList<RecognizedToken> tokens,
        int start,
        int count,
        out decimal minConfidence)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        minConfidence = 1m;

        if (start < 0 || count <= 0 || start + count > tokens.Count)
        {
            return null;
        }

        var digits = new char[count];
        for (var i = 0; i < count; i++)
        {
            var token = tokens[start + i];
            if (!token.IsDigit)
            {
                return null;
            }

            digits[i] = (char)('0' + token.Value!.Value);
            minConfidence = Math.Min(minConfidence, (decimal)token.Confidence);
        }

        return new string(digits);
    }

    private static bool TryReadWhole(IReadOnlyList<RecognizedToken> tokens, int start, int pointAt, out int whole)
    {
        whole = 0;

        // Digit by digit first. "eight five" -> 85.
        var allDigits = true;
        for (var i = start; i < pointAt; i++)
        {
            if (!tokens[i].IsDigit)
            {
                allDigits = false;
                break;
            }
        }

        if (allDigits)
        {
            for (var i = start; i < pointAt; i++)
            {
                whole = (whole * 10) + tokens[i].Value!.Value;
            }

            return true;
        }

        // Cardinal. "eighty five" -> 85, "fifty" -> 50.
        var span = pointAt - start;
        if (span == 1)
        {
            var single = tokens[start].Value;
            if (single is null)
            {
                return false;
            }

            whole = single.Value;
            return true;
        }

        if (span != 2 || !NumberWords.IsTens(tokens[start].Text) || !tokens[start + 1].IsDigit)
        {
            return false;
        }

        whole = tokens[start].Value!.Value + tokens[start + 1].Value!.Value;
        return true;
    }
}
