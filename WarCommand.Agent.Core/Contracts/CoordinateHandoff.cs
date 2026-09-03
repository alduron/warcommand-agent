using System.Globalization;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>
/// Renders a coordinate for the clipboard using the served template.
/// </summary>
/// <remarks>
/// The text shape is a fact about the game, not about our vocabulary, so it lives in
/// <c>coordinate_handoff</c> and never in code. The measured shape is
/// <c>MED-24 x94.70, y107.25</c>: Wardogs renders a coordinate written that way in chat as a
/// CLICKABLE link that pins the map for whoever clicks it, and a ticket code in front survives the
/// parse. One paste therefore puts both the job and a pinnable point on comms.
/// <para>
/// The copy path used to write <c>94.70 107.25</c> from a hardcoded interpolation: no ticket, no
/// prefixes, no comma, and so no pin for anybody who clicked it. Nothing here may be inlined again.
/// </para>
/// </remarks>
public static class CoordinateHandoffText
{
    /// <summary>
    /// The clipboard text for a point, or null when the profile names no usable format.
    /// </summary>
    /// <param name="handoff">The served coordinate_handoff section.</param>
    /// <param name="ticket">The row's ticket code. Templates without {ticket} ignore it.</param>
    /// <param name="x">Map x.</param>
    /// <param name="y">Map y.</param>
    /// <param name="formatId">Which format to use. Null takes the profile's default.</param>
    public static string? Render(
        CoordinateHandoffSection handoff,
        string? ticket,
        decimal x,
        decimal y,
        string? formatId = null)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        var wanted = formatId ?? handoff.DefaultFormat;

        var format = handoff.Format(wanted)
            ?? handoff.Format(handoff.DefaultFormat)
            ?? (handoff.Formats.Count > 0 ? handoff.Formats[0] : null);

        if (format is null || string.IsNullOrWhiteSpace(format.Template))
        {
            return null;
        }

        // Two decimals, always. A grid that drops a trailing zero is a different string to whatever
        // parses it on the other side, and the game's own readout always shows two.
        var text = format.Template
            .Replace("{x}", x.ToString("0.00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", y.ToString("0.00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ticket}", ticket ?? string.Empty, StringComparison.Ordinal);

        return text.Trim();
    }
}
