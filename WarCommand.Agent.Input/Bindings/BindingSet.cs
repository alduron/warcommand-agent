namespace WarCommand.Agent.Input.Bindings;

/// <summary>Why a rebind was accepted or refused.</summary>
public enum RebindStatus
{
    /// <summary>The chord is now held by the action.</summary>
    Applied = 0,

    /// <summary>Another WarCommand binding already holds that chord. It is named in the result.</summary>
    RefusedConflict,

    /// <summary>Panic cannot be unbound. A kill switch with no key is not a kill switch.</summary>
    RefusedCannotUnbind,

    /// <summary>The chord carries no key.</summary>
    RefusedNotAKey,
}

/// <summary>The outcome of a rebind, naming the other binding when one is in the way.</summary>
public readonly record struct RebindResult(RebindStatus Status, BindingAction ConflictsWith)
{
    /// <summary>True only for <see cref="RebindStatus.Applied"/>.</summary>
    public bool Applied => Status == RebindStatus.Applied;

    internal static RebindResult Ok() => new(RebindStatus.Applied, BindingAction.None);

    internal static RebindResult Conflict(BindingAction other) => new(RebindStatus.RefusedConflict, other);

    internal static RebindResult Refused(RebindStatus status) => new(status, BindingAction.None);
}

/// <summary>
/// Which chord each action holds. Four bindings, two of them chorded behind RightAlt. Every one is
/// rebindable, including PTT and Panic. A conflict
/// with another WarCommand binding is refused and names the other one; a conflict with the game
/// cannot be detected, and <see cref="CanDetectGameConflicts"/> says so rather than pretending.
/// </summary>
public sealed class BindingSet
{
    private readonly Dictionary<BindingAction, Chord> _chords = [];

    private BindingSet() => ApplyDefaults();

    /// <summary>Raised whenever a chord changes, so the hook can rebuild its arming table.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// V to speak, RightAlt for the menu, Escape, and the two RightAlt chords. Both hold keys ship
    /// bound: the overlay does nothing at all without one of them held, so an unbound default was a
    /// product that did nothing until the user found a settings page to fix it.
    /// </summary>
    public static BindingSet Defaults() => new();

    /// <summary>
    /// The key the first-run picker suggests. A suggestion the user confirms, never a value applied
    /// on their behalf.
    /// </summary>
    public static Chord SuggestedPtt =>
        BindingKey.TryFromMouseButton(MouseButton.Button5, out var key) ? Chord.Of(key) : Chord.Unbound;

    /// <summary>
    /// Always false. We do not read the game's bind file, and guessing is worse than silence. Callers
    /// must show <see cref="GameConflictNotice"/> rather than implying a check happened.
    /// </summary>
    public static bool CanDetectGameConflicts => false;

    /// <summary>The one line the rebind dialog shows in place of a game-conflict check.</summary>
    public static string GameConflictNotice =>
        "WarCommand cannot see the game's keybinds. Test this key in Wardogs before committing to it.";

    /// <summary>True once the user has chosen a PTT key. False sends them to the first-run picker.</summary>
    public bool PttChosen => _chords[BindingAction.Ptt].IsBound;

    /// <summary>Every action and the chord it holds, including any that is unbound.</summary>
    public IEnumerable<KeyValuePair<BindingAction, Chord>> All =>
        BindingActions.All.Select(a => new KeyValuePair<BindingAction, Chord>(a, _chords[a]));

    /// <summary>The chord an action holds, or <see cref="Chord.Unbound"/>.</summary>
    public Chord this[BindingAction action] =>
        _chords.TryGetValue(action, out var chord) ? chord : Chord.Unbound;

    /// <summary>Which action holds this chord, or <see cref="BindingAction.None"/>.</summary>
    public BindingAction Resolve(Chord chord)
    {
        if (!chord.IsBound)
        {
            return BindingAction.None;
        }

        foreach (var (action, held) in _chords)
        {
            if (held.IsBound && held == chord)
            {
                return action;
            }
        }

        return BindingAction.None;
    }

