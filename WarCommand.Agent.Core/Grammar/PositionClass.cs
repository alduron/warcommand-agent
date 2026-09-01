namespace WarCommand.Agent.Core.Grammar;

/// <summary>
/// The vocabulary is not flat. A token is only ever a candidate in the position it belongs to,
/// which is what keeps <c>left</c> apart from <c>lift</c> and <c>right</c> apart from <c>ride</c>.
/// </summary>
/// <remarks>
/// Collisions are computed per class, never across the whole alias set. Two words in different
/// classes may be perfect homophones without ever being confused: <c>wall</c> and <c>all</c> are.
/// </remarks>
public enum PositionClass
{
    /// <summary>First token only. Request type aliases, command verbs, join, role, help.</summary>
    Initial,

    /// <summary>After a request type. Modifiers and the takes_quantity numeral.</summary>
    Modifier,

    /// <summary>After a type that takes one. Supply kinds and structure kinds.</summary>
    Kind,

    /// <summary>After a command verb. 1-9, next, top, all.</summary>
    Slot,

    /// <summary>After adjust and its slot ref. over, short, left, right.</summary>
    AdjustDirection,

    /// <summary>After join only, and exactly six of them. Zero is legal here and nowhere else.</summary>
    Digit,
}

/// <summary>Maps <see cref="PositionClass"/> to and from the names in contracts/request-types.json.</summary>
public static class PositionClasses
{
    private static readonly (PositionClass Value, string Name)[] Names =
    [
        (PositionClass.Initial, "initial"),
        (PositionClass.Modifier, "modifier"),
        (PositionClass.Kind, "kind"),
        (PositionClass.Slot, "slot"),
        (PositionClass.AdjustDirection, "adjust_direction"),
        (PositionClass.Digit, "digit"),
    ];

    /// <summary>Every class, in contract order.</summary>
    public static IReadOnlyList<PositionClass> All { get; } = [.. Names.Select(n => n.Value)];

    /// <summary>The contract's name for a class.</summary>
    public static string ContractName(PositionClass value) =>
        Names.First(n => n.Value == value).Name;

    /// <summary>Parses a contract position_class value. Null when the contract names one we do not know.</summary>
    public static PositionClass? TryParse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var (value, candidate) in Names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }
}
