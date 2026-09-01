using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Speech;

/// <summary>
/// One catalog verb as the compiler reads it: <c>position_class</c>, <c>entry_aliases</c>,
/// <c>takes_metres</c>, <c>terminal</c> and <c>takes_quantity</c>.
/// </summary>
/// <param name="Id">Catalog verb id.</param>
/// <param name="AliasClass">Where the aliases live. Not the initial class when the verb declares one.</param>
/// <param name="Aliases">The verb's aliases, in <paramref name="AliasClass"/>.</param>
/// <param name="EntryAliases">Initial-class words that open the verb. Empty when the aliases are initial.</param>
/// <param name="TakesMetres">A trailing numeral supplies metres, so number words must be loaded.</param>
/// <param name="TakesQuantity">A trailing numeral supplies a count, so number words must be loaded.</param>
/// <param name="Terminal">Only a terminal verb closes a request.</param>
public sealed record SpeechVerb(
    string Id,
    PositionClass AliasClass,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> EntryAliases,
    bool TakesMetres,
    bool TakesQuantity,
    bool Terminal);

/// <summary>
/// One vocabulary per position class, plus the flat phrase list the recognizer is handed.
/// </summary>
/// <remarks>
/// The two are deliberately different objects. Positional legality is decided against
/// <see cref="For"/>, by <c>IntentParser</c>; <see cref="RecognizerPhrases"/> is only what the
/// decoder is allowed to emit at all, because Vosk's grammar is a loop over phrases with no notion
/// of position. Handing the decoder one flat list is therefore unavoidable, and it is also why the
/// per-class vocabularies have to exist separately rather than being derived from it.
/// </remarks>
public sealed class CompiledSpeechGrammar
{
    /// <summary>The out-of-vocabulary sink. Without it the decoder maps any speech onto a grammar word.</summary>
    public const string UnknownPhrase = "[unk]";

    private readonly Dictionary<PositionClass, SpeechVocabulary> _byClass;

    internal CompiledSpeechGrammar(
        Grammar source,
        Dictionary<PositionClass, SpeechVocabulary> byClass,
        SpeechVocabulary roles,
        IReadOnlyList<SpeechVerb> verbs,
        IReadOnlyList<string> recognizerPhrases)
    {
        Source = source;
        _byClass = byClass;
        Roles = roles;
        Verbs = verbs;
        RecognizerPhrases = recognizerPhrases;
        Fingerprint = FingerprintOf(recognizerPhrases);
    }

    /// <summary>The Core grammar this was compiled from, with its pruning context.</summary>
    public Grammar Source { get; }

    /// <summary>Role names, legal only after the role verb. The contract names no class for them.</summary>
    public SpeechVocabulary Roles { get; }

    /// <summary>Every legal verb, with the five fields the compiler reads.</summary>
    public IReadOnlyList<SpeechVerb> Verbs { get; }

    /// <summary>
    /// Every phrase the decoder may emit, ordinal ascending. The union of the classes, because the
    /// decoder has no position; nothing here weakens what <see cref="For"/> says is legal where.
    /// </summary>
    public IReadOnlyList<string> RecognizerPhrases { get; }

    /// <summary>Stable hash of <see cref="RecognizerPhrases"/>. A changed one rebuilds the recognizer.</summary>
    public string Fingerprint { get; }

    /// <summary>Distinct words across every class. What a word-list recognizer loads.</summary>
    public IReadOnlyList<string> AllWords =>
        [.. RecognizerPhrases
            .SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => !string.Equals(w, UnknownPhrase, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.Ordinal)];

    /// <summary>The vocabulary legal at one position.</summary>
    public SpeechVocabulary For(PositionClass positionClass) => _byClass[positionClass];

    /// <summary>The grammar JSON Vosk takes: a phrase array with the unknown sink appended.</summary>
    public string ToRecognizerGrammarJson() =>
        JsonSerializer.Serialize<IReadOnlyList<string>>([.. RecognizerPhrases, UnknownPhrase]);

    private static string FingerprintOf(IReadOnlyList<string> phrases)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', phrases)));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}

/// <summary>
/// Compiles the catalog into one vocabulary per position class, pruned by what the group and the
/// board make legal right now.
/// </summary>
/// <remarks>
/// Accuracy is a function of the vocabulary actually loaded, not the one on paper: types whose
/// target roles are not enabled are absent, verbs are pruned by board state, and kinds are only
/// reachable through a type that takes one. A twelve-person group running four roles carries a
/// fraction of the catalog.
/// </remarks>
public static class SpeechGrammarCompiler
{
    /// <summary>Compiles from an already-compiled Core grammar.</summary>
    public static CompiledSpeechGrammar Compile(Grammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        var byClass = new Dictionary<PositionClass, SpeechVocabulary>();
        foreach (var positionClass in PositionClasses.All)
        {
            byClass[positionClass] = new SpeechVocabulary(positionClass, grammar.PhrasesFor(positionClass));
        }

        var roles = new SpeechVocabulary(
            PositionClass.Modifier,
            grammar.RoleTokens.Select(t => t.Phrase));

        var verbs = ReadVerbs(grammar);
        var phrases = RecognizerPhrases(byClass.Values, roles, verbs);

        return new CompiledSpeechGrammar(grammar, byClass, roles, verbs, phrases);
    }

    /// <summary>Compiles straight from the catalog and a pruning context.</summary>
    public static CompiledSpeechGrammar Compile(Catalog catalog, GrammarContext context) =>
        Compile(Grammar.Compile(catalog, context));

    /// <summary>
    /// The verbs legal in this context, read as the five fields 10-agent-spec names. A verb whose
    /// aliases were pruned out of every class is not legal and is not listed.
    /// </summary>
    private static List<SpeechVerb> ReadVerbs(Grammar grammar)
    {
        var verbs = new List<SpeechVerb>();
        foreach (var def in grammar.Catalog.CommandVerbs)
        {
            var aliasClass = PositionClasses.TryParse(def.PositionClass) ?? PositionClass.Initial;
            var aliases = def.Aliases
                .Where(a => grammar.Contains(aliasClass, a))
                .ToList();
            var entries = def.EntryAliases
                .Where(a => grammar.Contains(PositionClass.Initial, a))
                .ToList();

            if (aliases.Count == 0 && entries.Count == 0)
            {
                continue;
            }

            verbs.Add(new SpeechVerb(
                def.Id,
                aliasClass,
                aliases,
                entries,
                def.TakesMetres,
                def.TakesQuantity,
                def.Terminal));
        }

        return verbs;
    }

    /// <summary>
    /// The union the decoder is handed. Number words ride in when a legal verb takes metres or a
    /// quantity: 'adjust 3 over fifty' cannot be heard if 'fifty' was never loaded.
    /// </summary>
    private static IReadOnlyList<string> RecognizerPhrases(
        IEnumerable<SpeechVocabulary> vocabularies,
        SpeechVocabulary roles,
        IReadOnlyList<SpeechVerb> verbs)
    {
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vocabulary in vocabularies)
        {
            phrases.UnionWith(vocabulary.Phrases);
        }

        phrases.UnionWith(roles.Phrases);

        if (verbs.Any(v => v.TakesMetres || v.TakesQuantity))
        {
            phrases.UnionWith(NumberWords.All);
            for (var digit = 0; digit <= 9; digit++)
            {
                phrases.Add(digit.ToString(CultureInfo.InvariantCulture));
            }
        }

        phrases.RemoveWhere(string.IsNullOrWhiteSpace);
        return [.. phrases.OrderBy(p => p, StringComparer.Ordinal)];
    }
}
