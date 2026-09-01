using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Speech;

/// <summary>
/// The phrases legal at one position, and nothing else. One of these per position class is what
/// keeps <c>left</c> out of the position <c>lift</c> occupies.
/// </summary>
/// <remarks>
/// A floor computed over the whole alias set measures the wrong thing, so the vocabulary is never
/// flattened: every consumer asks for a class and gets only that class.
/// </remarks>
public sealed class SpeechVocabulary
{
    private readonly HashSet<string> _phrases;
    private readonly HashSet<string> _words;

    internal SpeechVocabulary(PositionClass positionClass, IEnumerable<string> phrases)
    {
        Class = positionClass;
        _phrases = new HashSet<string>(phrases, StringComparer.OrdinalIgnoreCase);
        _words = new HashSet<string>(
            _phrases.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);
        Phrases = [.. _phrases.OrderBy(p => p, StringComparer.Ordinal)];
        Words = [.. _words.OrderBy(w => w, StringComparer.Ordinal)];
    }

    /// <summary>The position these phrases are legal at.</summary>
    public PositionClass Class { get; }

    /// <summary>Every legal phrase, one or more words each, ordinal ascending.</summary>
    public IReadOnlyList<string> Phrases { get; }

    /// <summary>Every distinct word across those phrases.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>Phrases legal here.</summary>
    public int Count => Phrases.Count;

    /// <summary>True when this exact phrase is legal at this position.</summary>
    public bool Contains(string phrase) => !string.IsNullOrEmpty(phrase) && _phrases.Contains(phrase);

    /// <summary>True when this word appears in any phrase legal at this position.</summary>
    public bool ContainsWord(string word) => !string.IsNullOrEmpty(word) && _words.Contains(word);
}
