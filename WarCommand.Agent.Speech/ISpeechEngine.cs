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
}
