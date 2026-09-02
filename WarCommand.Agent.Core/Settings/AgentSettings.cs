namespace WarCommand.Agent.Core.Settings;

/// <summary>Where the overlay sits. From the Overlay tab of docs/design/mocks/TraySettings.dc.html.</summary>
public enum OverlayAnchor
{
    Left = 0,
    Right,
    TopRight,
    BottomRight,
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
    public int Version { get; init; } = 1;

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
    /// Always on, mirroring the game, or hidden. Defaults to always on, and it has to: Wardogs is
    /// not out, so mirroring it would leave the overlay correct and invisible on every machine.
    /// </summary>
    public OverlayMode OverlayMode { get; init; } = OverlayMode.AlwaysOn;

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

    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.Right;

    /// <summary>Panel width in pixels. The mocks draw 380 everywhere.</summary>
    public int WidthPx { get; init; } = 380;

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

    /// <summary>The width the overlay actually renders at, clamped to what the mocks support.</summary>
    public int ClampedWidth => Math.Clamp(WidthPx, 300, 560);
}
