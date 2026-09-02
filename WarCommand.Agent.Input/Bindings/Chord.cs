namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// The one modifier WarCommand uses. Right Alt is distinct from Left Alt, which many games bind, and
/// a chord cannot be pressed by accident during movement. There are no three-key combinations.
/// </summary>
[Flags]
public enum BindingModifiers
{
    /// <summary>No modifier. Only the user's own PTT choice is ungated.</summary>
    None = 0,

    /// <summary>Right Alt, VK_RMENU.</summary>
    RightAlt = 1,
}

/// <summary>One modifier and one key. The whole shape of a WarCommand binding.</summary>
public readonly record struct Chord(BindingModifiers Modifiers, BindingKey Key)
{
    /// <summary>A bare key with no modifier.</summary>
    public static Chord Of(BindingKey key) => new(BindingModifiers.None, key);

    /// <summary>A RightAlt chord over the key with this label. Throws when the label is unknown.</summary>
    public static Chord RightAlt(string keyLabel) => new(BindingModifiers.RightAlt, KeyFor(keyLabel));

    /// <summary>A bare key by label. Throws when the label is unknown.</summary>
    public static Chord Bare(string keyLabel) => new(BindingModifiers.None, KeyFor(keyLabel));

    /// <summary>Nothing. A binding that has not been chosen.</summary>
    public static Chord Unbound => default;

    /// <summary>False when no key has been chosen.</summary>
    public bool IsBound => Key.IsBound;

    /// <summary>
    /// The display label, assembled from the fixed key label set and the modifier name. This is what
    /// goes to the server as <c>ptt_binding</c> and what the web renders.
    /// </summary>
    public string Label => Modifiers.HasFlag(BindingModifiers.RightAlt)
        ? "RightAlt+" + Key.Label
        : Key.Label;

    /// <summary>The label. There is no other string projection of a chord.</summary>
    public override string ToString() => Label;

    /// <summary>True when this chord is a bare digit, which is a menu key rather than a hotkey.</summary>
    public bool TryDigit(out int digit)
    {
        if (Modifiers == BindingModifiers.None)
        {
            return Key.TryDigit(out digit);
        }

        digit = -1;
        return false;
    }

    /// <summary>
    /// Reads a chord back from its own <see cref="Label"/>. The only parser, so a stored binding
    /// and a rendered one can never disagree about what a string means.
    /// </summary>
    public static bool TryParse(string? label, out Chord chord)
    {
        chord = Unbound;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        const string prefix = "RightAlt+";
        var modifiers = BindingModifiers.None;
        var rest = label;
        if (label.StartsWith(prefix, StringComparison.Ordinal))
        {
            modifiers = BindingModifiers.RightAlt;
            rest = label[prefix.Length..];
        }

        if (!BindingKey.TryFromLabel(rest, out var key))
        {
            return false;
        }

        chord = new Chord(modifiers, key);
        return true;
    }

    private static BindingKey KeyFor(string keyLabel) =>
        BindingKey.TryFromLabel(keyLabel, out var key)
            ? key
            : throw new ArgumentOutOfRangeException(nameof(keyLabel), "not a label in the fixed key set");
}
