using System.Globalization;
using System.Speech.Synthesis;

namespace WarCommand.Agent.Speech.Readback;

/// <summary>
/// Readback through the Windows synthesizer.
/// </summary>
/// <remarks>
/// <c>System.Speech</c> is rejected for recognition in 05-voice-grammar.md because its accuracy on
/// non-American accents is poor and it has no offline model story that can be improved. None of
/// that applies to synthesis: this is output, it ships with Windows, and it costs no install.
/// </remarks>
public sealed class SystemSpeechReadback : ITextToSpeech
{
    private readonly SpeechSynthesizer? _synthesizer;
    private bool _disposed;

    /// <summary>Opens the synthesizer. Unavailable rather than throwing when no voice is installed.</summary>
    public SystemSpeechReadback(ISpeechLog? log = null)
    {
        var speechLog = log ?? NullSpeechLog.Instance;
        try
        {
            var synthesizer = new SpeechSynthesizer();
            if (HasVoice(synthesizer))
            {
                synthesizer.SetOutputToDefaultAudioDevice();
                _synthesizer = synthesizer;
            }
            else
            {
                synthesizer.Dispose();
                speechLog.Note(SpeechEvent.ReadbackUnavailable);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _synthesizer = null;
            speechLog.Note(SpeechEvent.ReadbackUnavailable);
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => !_disposed && _synthesizer is not null;

    /// <summary>
    /// True when an enabled voice exists for this machine. The UI culture first, then en-US: a
    /// German UI with only an English voice should still read a grid back rather than go silent.
    /// </summary>
    private static bool HasVoice(SpeechSynthesizer synthesizer) =>
        synthesizer.GetInstalledVoices(CultureInfo.CurrentUICulture).Any(v => v.Enabled)
        || synthesizer.GetInstalledVoices(CultureInfo.GetCultureInfo("en-US")).Any(v => v.Enabled);

    /// <summary>Speaking rate, -10 to 10. Readback is a grid, so it is deliberately not fast.</summary>
    public int Rate
    {
        get => _synthesizer?.Rate ?? 0;
        set
        {
            if (_synthesizer is not null)
            {
                _synthesizer.Rate = Math.Clamp(value, -10, 10);
            }
        }
    }

    /// <inheritdoc />
    public void Speak(string text)
    {
        if (_disposed || _synthesizer is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A readback that fails is worth nothing mid-match and must never take the agent down.
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        if (_disposed || _synthesizer is null)
        {
            return;
        }

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Nothing was speaking.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
        _synthesizer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
