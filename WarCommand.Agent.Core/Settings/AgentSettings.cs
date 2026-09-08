namespace WarCommand.Agent.Core.Settings;

/// <summary>Where the overlay sits. From the Overlay tab of docs/design/mocks/TraySettings.dc.html.</summary>
public enum OverlayAnchor
{
    /// <summary>Left edge, vertically centred.</summary>
    Left = 0,

    /// <summary>Right edge, vertically centred. The default.</summary>
    Right = 1,

    TopRight = 2,

    BottomRight = 3,

    // Appended, never reordered: settings.json holds the number, so a reorder silently moves
    // everybody's overlay.
    TopLeft = 4,

    BottomLeft = 5,

    /// <summary>Top edge, horizontally centred.</summary>
    Top = 6,

    /// <summary>Bottom edge, horizontally centred.</summary>
    Bottom = 7,

    /// <summary>Both axes centred.</summary>
    Centre = 8,
}

/// <summary>The bounds of the width share, so the settings slider and the layout agree on one pair.</summary>
public static class OverlayWidth
{
    /// <summary>Roughly the old 300 px floor at 1080p. Below this the coordinate column wraps.</summary>
    public const double MinFraction = 0.16;

    public const double MaxFraction = 0.40;
}

/// <summary>Three steps, not a slider: a number nobody can name is a setting nobody tunes.</summary>
public enum OverlayOpacity
{
    Low = 0,
    Normal,
    High,
}

/// <summary>
/// What the board key cycles through. One key, three states, in this order. Transient: it never
/// rewrites <see cref="AgentSettings.Opacity"/>, which still says how bright Full is.
/// </summary>
public enum BoardStep
{
    Full = 0,
    Dim,
    Off,
}

/// <summary>What the overlay does while the game is not the foreground window.</summary>
public enum UnfocusedBehaviour
{
    /// <summary>The default.</summary>
    Hide = 0,

    /// <summary>For a deliberate second-monitor setup.</summary>
    Dim,
}

/// <summary>
/// The three things the overlay can be doing. One setting, because "on", "follows the game" and
/// "which monitor" were three booleans that could be combined into states nobody wanted.
/// </summary>
public enum OverlayMode
{
    /// <summary>
    /// Always drawing, on <see cref="AgentSettings.DisplayDeviceName"/>, whatever the game is
    /// doing. The default, and the only mode that shows anything until Wardogs ships.
    /// </summary>
    AlwaysOn = 0,

    /// <summary>
    /// Follows Wardogs: drawn only while it is the foreground window, and on its screen rather
    /// than on a monitor of its own. The mode to be in once the game exists, and the one that
    /// keeps coordinates and callsigns off a stream while alt-tabbed.
    /// </summary>
    MirrorGame,

    /// <summary>Never drawn. Second-screen mode and the board window still work.</summary>
    Hidden,
}

/// <summary>
/// The six sounds the overlay can make, each mutable on its own. From the per-event mute grid.
/// </summary>
public sealed record SoundMutes
{
    public bool BoardWentFromEmpty { get; init; } = true;

    public bool NewUrgent { get; init; } = true;

    public bool YourRequestClaimed { get; init; } = true;

    public bool ClaimSucceeded { get; init; } = true;

    public bool ClaimLostTheRace { get; init; }

    /// <summary>The master switch. False silences every event regardless of the rest.</summary>
    public bool AllSound { get; init; } = true;
}

/// <summary>
/// Everything the settings window owns, persisted as JSON beside the token store.
/// </summary>
/// <remarks>
/// Preferences only. Nothing here is a fact about the game, which lives in the served
/// game-profile, and nothing here is a credential.
/// </remarks>
public sealed record AgentSettings
{
    /// <summary>Bumped when a field is removed or its meaning changes, never for an addition.</summary>
    public int Version { get; init; } = 2;

    // Audio.

    /// <summary>Endpoint id, or null for the system default communications device.</summary>
    public string? InputDeviceId { get; init; }

    /// <summary>Endpoint id, or null for the system default. Separate from the input on purpose.</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>0 to 1.</summary>
    public double MasterVolume { get; init; } = 0.6;

    public SoundMutes Sounds { get; init; } = new();

    // Speech.

    /// <summary>Default 0.60. Headset quality varies enormously and one number cannot suit everybody.</summary>
    public double ConfidenceFloor { get; init; } = 0.60;

