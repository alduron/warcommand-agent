using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Input;
using WarCommand.Agent.Speech;

namespace WarCommand.Agent.Diagnostics;

/// <summary>
/// Carries <see cref="SpeechEvent"/> into the file the settings window exports.
/// </summary>
/// <remarks>
/// Every ISpeechLog parameter in the Speech assembly is optional and nothing constructed one, so
/// the whole channel resolved to <see cref="NullSpeechLog"/>: a machine with no microphone, a lost
/// device, a missing model and five silent holds in a row all produced an export with nothing in
/// it about voice at all. That is the report we cannot answer.
/// <para>
/// A fault is WARN so it survives the default log. The rest is INFO, which a verbose session keeps
/// and which is what shows a working machine next to a broken one.
/// </para>
/// </remarks>
public sealed class SpeechLogBridge(IClientLog log) : ISpeechLog
{
    private readonly IClientLog _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public void Note(SpeechEvent speechEvent, string? detail = null)
    {
        var line = detail is { Length: > 0 } named
            ? $"Speech: {speechEvent} ({named})"
            : $"Speech: {speechEvent}";

        if (IsFault(speechEvent))
        {
            _log.Warn(line);
            return;
        }

        _log.Info(line);
    }

    /// <summary>The events that are somebody's report rather than the shape of a healthy session.</summary>
    private static bool IsFault(SpeechEvent speechEvent) => speechEvent
        is SpeechEvent.ModelUnavailable
        or SpeechEvent.NoInputDevice
        or SpeechEvent.CaptureDeviceLost
        or SpeechEvent.CaptureFellBackToDefault
        or SpeechEvent.SilentHold
        or SpeechEvent.SilentHoldWarningRaised
        or SpeechEvent.ReadbackUnavailable;
}

/// <summary>
/// Carries <see cref="InputEvent"/> into the same file.
/// </summary>
/// <remarks>
/// The same dead seam as the speech one: hooks installed and removed, panic, the game window coming
/// and going and the exclusive-fullscreen refusal were all recorded into a null sink. Half the
/// reports that read as "voice does nothing" are a hotkey that never armed, and there was no way to
/// tell them apart from an export.
/// <para>
/// <see cref="IInputLog"/> takes an enum and nothing else, so binding rule 4 holds by construction
/// here: there is no parameter a code could ride in on.
/// </para>
/// </remarks>
public sealed class InputLogBridge(IClientLog log) : IInputLog
{
    private readonly IClientLog _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public void Note(InputEvent inputEvent)
    {
        var line = $"Input: {inputEvent}";

        if (IsFault(inputEvent))
        {
            _log.Warn(line);
            return;
        }

        _log.Info(line);
    }

    private static bool IsFault(InputEvent inputEvent) => inputEvent
        is InputEvent.HookReinstalled
        or InputEvent.ExclusiveFullscreenDetected
        or InputEvent.GameWindowLost
        or InputEvent.RebindRefusedConflict
        or InputEvent.RebindAborted;
}
