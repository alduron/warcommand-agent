namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// The fixed set of labels a key may wear, and the only place a virtual-key code and a string sit in
/// the same table. A code absent from this table cannot become a <see cref="BindingKey"/> at all, so
/// nothing unlabelled is ever representable, let alone printable.
/// </summary>
internal static class KeyLabels
{
    internal const ushort Unbound = 0;

    /// <summary>Label for <see cref="Unbound"/>. Not a key, and never a code.</summary>
    internal const string UnboundLabel = "(unbound)";

    private static readonly Dictionary<ushort, string> LabelByCode = Build();

    private static readonly Dictionary<string, ushort> CodeByLabel =
        LabelByCode.ToDictionary(e => e.Value, e => e.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every label a key may wear, sorted. The fixed set.</summary>
    internal static IReadOnlyList<string> All { get; } =
        [.. LabelByCode.Values.Order(StringComparer.Ordinal)];

    internal static bool IsKnown(ushort code) => code != Unbound && LabelByCode.ContainsKey(code);

    internal static string Label(ushort code) =>
        LabelByCode.TryGetValue(code, out var label) ? label : UnboundLabel;

    internal static bool TryCode(string label, out ushort code) =>
        CodeByLabel.TryGetValue(label, out code);

    private static Dictionary<ushort, string> Build()
    {
        var table = new Dictionary<ushort, string>();

        // Mouse. Left and Right are deliberately absent: swallowing either would take the game's
        // primary controls, and an unlabelled button cannot be bound.
        table[0x04] = "Mouse3";
        table[0x05] = "Mouse4";
        table[0x06] = "Mouse5";

        for (ushort c = 0x30; c <= 0x39; c++)
        {
            table[c] = ((char)c).ToString();
        }

        for (ushort c = 0x41; c <= 0x5A; c++)
        {
            table[c] = ((char)c).ToString();
        }

        for (ushort i = 0; i < 24; i++)
        {
            table[(ushort)(0x70 + i)] = $"F{i + 1}";
        }

        for (ushort i = 0; i < 10; i++)
        {
            table[(ushort)(0x60 + i)] = $"Numpad{i}";
        }

        table[0x6A] = "NumpadMultiply";
        table[0x6B] = "NumpadAdd";
        table[0x6D] = "NumpadSubtract";
        table[0x6E] = "NumpadDecimal";
        table[0x6F] = "NumpadDivide";

        table[0x08] = "Backspace";
        table[0x09] = "Tab";
        table[0x0D] = "Enter";
        table[0x13] = "Pause";
        table[0x14] = "CapsLock";
        table[0x1B] = "Escape";
        table[0x20] = "Space";
        table[0x21] = "PageUp";
        table[0x22] = "PageDown";
        table[0x23] = "End";
        table[0x24] = "Home";
        table[0x25] = "Left";
        table[0x26] = "Up";
        table[0x27] = "Right";
        table[0x28] = "Down";
        table[0x2C] = "PrintScreen";
        table[0x2D] = "Insert";
        table[0x2E] = "Delete";
        table[0x90] = "NumLock";
        table[0x91] = "ScrollLock";
        table[0xBA] = "Semicolon";
        table[0xBB] = "Equals";
        table[0xBC] = "Comma";
        table[0xBD] = "Minus";
        table[0xBE] = "Period";
        table[0xBF] = "Slash";
        table[0xC0] = "Grave";
        table[0xDB] = "LeftBracket";
        table[0xDC] = "Backslash";
        table[0xDD] = "RightBracket";
        table[0xDE] = "Quote";

        return table;
    }
}
