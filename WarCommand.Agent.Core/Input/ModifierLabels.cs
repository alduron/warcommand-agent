using System.Globalization;

namespace WarCommand.Agent.Core.Input;

/// <summary>
/// How a modifier id is written on any surface. One derivation, used by the menu and the board.
/// </summary>
/// <remarks>
/// The catalog carries a display per modifier and it WINS. Deriving one from the id mangles every
/// real name in this game: t21 reads "T 21", box_mags reads "BOX MAGS", and irangefinder reads
/// "IRANGEFINDER" where the game says iRANGE. The derivation stays as the fallback for a tag the
/// catalog has not named, so a new modifier still renders as something rather than throwing.
/// <para>
/// The menu did this inline and the board did not, so a row printed the raw id, DANGER_CLOSE,
/// beside a menu that had offered DANGER CLOSE.
/// </para>
/// </remarks>
public static class ModifierLabels
{
    /// <summary>One modifier, as it is written. 'danger_close' becomes 'DANGER CLOSE'.</summary>
    public static string Of(string modifierId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modifierId);
        return modifierId.Replace('_', ' ').ToUpperInvariant();
    }

    /// <summary>The catalog's word for a tag, or the derived one when it names none.</summary>
    public static string Of(string modifierId, Contracts.Catalog? catalog)
    {
        ArgumentException.ThrowIfNullOrEmpty(modifierId);

        return catalog is not null && catalog.ModifierDisplays.TryGetValue(modifierId, out var display)
            && !string.IsNullOrWhiteSpace(display)
            ? display
            : Of(modifierId);
    }

    /// <summary>
    /// Every modifier on a row, in the order they were chosen, with the quantity after them.
    /// </summary>
    /// <remarks>
    /// All of them, not the first. Choosing danger close AND he and being shown only danger close
    /// is worse than being shown neither, because the row reads as a complete description of the
    /// request and it is not one.
    /// </remarks>
    public static string Line(IReadOnlyList<string> modifierIds, int? quantity) =>
        Line(modifierIds, quantity, catalog: null);

    /// <summary>The same line, with the catalog's word for each tag.</summary>
    public static string Line(
        IReadOnlyList<string> modifierIds,
        int? quantity,
        Contracts.Catalog? catalog) =>
        string.Join(' ', Words(modifierIds, quantity, catalog));

    /// <summary>
    /// The same tags, one entry each, for a surface that draws a tag per tag rather than a line.
    /// </summary>
    /// <remarks>
    /// <c>Line</c> is this joined by a space. Kept as the one derivation so an overlay chip
    /// and an overlay line can never disagree about what the row carries.
    /// </remarks>
    public static IReadOnlyList<string> Words(
        IReadOnlyList<string> modifierIds,
        int? quantity,
        Contracts.Catalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(modifierIds);

        var parts = new List<string>(modifierIds.Count + 1);
        foreach (var id in modifierIds)
        {
            if (!string.IsNullOrEmpty(id))
            {
                parts.Add(Of(id, catalog));
            }
        }

        if (quantity is { } count)
        {
            parts.Add($"x{count.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts;
    }
}
