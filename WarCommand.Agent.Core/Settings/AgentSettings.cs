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

/// <summary>What the overlay does while the game is not the foreground window.</summary>
public enum UnfocusedBehaviour
{
    /// <summary>The default.</summary>
    Hide = 0,

    /// <summary>For a deliberate second-monitor setup.</summary>
    Dim,
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
    /// The master switch for the in-game surface, on by default. Off is a real choice: a streamer
    /// or a single-monitor player who works from second-screen mode wants nothing drawn over the
    /// game, and turning every anchor and opacity into a way of hiding it is not that.
    /// </summary>
    public bool OverlayEnabled { get; init; } = true;

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
