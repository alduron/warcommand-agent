namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// Every hotkey WarCommand holds. Four bindings: the user's own push-to-talk key, Escape, and two
/// RightAlt chords. Nothing outside this enum is bindable.
/// </summary>
/// <remarks>
/// It was twelve. Eight of them opened a panel, and a panel is something you open once a session
/// from a menu that is already on screen, not something worth a chord nobody can recall under fire.
/// Everything they reached now lives under the PTT menu, which draws its own digits.
/// </remarks>
public enum BindingAction
{
    /// <summary>No binding. The result of resolving a chord WarCommand does not hold.</summary>
    None = 0,

    /// <summary>Hold to speak or to open the menu, tap to place a point. The user's own choice.</summary>
    Ptt,

    /// <summary>Discard a draft or close a panel. Never closes the overlay.</summary>
    Escape,

    /// <summary>Cycle the board: full, dim, off. One key for what was a toggle and an opacity cycle.</summary>
    Board,

    /// <summary>Suspends every hook, capture, draw and audio capture. Never foreground gated.</summary>
    Panic,
}

/// <summary>Display text for a binding. The only text a conflict message or the tray may render.</summary>
public static class BindingActions
{
    /// <summary>Every action a <see cref="BindingSet"/> holds, excluding <see cref="BindingAction.None"/>.</summary>
    public static IReadOnlyList<BindingAction> All { get; } =
    [
        BindingAction.Ptt,
        BindingAction.Escape,
        BindingAction.Board,
        BindingAction.Panic,
    ];

    /// <summary>Human label for the settings list and for the name in a refused-conflict message.</summary>
    public static string Display(BindingAction action) => action switch
    {
        BindingAction.Ptt => "Push to talk",
        BindingAction.Escape => "Discard or close",
        BindingAction.Board => "Board: full, dim, off",
        BindingAction.Panic => "Panic",
        _ => "Unbound",
    };

    /// <summary>Panic cannot be unbound. A kill switch with no key is not a kill switch.</summary>
    public static bool CanBeUnbound(BindingAction action) => action != BindingAction.Panic;
}
