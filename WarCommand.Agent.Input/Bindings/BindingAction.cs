namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// Every hotkey WarCommand holds. Five bindings: two hold keys, Escape, and two RightAlt chords.
/// Nothing outside this enum is bindable.
/// </summary>
/// <remarks>
/// It was twelve. Eight of them opened a panel, and a panel is something you open once a session
/// from a menu that is already on screen, not something worth a chord nobody can recall under fire.
/// Everything they reached lives under the menu, which draws its own digits.
/// <para>
/// The two hold keys are the whole control surface and they are separate on purpose. Voice and
/// keyboard are alternatives, not one key doing both jobs: sharing them meant anybody who did not
/// want to speak had no way into the menu at all. Nothing happens on the overlay without one of
/// them held down.
/// </para>
/// </remarks>
public enum BindingAction
{
    /// <summary>No binding. The result of resolving a chord WarCommand does not hold.</summary>
    None = 0,

    /// <summary>Hold to speak. Voice only; the menu has its own key.</summary>
    Ptt,

    /// <summary>Hold to work the overlay with the keyboard. Released, nothing is listening.</summary>
    Menu,

    /// <summary>Discard a draft or close a panel. Never closes the overlay.</summary>
    Escape,

    /// <summary>Cycle the board: full, dim, off. One key for what was a toggle and an opacity cycle.</summary>
    Board,

    /// <summary>Suspends every hook, capture, draw and audio capture. Never foreground gated.</summary>
    Panic,

    /// <summary>Move the highlight up. Armed only while a hold key is down.</summary>
    NavUp,

    /// <summary>Move the highlight down. Armed only while a hold key is down.</summary>
    NavDown,

    /// <summary>Commit the highlighted option. Armed only while a hold key is down.</summary>
    NavSelect,

    /// <summary>Up one level. Armed only while a hold key is down.</summary>
    NavBack,
}

/// <summary>Display text for a binding. The only text a conflict message or the tray may render.</summary>
public static class BindingActions
{
    /// <summary>Every action a <see cref="BindingSet"/> holds, excluding <see cref="BindingAction.None"/>.</summary>
    public static IReadOnlyList<BindingAction> All { get; } =
    [
        BindingAction.Ptt,
        BindingAction.Menu,
        BindingAction.Escape,
        BindingAction.Board,
        BindingAction.Panic,
        BindingAction.NavUp,
        BindingAction.NavDown,
        BindingAction.NavSelect,
        BindingAction.NavBack,
    ];

    /// <summary>
    /// The four navigation keys. Armed ONLY while a hold key is down, so W walks and D leans
    /// exactly as they always did whenever the overlay is not being driven.
    /// </summary>
    public static IReadOnlyList<BindingAction> Navigation { get; } =
    [
        BindingAction.NavUp,
        BindingAction.NavDown,
        BindingAction.NavSelect,
        BindingAction.NavBack,
    ];

    /// <summary>True for a key that only ever acts inside a held menu.</summary>
    public static bool IsNavigation(BindingAction action) => Navigation.Contains(action);

    /// <summary>Human label for the settings list and for the name in a refused-conflict message.</summary>
    public static string Display(BindingAction action) => action switch
    {
        BindingAction.Ptt => "Push to talk (hold)",
        BindingAction.Menu => "Overlay menu (hold)",
        BindingAction.Escape => "Discard or close",
        BindingAction.Board => "Board: full, dim, off",
        BindingAction.Panic => "Panic",
        BindingAction.NavUp => "Menu: up",
        BindingAction.NavDown => "Menu: down",
        BindingAction.NavSelect => "Menu: select",
        BindingAction.NavBack => "Menu: back",
        _ => "Unbound",
    };

    /// <summary>Panic cannot be unbound. A kill switch with no key is not a kill switch.</summary>
    public static bool CanBeUnbound(BindingAction action) => action != BindingAction.Panic;
}
