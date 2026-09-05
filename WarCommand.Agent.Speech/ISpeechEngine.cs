using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Speech;

/// <summary>
/// Turns one PTT hold into an utterance, constrained to the compiled grammar. One method, so
/// swapping the recognizer is a composition-root change and nothing else.
/// </summary>
/// <remarks>
/// The engine never transcribes freely. Anything outside the loaded vocabulary comes back as an
/// out-of-vocabulary token or an empty utterance, which the intent parser rejects.
/// </remarks>
public interface ISpeechEngine
{
    /// <summary>
    /// Recognizes <paramref name="buffer"/> against <paramref name="grammar"/>. The buffer is read
    /// and never retained: no implementation may copy it anywhere that outlives the call.
    /// </summary>
    Task<Utterance> RecognizeAsync(AudioBuffer buffer, Grammar grammar, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a session that decodes while the key is still down, so an utterance acts the moment it
    /// is finished rather than on release.
    /// </summary>
    /// <remarks>
    /// This is what makes voice a peer of the keyboard: a panel spoken under the hold opens under
    /// the hold, and closing it is letting go, exactly as pressing its digit would be.
    /// </remarks>
    ISpeechSession BeginSession(Grammar grammar);
}

/// <summary>
/// One hold's streaming decode. Fed chunks as they arrive; each completed utterance comes back the
/// moment the recognizer decides the speaker stopped.
/// </summary>
/// <remarks>
/// Not thread safe, and not meant to be: one hold, one session, one draining task.
/// </remarks>
public interface ISpeechSession : IDisposable
{
    /// <summary>
    /// Feeds one chunk. Returns a completed utterance when the recognizer found an endpoint, else
    /// null. The samples are read and never retained.
    /// </summary>
    Utterance? Feed(ReadOnlySpan<short> samples);

    /// <summary>Whatever is still in flight when the key comes up. Null when nothing was said.</summary>
    Utterance? Final();
}
