namespace WarCommand.Agent.Speech.Readback;

/// <summary>
/// Speaks one short line. Output only: nothing here listens, and nothing here reaches the network.
/// </summary>
public interface ITextToSpeech : IDisposable
{
    /// <summary>False when no synthesizer or no voice is installed. Readback is then simply off.</summary>
    bool IsAvailable { get; }

    /// <summary>Speaks asynchronously, cancelling anything already queued.</summary>
    void Speak(string text);

    /// <summary>Drops anything queued or in progress.</summary>
    void Cancel();
}

/// <summary>Says nothing. The default, and what a machine with no voice installed gets.</summary>
public sealed class NullTextToSpeech : ITextToSpeech
{
    /// <summary>The shared instance.</summary>
    public static NullTextToSpeech Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public void Speak(string text)
    {
        // Nothing is spoken.
    }

    /// <inheritdoc />
    public void Cancel()
    {
        // Nothing is queued.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing is held.
    }
}