    /// <summary>
    /// Points an action at a chord. Refused, naming the other binding, when anything else already
    /// holds it. Rebinding an action to the chord it already holds is a no-op success.
    /// </summary>
    public RebindResult Rebind(BindingAction action, Chord chord)
    {
        if (action == BindingAction.None)
        {
            return RebindResult.Refused(RebindStatus.RefusedNotAKey);
        }

        if (!chord.IsBound)
        {
            return RebindResult.Refused(RebindStatus.RefusedNotAKey);
        }

        var holder = Resolve(chord);
        if (holder != BindingAction.None && holder != action)
        {
            return RebindResult.Conflict(holder);
        }

        _chords[action] = chord;
        Changed?.Invoke(this, EventArgs.Empty);
        return RebindResult.Ok();
    }

    /// <summary>Clears a binding. Refused for Panic, which is rebindable but cannot be unbound.</summary>
    public RebindResult Unbind(BindingAction action)
    {
        if (!BindingActions.CanBeUnbound(action))
        {
            return RebindResult.Refused(RebindStatus.RefusedCannotUnbind);
        }

        if (action == BindingAction.None)
        {
            return RebindResult.Refused(RebindStatus.RefusedNotAKey);
        }

        _chords[action] = Chord.Unbound;
        Changed?.Invoke(this, EventArgs.Empty);
        return RebindResult.Ok();
    }

    /// <summary>
    /// Restores the RightAlt chord set and clears the PTT choice, which sends the user back through
    /// the first-run picker.
    /// </summary>
    public void ResetToDefaults()
    {
        ApplyDefaults();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Marks every bound chord's key, and RightAlt, in a hook arming table. The four navigation
    /// keys are EXCLUDED unless a hold key is down: they sit on WASD, and arming them outside a
    /// hold would put the movement keys through the hook on every step the player takes.
    /// </summary>
    internal void ArmIn(bool[] table, bool holdActive = false)
    {
        var wantsRightAlt = false;
        foreach (var (action, chord) in _chords)
        {
            if (!chord.IsBound)
            {
                continue;
            }

            if (!holdActive && BindingActions.IsNavigation(action))
            {
                continue;
            }

            chord.Key.ArmIn(table);
            wantsRightAlt |= chord.Modifiers.HasFlag(BindingModifiers.RightAlt);
        }

        if (wantsRightAlt)
        {
            ModifierKeys.ArmIn(BindingModifiers.RightAlt, table);
        }
    }

    /// <summary>Marks only Panic's chord. This is the whole arming table while Panic is engaged.</summary>
    internal void ArmPanicOnlyIn(bool[] table)
    {
        var panic = _chords[BindingAction.Panic];
        panic.Key.ArmIn(table);
        if (panic.Modifiers.HasFlag(BindingModifiers.RightAlt))
        {
            ModifierKeys.ArmIn(BindingModifiers.RightAlt, table);
        }
    }

    private void ApplyDefaults()
    {
        _chords.Clear();

        // Two hold keys, both shipped bound and both rebindable. Nothing on the overlay happens
        // without one of them down, so shipping them unbound shipped a product that does nothing.
        _chords[BindingAction.Ptt] = Chord.Bare("V");

        // CapsLock: reachable by the left hand without leaving WASD, and a key no game binds. It is
        // swallowed on the way through, so holding it never toggles caps.
        _chords[BindingAction.Menu] = Chord.Bare("CapsLock");

        // Navigation sits on the fingers already on the movement keys, and is armed only while a
        // hold key is down. Rebindable like everything else.
        _chords[BindingAction.NavUp] = Chord.Bare("W");
        _chords[BindingAction.NavDown] = Chord.Bare("S");
        _chords[BindingAction.NavSelect] = Chord.Bare("D");
        _chords[BindingAction.NavBack] = Chord.Bare("A");

        _chords[BindingAction.Escape] = Chord.Bare("Escape");
        _chords[BindingAction.Board] = Chord.RightAlt("B");
        _chords[BindingAction.Panic] = Chord.RightAlt("P");
    }
}