    /// <summary>Off by default, invaluable while somebody is learning the grammar.</summary>
    public bool ShowRecognizedText { get; init; }

    // Overlay.

    /// <summary>
    /// Always on, mirroring the game, or hidden. Defaults to mirroring Wardogs: the overlay belongs
    /// on the game's screen, and always on is the fallback for a machine the game is not on.
    /// </summary>
    public OverlayMode OverlayMode { get; init; } = OverlayMode.MirrorGame;

    /// <summary>
    /// Which monitor the overlay draws on, as a Windows device name like <c>\.\DISPLAY2</c>.
    /// Null means the primary.
    /// </summary>
    /// <remarks>
    /// Read in <see cref="Settings.OverlayMode.AlwaysOn"/> only. Mirroring the game puts the board
    /// on the game's screen by definition, so a monitor chosen there would be a setting that does
    /// nothing, which is worse than no setting.
    /// </remarks>
    public string? DisplayDeviceName { get; init; }

    /// <summary>One of the nine grid positions. Right edge, vertically centred, by default.</summary>
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.Right;

    /// <summary>
    /// Nudge along the anchor's free axis, -1 to +1. Positive is up on a vertical axis and right on
    /// a horizontal one, so +1 always travels toward the top right: Right at +1 is the top right
    /// corner, Top at -1 is the top left. A corner anchor has no free axis and ignores it.
    /// </summary>
    /// <remarks>
    /// Default +0.20, which lifts the panel a fifth of its travel off centre and out of the way of
    /// the crosshair band without leaving the right edge.
    /// </remarks>
    public double Slide { get; init; } = 0.20;

    /// <summary>
    /// Panel width as a share of the game's width. 0.20 is the mocks' 380 px at 1920.
    /// </summary>
    /// <remarks>
    /// A share rather than a pixel count: 380 px is a fifth of a 1080p picture and a fourteenth of
    /// a 32:9 one, so a pixel width that suits one screen is wrong on the next.
    /// </remarks>
    public double WidthFraction { get; init; } = 0.20;

    public OverlayOpacity Opacity { get; init; } = OverlayOpacity.Normal;

    /// <summary>Swaps the green to #4C9AFF. Urgent keeps its red.</summary>
    public bool ColourblindSafe { get; init; }

    public bool SecondScreenMode { get; init; }

    public UnfocusedBehaviour WhenUnfocused { get; init; } = UnfocusedBehaviour.Hide;

    /// <summary>On by default, and it clobbers whatever is on the clipboard.</summary>
    public bool AutoCopyOnClaim { get; init; } = true;

    // Capture.

    /// <summary>Opt-in, off by default. Binding rule 9.</summary>
    public bool ScreenCaptureEnabled { get; init; }

    /// <summary>
    /// Off: warnings and errors only. On: every step as well, for reproducing something on request.
    /// </summary>
    /// <remarks>
    /// The default file has to stay small enough that somebody will actually attach it, and long
    /// enough that it still holds last night's incident. Warnings and errors alone do both. Verbose
    /// is what a person is asked to switch on for one reproduction, not what everyone runs.
    /// </remarks>
    public bool VerboseLogging { get; init; }



    /// <summary>
    /// The chosen chord per binding action, keyed by the action's name and holding a chord label.
    /// Empty takes the defaults, which is a first run.
    /// </summary>
    /// <remarks>
    /// Without this every launch rebuilt the defaults, which leave push-to-talk unbound on purpose,
    /// and there was nowhere to put a choice: the keybinds tab could show a chord and never set one,
    /// so no hotkey could ever fire on any machine.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Bindings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The chosen HOTAS button per binding action, keyed by the action's name and holding a device
    /// button label. Separate from <see cref="Bindings"/> on purpose: a stick and a keyboard are two
    /// rows, and a pilot who binds one is not giving up the other.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecondaryBindings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The width share the overlay actually renders at, clamped to what the mocks support.</summary>
    public double ClampedWidthFraction =>
        Math.Clamp(WidthFraction, OverlayWidth.MinFraction, OverlayWidth.MaxFraction);

    /// <summary>The nudge the overlay actually renders at.</summary>
    public double ClampedSlide => Math.Clamp(Slide, -1, 1);
}
