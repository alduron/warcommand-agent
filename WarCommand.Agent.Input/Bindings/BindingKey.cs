namespace WarCommand.Agent.Input.Bindings;

/// <summary>A mouse button WarCommand will accept as a binding. Left and Right are not offered.</summary>
public enum MouseButton
{
    /// <summary>Not a button.</summary>
    None = 0,

    /// <summary>Middle button, VK_MBUTTON.</summary>
    Middle,

    /// <summary>Thumb button 1, VK_XBUTTON1.</summary>
    Button4,

    /// <summary>Thumb button 2, VK_XBUTTON2. The suggested push-to-talk key.</summary>
    Button5,
}

/// <summary>
/// One bindable key or mouse button. The code it wraps has no accessor of any kind: it enters
/// through <see cref="TryFromVirtualKey"/> and leaves only as a bool or a label from the fixed set
/// in <see cref="KeyLabels"/>. A code with no label is not representable, so no code path can render
/// one into a string.
/// </summary>
public readonly struct BindingKey : IEquatable<BindingKey>
{
    private readonly ushort _code;

    private BindingKey(ushort code) => _code = code;

    /// <summary>The unbound key. PTT holds this until first run makes the user choose.</summary>
    public static BindingKey Unbound => default;

    /// <summary>Every label a key may wear. Nothing outside this set reaches the server or the UI.</summary>
    public static IReadOnlyList<string> Labels => KeyLabels.All;

    /// <summary>False for <see cref="Unbound"/>.</summary>
    public bool IsBound => _code != KeyLabels.Unbound;

    /// <summary>
    /// The display label, from the fixed set. This is what is sent as <c>ptt_binding</c> and what the
    /// web renders. It is never logged.
    /// </summary>
    public string Label => KeyLabels.Label(_code);

    /// <summary>The label. There is no other string projection of a key.</summary>
    public override string ToString() => Label;

    /// <summary>
    /// Accepts a virtual-key code only if it carries a label. An unlabelled code yields false and no
    /// key, which is what keeps an arbitrary keystroke from ever being held or printed.
    /// </summary>
    public static bool TryFromVirtualKey(int virtualKey, out BindingKey key)
    {
        if (virtualKey is > 0 and <= ushort.MaxValue && KeyLabels.IsKnown((ushort)virtualKey))
        {
            key = new BindingKey((ushort)virtualKey);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>Accepts a bindable mouse button. Left and Right are not bindable.</summary>
    public static bool TryFromMouseButton(MouseButton button, out BindingKey key) => button switch
    {
        MouseButton.Middle => TryFromVirtualKey(0x04, out key),
        MouseButton.Button4 => TryFromVirtualKey(0x05, out key),
        MouseButton.Button5 => TryFromVirtualKey(0x06, out key),
        _ => Fail(out key),
    };

    /// <summary>Rehydrates a key from a persisted label. Unknown labels yield false.</summary>
    public static bool TryFromLabel(string label, out BindingKey key)
    {
        if (label is not null && KeyLabels.TryCode(label, out var code))
        {
            key = new BindingKey(code);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// True when this key is the digit <paramref name="digit"/>. Read off the label, never the code.
    /// </summary>
    /// <remarks>
    /// The numpad counts. Its keys are labelled Numpad0 to Numpad9, which is eight characters, so
    /// the single-character test refused every one of them: an invite code or a typed grid could
    /// only be entered on the number row, and the numpad did nothing at all.
    /// </remarks>
    public bool TryDigit(out int digit)
    {
        var label = Label;
        if (label.Length == 1 && label[0] is >= '0' and <= '9')
        {
            digit = label[0] - '0';
            return true;
        }

        const string numpad = "Numpad";
        if (label.Length == numpad.Length + 1
            && label.StartsWith(numpad, StringComparison.Ordinal)
            && label[^1] is >= '0' and <= '9')
        {
            digit = label[^1] - '0';
            return true;
        }

        digit = -1;
        return false;
    }

    /// <summary>
    /// Marks this key's slot in a hook arming table. The code is passed to nobody: it is used as an
    /// index and discarded.
    /// </summary>
    internal void ArmIn(bool[] table)
    {
        if (_code != KeyLabels.Unbound)
        {
            table[_code] = true;
        }
    }

    /// <summary>True when a code the hook just received is this key. The code is not retained.</summary>
    internal bool Matches(int virtualKey) => _code != KeyLabels.Unbound && _code == virtualKey;

    /// <summary>True when both keys are the same key.</summary>
    public bool Equals(BindingKey other) => _code == other._code;

    /// <summary>True when both keys are the same key.</summary>
    public override bool Equals(object? obj) => obj is BindingKey other && Equals(other);

    /// <summary>Scrambled deliberately, so the hash is not a second reading of the code.</summary>
    public override int GetHashCode() => HashCode.Combine(_code);

    /// <summary>True when both keys are the same key.</summary>
    public static bool operator ==(BindingKey left, BindingKey right) => left.Equals(right);

    /// <summary>True when the keys differ.</summary>
    public static bool operator !=(BindingKey left, BindingKey right) => !left.Equals(right);

    private static bool Fail(out BindingKey key)
    {
        key = default;
        return false;
    }
}
