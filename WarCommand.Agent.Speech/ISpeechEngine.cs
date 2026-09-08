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

    /// <summary>
    /// Opens a session constrained to an explicit phrase list rather than to the catalog.
    /// </summary>
    /// <remarks>
    /// This is the menu's route. Voice selects the option a key would select, so the vocabulary
    /// while a surface is drawn is that surface's own labels: a list the catalog has no view of,
    /// because it is whatever <c>MenuStateMachine.Options</c> is showing at this instant.
    /// </remarks>
    ISpeechSession BeginSession(IReadOnlyList<string> phrases);

    /// <summary>
    /// Transcribes against the model's own lexicon, constrained by nothing.
    /// </summary>
    /// <remarks>
    /// The near-miss route, and the only caller is a menu utterance the grammar sent to [unk].
    /// A constrained decode cannot tell 'armor' from a word it does not hold, so the fallback asks
    /// what was actually said and lets <c>MenuSpeech</c> measure that against the drawn lines.
    /// <para>
    /// Runs on its own recognizer under its own lock, never the grammar one: a session holds the
    /// grammar recognizer for the whole hold, and this is called from inside that session.
    /// </para>
    /// </remarks>
    Utterance Transcribe(ReadOnlySpan<short> samples);
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
