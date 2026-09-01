namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// Modifier tracking for the hook. Modifiers are not <see cref="BindingKey"/>s: they carry no label,
/// are never a binding on their own, and are never swallowed.
/// </summary>
internal static class ModifierKeys
{
    private const ushort VkRightMenu = 0xA5;

    /// <summary>True when this code is a modifier the hook must watch.</summary>
    internal static bool IsModifier(int virtualKey) => virtualKey == VkRightMenu;

    /// <summary>Which modifier a code is, or <see cref="BindingModifiers.None"/>.</summary>
    internal static BindingModifiers Of(int virtualKey) =>
        virtualKey == VkRightMenu ? BindingModifiers.RightAlt : BindingModifiers.None;

    /// <summary>Marks the modifier's slot in a hook arming table.</summary>
    internal static void ArmIn(BindingModifiers modifiers, bool[] table)
    {
        if (modifiers.HasFlag(BindingModifiers.RightAlt))
        {
            table[VkRightMenu] = true;
        }
    }
}
