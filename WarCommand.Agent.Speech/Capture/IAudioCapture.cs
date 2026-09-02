namespace WarCommand.Agent.Speech.Capture;

/// <summary>One WASAPI capture endpoint, by its friendly name.</summary>
/// <param name="Id">Endpoint id. Stable across reboots; what settings stores.</param>
/// <param name="FriendlyName">What the tray submenu shows.</param>
/// <param name="IsDefault">True for the system default communications device.</param>
public sealed record AudioDevice(string Id, string FriendlyName, bool IsDefault);

/// <summary>What capture is doing, and whether the tray may stay green.</summary>
public enum AudioCaptureState
{
    /// <summary>Not opened.</summary>
    Closed,

    /// <summary>Open on the requested device.</summary>
    Running,

    /// <summary>The requested device went away and capture reopened on Default.</summary>
    FellBackToDefault,

    /// <summary>There is no input device at all. The PTT key must not appear to work.</summary>
    NoInputDevice,

    /// <summary>The audio client refused to start, or died and could not be replaced.</summary>
    Failed,
}

/// <summary>Capture health, which drives the tray colour and the persistent overlay line.</summary>
/// <param name="State">What capture is doing.</param>
/// <param name="DeviceName">The device in use, or null when there is none.</param>
/// <param name="Message">One line naming what happened, or null when nothing did.</param>
public sealed record AudioCaptureHealth(AudioCaptureState State, string? DeviceName, string? Message)
{
    /// <summary>Nothing is wrong. Closed is not a fault: the agent may simply be unarmed.</summary>
    public bool IsHealthy => State is AudioCaptureState.Closed or AudioCaptureState.Running;

    /// <summary>The tray goes amber. An agent that hears nothing must never look green.</summary>
    public bool NeedsAttention => !IsHealthy;

    /// <summary>The persistent overlay line, or null. Never a transient message.</summary>
    public string? OverlayLine => State == AudioCaptureState.NoInputDevice ? "NO MICROPHONE" : null;
}

/// <summary>Every active endpoint, capture and render, refreshed on device change.</summary>
/// <remarks>
/// Render endpoints are here for the settings list and the sound output row. Enumerating an
/// endpoint opens nothing and captures nothing: the names come from the shell's device enumerator,
/// and audio only ever moves once <see cref="IAudioCapture.Open"/> is called.
/// </remarks>
public interface IAudioDeviceCatalog
{
    /// <summary>Active capture endpoints, Default first when one exists.</summary>
    IReadOnlyList<AudioDevice> Inputs { get; }

    /// <summary>The system default communications capture device, or null when there is none.</summary>
    AudioDevice? DefaultInput { get; }

    /// <summary>Active render endpoints, Default first when one exists.</summary>
    IReadOnlyList<AudioDevice> Outputs { get; }

    /// <summary>The system default render device, or null when there is none.</summary>
    AudioDevice? DefaultOutput { get; }
}

/// <summary>
/// The microphone, opened once and held for the session. Audio only ever moves from here into an
/// <see cref="AudioBuffer"/>: there is no sink that writes it anywhere else.
/// </summary>
/// <remarks>
/// Device loss is handled live. Unplugging a USB headset mid-match falls back to Default, reports
/// it, and turns the tray amber rather than silently killing recognition.
/// </remarks>
public interface IAudioCapture : IDisposable
{
    /// <summary>The device in use, or null when capture is closed or there is none.</summary>
    AudioDevice? Device { get; }

    /// <summary>Current health. Never <c>Running</c> when nothing is actually being captured.</summary>
    AudioCaptureHealth Health { get; }

    /// <summary>Live input peak in dBFS since the last read, for the settings meter.</summary>
    double LevelDbfs { get; }

    /// <summary>True between <see cref="BeginHold"/> and <see cref="EndHold"/>.</summary>
    bool IsHolding { get; }

    /// <summary>Raised on every health change, including the fallback to Default.</summary>
    event EventHandler<AudioCaptureHealth>? HealthChanged;

    /// <summary>Opens the named endpoint, or the system default when <paramref name="deviceId"/> is null.</summary>
    void Open(string? deviceId);

    /// <summary>Stops capture and zeroes every scratch buffer it held.</summary>
    void Close();

    /// <summary>Routes captured samples into <paramref name="destination"/> until the hold ends.</summary>
    void BeginHold(AudioBuffer destination);

    /// <summary>Stops routing. The buffer keeps what it took, including its peak.</summary>
    void EndHold();
}
