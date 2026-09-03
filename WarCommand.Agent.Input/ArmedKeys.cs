using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Input;

/// <summary>
/// The hook's first and cheapest test: is this code one WarCommand holds at all. A flat table indexed
/// by the code, so the answer costs one bounds check and one load, allocates nothing, and the code
/// itself is used as an index and discarded.
/// </summary>
public sealed class ArmedKeys
{
    private const int Codes = 256;

    private readonly bool[] _table;

    private ArmedKeys(bool[] table) => _table = table;

    /// <summary>Nothing is armed.</summary>
    public static ArmedKeys None { get; } = new(new bool[Codes]);

    /// <summary>
    /// True when the hook may look further at this code. False means return immediately: no
    /// processing, no allocation, nothing recorded.
    /// </summary>
    public bool IsArmed(int virtualKey) => (uint)virtualKey < Codes && _table[virtualKey];

    /// <summary>
    /// The table for the current state. While Panic is engaged nothing but Panic is armed, which is
    /// the hook suspended as far as it can be and still hear the press that resumes it.
    /// </summary>
    internal static ArmedKeys Build(BindingSet bindings, bool suspended, bool menuOpen, bool holdActive = false)
    {
        var table = new bool[Codes];

        if (suspended)
        {
            bindings.ArmPanicOnlyIn(table);
            return new ArmedKeys(table);
        }

        bindings.ArmIn(table, holdActive);

        if (menuOpen)
        {
            foreach (var label in MenuKeyLabels)
            {
                if (BindingKey.TryFromLabel(label, out var key))
                {
                    key.ArmIn(table);
                }
            }
        }

        return new ArmedKeys(table);
    }

    /// <summary>
    /// The only keys a menu is allowed to swallow: the digits, on the number row and the numpad. The
    /// PTT key is already armed as a binding. Everything else passes to the game, because a menu
    /// that ate W for a second and a half would get somebody killed.
    ///
    /// Escape and Backspace used to be here and are gone. Escape discarded and closed, which is
    /// exactly what letting go of the hold key does, so it bought nothing and cost the game its own
    /// Escape key for as long as the menu was open. Backspace deleted one typed digit, which the
    /// back key already does.
    /// </summary>
    private static IEnumerable<string> MenuKeyLabels
    {
        get
        {
            for (var d = 0; d <= 9; d++)
            {
                var digit = d.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Both, always. The numpad is where a hand types six digits, and arming only the
                // number row meant an invite code could not be entered on it at all.
                yield return digit;
                yield return $"Numpad{digit}";
            }
        }
    }
}
