using System.Globalization;

namespace WarCommand.Agent.Core.Input;

/// <summary>
/// How a modifier id is written on any surface. One derivation, used by the menu and the board.
/// </summary>
/// <remarks>
/// The catalog carries modifier ids and no display names, so the label is derived rather than
/// looked up: nothing here is a fact about the game, which is why it may live in code at all. The
/// menu did this inline and the board did not, so a row printed the raw id, DANGER_CLOSE, beside a
/// menu that had offered DANGER CLOSE.
/// </remarks>
public static class ModifierLabels
{
    /// <summary>One modifier, as it is written. 'danger_close' becomes 'DANGER CLOSE'.</summary>
    public static string Of(string modifierId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modifierId);
        return modifierId.Replace('_', ' ').ToUpperInvariant();
    }

    /// <summary>
    /// Every modifier on a row, in the order they were chosen, with the quantity after them.
    /// </summary>
    /// <remarks>
    /// All of them, not the first. Choosing danger close AND he and being shown only danger close
    /// is worse than being shown neither, because the row reads as a complete description of the
    /// request and it is not one.
    /// </remarks>
    public static string Line(IReadOnlyList<string> modifierIds, int? quantity)
    {
        ArgumentNullException.ThrowIfNull(modifierIds);

        var parts = new List<string>(modifierIds.Count + 1);
        foreach (var id in modifierIds)
        {
            if (!string.IsNullOrEmpty(id))
            {
                parts.Add(Of(id));
            }
        }

        if (quantity is { } count)
        {
            parts.Add($"x{count.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(' ', parts);
    }
}
