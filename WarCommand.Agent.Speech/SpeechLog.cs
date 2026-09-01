namespace WarCommand.Agent.Speech;

/// <summary>Everything this assembly is allowed to say. Never a sample, never a transcript.</summary>
public enum SpeechEvent
{
    /// <summary>Never emitted. Present so the enum has a zero value.</summary>
    None = 0,

    /// <summary>A Vosk model load began. It is ~400 ms and never runs on the UI thread.</summary>
    ModelLoadStarted,

    /// <summary>The model is resident and recognizers can be built.</summary>
    ModelLoaded,

    /// <summary>The model directory is missing or unreadable. Voice is unavailable this session.</summary>
    ModelUnavailable,

    /// <summary>A per-class vocabulary set was compiled from the catalog and the board state.</summary>
    GrammarCompiled,

    /// <summary>The recognizer's word list changed, so the recognizer was rebuilt.</summary>
    RecognizerRebuilt,

    /// <summary>Capture opened on a device.</summary>
    CaptureOpened,

    /// <summary>Capture closed and its scratch buffers were zeroed.</summary>
    CaptureClosed,

    /// <summary>The open device went away mid-session.</summary>
    CaptureDeviceLost,

    /// <summary>Capture reopened on the system default after losing the configured device.</summary>
    CaptureFellBackToDefault,

    /// <summary>There is no input device at all. The overlay says so; nothing looks healthy.</summary>
    NoInputDevice,

    /// <summary>A PTT hold whose peak never crossed the noise floor.</summary>
    SilentHold,

    /// <summary>Enough consecutive silent holds. NO AUDIO FROM &lt;device&gt; is on the overlay.</summary>
    SilentHoldWarningRaised,

    /// <summary>A hold with audio in it. The consecutive count is back to zero.</summary>
    SilentHoldWarningCleared,

    /// <summary>A spoken grid was read back.</summary>
    ReadbackSpoken,

    /// <summary>No synthesizer or no voice is installed, so readback is off.</summary>
    ReadbackUnavailable,
}

/// <summary>
/// The only logging seam in this assembly. The detail argument carries a device or model name and
/// nothing else: no samples, no transcript, no confidence.
/// </summary>
public interface ISpeechLog
{
    /// <summary>Records that something happened.</summary>
    /// <param name="speechEvent">What happened.</param>
    /// <param name="detail">The device or model name involved, or null.</param>
    void Note(SpeechEvent speechEvent, string? detail = null);
}

/// <summary>Discards every event. The default when the composition root wires no log.</summary>
public sealed class NullSpeechLog : ISpeechLog
{
    /// <summary>The shared instance.</summary>
    public static NullSpeechLog Instance { get; } = new();

    /// <inheritdoc />
    public void Note(SpeechEvent speechEvent, string? detail = null)
    {
        // Nothing is recorded.
    }
}
