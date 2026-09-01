using System.Globalization;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Speech;

/// <summary>What one PTT hold told the monitor.</summary>
public enum SilentHoldResult
{
    /// <summary>Peak crossed the noise floor. The consecutive count is back to zero.</summary>
    HadAudio,

    /// <summary>Peak never crossed the floor, and the warning threshold is not reached yet.</summary>
    Silent,

    /// <summary>This hold reached the threshold. NO AUDIO FROM &lt;device&gt; goes on the overlay.</summary>
    WarningRaised,
}

/// <summary>
/// Counts consecutive PTT holds whose peak level never crossed <c>speech.noise_floor_dbfs</c>, and
/// raises <c>NO AUDIO FROM &lt;device&gt;</c> at <c>speech.silent_holds_before_warning</c>.
/// </summary>
/// <remarks>
/// Consecutive, never cumulative: one hold where somebody thought better of it mid-press must not
/// accumulate toward a warning over a whole match. Any hold with audio in it resets the count and
/// clears the warning. A device that exists and delivers silence is otherwise indistinguishable
/// from hesitation, because the hold just opens the menu and says nothing about why.
/// </remarks>
public sealed class SilentHoldMonitor
{
    private readonly ISpeechLog _log;

    /// <summary>Reads its two numbers from the served profile. Neither is a constant here.</summary>
    public SilentHoldMonitor(SpeechSection speech, ISpeechLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(speech);
        NoiseFloorDbfs = (double)speech.NoiseFloorDbfs;
        HoldsBeforeWarning = speech.SilentHoldsBeforeWarning;
        _log = log ?? NullSpeechLog.Instance;
    }

    /// <summary>From <c>speech.noise_floor_dbfs</c>. A peak at or below this is silence.</summary>
    public double NoiseFloorDbfs { get; }

    /// <summary>From <c>speech.silent_holds_before_warning</c>.</summary>
    public int HoldsBeforeWarning { get; }

    /// <summary>Holds in a row with no audio. Reset by any hold that has some.</summary>
    public int ConsecutiveSilentHolds { get; private set; }

    /// <summary>True while the overlay carries the warning and the tray is amber.</summary>
    public bool WarningActive => Warning is not null;

    /// <summary>The overlay line, or null.</summary>
    public string? Warning { get; private set; }

    /// <summary>Records one hold from its buffer's peak. The peak is discarded with the buffer.</summary>
    public SilentHoldResult Hold(AudioBuffer buffer, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Hold(buffer.PeakDbfs, deviceName);
    }

    /// <summary>Records one hold from its peak level in dBFS.</summary>
    public SilentHoldResult Hold(double peakDbfs, string deviceName)
    {
        if (peakDbfs > NoiseFloorDbfs)
        {
            var wasWarning = WarningActive;
            ConsecutiveSilentHolds = 0;
            Warning = null;
            if (wasWarning)
            {
                _log.Note(SpeechEvent.SilentHoldWarningCleared, deviceName);
            }

            return SilentHoldResult.HadAudio;
        }

        ConsecutiveSilentHolds++;
        _log.Note(SpeechEvent.SilentHold, deviceName);

        if (ConsecutiveSilentHolds < HoldsBeforeWarning)
        {
            return SilentHoldResult.Silent;
        }

        var raised = !WarningActive;
        Warning = string.Create(
            CultureInfo.InvariantCulture,
            $"NO AUDIO FROM {(string.IsNullOrWhiteSpace(deviceName) ? "MICROPHONE" : deviceName.ToUpperInvariant())}");

        if (raised)
        {
            _log.Note(SpeechEvent.SilentHoldWarningRaised, deviceName);
        }

        return SilentHoldResult.WarningRaised;
    }

    /// <summary>Drops the count and the warning. Called when the device changes.</summary>
    public void Reset()
    {
        ConsecutiveSilentHolds = 0;
        Warning = null;
    }
}
