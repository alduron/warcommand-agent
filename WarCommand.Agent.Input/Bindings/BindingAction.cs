namespace WarCommand.Agent.Input.Bindings;

/// <summary>
/// Every hotkey WarCommand holds. Ten bindings, nine chorded behind RightAlt and one chosen by the
/// user. Nothing outside this enum is bindable.
/// </summary>
public enum BindingAction
{
    /// <summary>No binding. The result of resolving a chord WarCommand does not hold.</summary>
    None = 0,

    /// <summary>Hold to speak, tap to place a point. The user's own choice; no shipped default.</summary>
    Ptt,

    /// <summary>Discard a draft or close a panel. Never closes the overlay.</summary>
    Escape,

    /// <summary>Show or hide the board.</summary>
    ToggleBoard,

    /// <summary>Cycle opacity, three levels.</summary>
    CycleOpacity,

    /// <summary>Group and match picker.</summary>
    DeploymentPicker,

    /// <summary>Roles panel.</summary>
    Roles,

    /// <summary>Participants panel.</summary>
    Participants,

    /// <summary>Set gun position at the cursor.</summary>
    GunPosition,

    /// <summary>Link a provider account. Live only while the one-time prompt is showing.</summary>
    LinkAccount,

    /// <summary>Copy the top claimable row's coordinate to the clipboard.</summary>
    CopyCoordinate,

    /// <summary>Help card.</summary>
    Help,

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
        BindingAction.ToggleBoard,
        BindingAction.CycleOpacity,
        BindingAction.DeploymentPicker,
        BindingAction.Roles,
        BindingAction.Participants,
        BindingAction.GunPosition,
        BindingAction.LinkAccount,
        BindingAction.CopyCoordinate,
        BindingAction.Help,
        BindingAction.Panic,
    ];

    /// <summary>Human label for the settings list and for the name in a refused-conflict message.</summary>
    public static string Display(BindingAction action) => action switch
    {
        BindingAction.Ptt => "Push to talk",
        BindingAction.Escape => "Discard or close",
        BindingAction.ToggleBoard => "Show or hide the board",
        BindingAction.CycleOpacity => "Cycle opacity",
        BindingAction.DeploymentPicker => "Group and match picker",
        BindingAction.Roles => "Roles",
        BindingAction.Participants => "Participants",
        BindingAction.GunPosition => "Set gun position",
        BindingAction.LinkAccount => "Link an account",
        BindingAction.CopyCoordinate => "Copy coordinate",
        BindingAction.Help => "Help",
        BindingAction.Panic => "Panic",
        _ => "Unbound",
    };

    /// <summary>Panic cannot be unbound. A kill switch with no key is not a kill switch.</summary>
    public static bool CanBeUnbound(BindingAction action) => action != BindingAction.Panic;
}
