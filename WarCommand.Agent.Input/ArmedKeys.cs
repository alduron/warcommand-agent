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
    internal static ArmedKeys Build(BindingSet bindings, bool suspended, bool menuOpen)
    {
        var table = new bool[Codes];

        if (suspended)
        {
            bindings.ArmPanicOnlyIn(table);
            return new ArmedKeys(table);
        }

        bindings.ArmIn(table);

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
    /// The only keys a menu is allowed to swallow: the digits, Escape and Backspace. The PTT key is
    /// already armed as a binding. Everything else passes to the game, because a menu that ate W for
    /// a second and a half would get somebody killed.
    /// </summary>
    private static IEnumerable<string> MenuKeyLabels
    {
        get
        {
            yield return "Escape";
            yield return "Backspace";
            for (var d = 0; d <= 9; d++)
            {
                yield return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
