using System.Globalization;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Grammar;

/// <summary>What one token in one position class means once it is matched.</summary>
public enum GrammarTokenKind
{
    RequestType,

    /// <summary>A supply or structure kind spoken alone. Resolves to its parent type with the kind set.</summary>
    KindShortcut,

    CommandVerb,

    /// <summary>An initial-class word that opens a verb whose aliases live in another class.</summary>
    VerbEntry,

    Modifier,

    /// <summary>A modifier that is a priority rather than a modifier id. Never both.</summary>
    PriorityModifier,

    /// <summary>The thing a takes_quantity count counts: rounds, pallets, passengers.</summary>
    QuantityUnit,

    SupplyKind,

    StructureKind,

    SlotRef,

    AdjustDirection,

    Digit,

    /// <summary>A cardinal or numeral used for a count or for metres.</summary>
    Number,

    /// <summary>at, here, point.</summary>
    Function,

    RoleName,
}

/// <summary>One legal word or phrase in one position class.</summary>
public sealed record GrammarToken
{
    /// <summary>Lowercase, single spaced, as the recognizer emits it.</summary>
    public required string Phrase { get; init; }

    public required PositionClass Class { get; init; }

    public required GrammarTokenKind Kind { get; init; }

    /// <summary>Type id, verb id, kind id, modifier id, role id, or the slot ref text.</summary>
    public string? Id { get; init; }

    /// <summary>The kind a shortcut alias sets on its parent type.</summary>
    public string? SecondaryId { get; init; }

    /// <summary>From ambiguous_aliases. Recognised, and never resolved by confidence.</summary>
    public bool Ambiguous { get; init; }

    /// <summary>Numeric reading for a digit or number token.</summary>
    public int? Value { get; init; }

    public int WordCount => Phrase.Count(c => c == ' ') + 1;
}

/// <summary>
/// What the group and the board make legal right now. The loaded grammar is smaller than the
/// catalog, and accuracy is a function of the vocabulary actually loaded.
/// </summary>
public sealed record GrammarContext
{
    /// <summary>Types whose target roles are not enabled here are absent from the vocabulary.</summary>
    public IReadOnlyCollection<string> EnabledRoleIds { get; init; } = [];

    /// <summary>An empty board means no verb that names a row. join, help and role survive it.</summary>
    public bool HasAnyRows { get; init; }

    /// <summary>No claimed rows means done, release and start are not legal.</summary>
    public bool HasClaimedRows { get; init; }

    /// <summary>rounds_away is legal only on a row the speaker started.</summary>
    public bool HasStartedRows { get; init; }

    /// <summary>adjust only on a row the speaker requested or holds the paired spotter request for.</summary>
    public bool HasAdjustableRows { get; init; }

    /// <summary>Nothing is pruned. For the collision suite, which measures the whole catalog.</summary>
    public static GrammarContext Everything { get; } = new()
    {
        HasAnyRows = true,
        HasClaimedRows = true,
        HasStartedRows = true,
        HasAdjustableRows = true,
    };

    /// <summary>The board state the verb pruning rules read.</summary>
    public static GrammarContext FromBoard(BoardState board, IReadOnlyCollection<string> enabledRoleIds)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(enabledRoleIds);

        var visible = board.Rows.Concat(board.Overflow).Concat(board.Yours).ToList();
        var held = visible.Where(r => r.IsClaimedBy(board.ViewerParticipantId)).ToList();

        return new GrammarContext
        {
            EnabledRoleIds = enabledRoleIds,
            HasAnyRows = visible.Count > 0,
            HasClaimedRows = held.Count > 0,
            HasStartedRows = held.Exists(r => r.State == RequestState.InProgress),
            HasAdjustableRows = visible.Exists(r =>
                r.RequestedByParticipantId == board.ViewerParticipantId
                || (r.RelatedRequestId is not null && held.Exists(h => h.Id == r.RelatedRequestId))),
        };
    }
}

/// <summary>
/// The compiled vocabulary, one list per position class. This is what
/// <c>ISpeechEngine.RecognizeAsync(AudioBuffer, Grammar, CancellationToken)</c> is constrained to,
/// and what the parser matches against.
/// </summary>
/// <remarks>
/// Compiling one flat vocabulary would put <c>left</c> back in contact with <c>lift</c> and the
/// collision test would not see it, because a floor computed over the whole alias set measures the
/// wrong thing.
/// </remarks>
public sealed class Grammar
{
    private static readonly Dictionary<string, string[]> ModifierSynonyms =
        new(StringComparer.Ordinal)
        {
            ["urgent"] = ["urgent", "priority"],
            ["he"] = ["high explosive", "he"],
        };

    private static readonly Dictionary<string, string[]> VerbSlotRequirement =
        new(StringComparer.Ordinal)
        {
            ["start"] = ["claimed"],
            ["done"] = ["claimed"],
            ["release"] = ["claimed"],
            ["rounds_away"] = ["started"],
            ["adjust"] = ["adjustable"],
        };

    private readonly Dictionary<PositionClass, List<GrammarToken>> _byClass = [];

    private Grammar(Catalog catalog, GrammarContext context)
    {
        Catalog = catalog;
        Context = context;
        foreach (var value in PositionClasses.All)
        {
            _byClass[value] = [];
        }
    }

    public Catalog Catalog { get; }

    public GrammarContext Context { get; }

    /// <summary>Role names, legal only after the role verb. The contract names no class for them.</summary>
    public IReadOnlyList<GrammarToken> RoleTokens { get; private set; } = [];

    /// <summary>Every distinct word the recognizer must load, across every class.</summary>
    public IReadOnlyList<string> AllWords =>
        [.. _byClass.Values
            .SelectMany(tokens => tokens)
            .Concat(RoleTokens)
            .SelectMany(t => t.Phrase.Split(' '))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal)];

    /// <summary>Every token legal at this position.</summary>
    public IReadOnlyList<GrammarToken> TokensFor(PositionClass value) => _byClass[value];

    /// <summary>The distinct phrases legal at this position.</summary>
    public IReadOnlyList<string> PhrasesFor(PositionClass value) =>
        [.. _byClass[value].Select(t => t.Phrase).Distinct(StringComparer.Ordinal)];

    public bool Contains(PositionClass value, string phrase) =>
        _byClass[value].Exists(t => string.Equals(t.Phrase, phrase, StringComparison.Ordinal));

    /// <summary>
    /// The longest legal token sequence at <paramref name="start"/>. Deterministic, with no
    /// confidence comparison: that is what makes every two-word mitigation function.
    /// </summary>
    public GrammarMatch? LongestMatch(PositionClass value, IReadOnlyList<RecognizedToken> tokens, int start)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        GrammarToken? best = null;
        var bestWords = 0;
        var bestRank = -1;

        foreach (var candidate in _byClass[value])
        {
            var words = candidate.WordCount;
            var rank = Rank(candidate);
            if (words < bestWords || (words == bestWords && rank <= bestRank) || start + words > tokens.Count)
            {
                continue;
            }

            if (Matches(candidate, tokens, start, words))
            {
                best = candidate;
                bestWords = words;
                bestRank = rank;
            }
        }

        return best is null ? null : new GrammarMatch(best, start, bestWords);
    }

    /// <summary>
    /// Compiles one vocabulary per position class from the catalog and the pruning context. Run at
    /// startup and again whenever the catalog, the enabled roles or the board state change.
    /// </summary>
    public static Grammar Compile(Catalog catalog, GrammarContext context)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(context);

        var grammar = new Grammar(catalog, context);
        grammar.AddRequestTypes();
        grammar.AddKinds();
        grammar.AddVerbs();
        grammar.AddModifiers();
        grammar.AddSlotRefs();
        grammar.AddDigits();
        grammar.AddFunctionWords();
        grammar.AddRoles();
        return grammar;
    }

    /// <summary>
    /// Tie-break at equal length. A type alias outranks a kind shortcut of the same phrase, so
    /// 'spawn point' is the transport request and bare 'spawn' is the buildable.
    /// </summary>
    private static int Rank(GrammarToken token) => token.Kind == GrammarTokenKind.KindShortcut ? 0 : 1;

    private static bool Matches(GrammarToken candidate, IReadOnlyList<RecognizedToken> tokens, int start, int words)
    {
        var phrase = candidate.Phrase;
        var offset = 0;
        for (var i = 0; i < words; i++)
        {
            var space = phrase.IndexOf(' ', offset);
            var word = space < 0 ? phrase[offset..] : phrase[offset..space];
            offset = space + 1;

            if (!string.Equals(tokens[start + i].Text, word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private bool RoleEnabled(RequestTypeDef type) =>
        Context.EnabledRoleIds.Count == 0 || type.TargetRoles.Any(Context.EnabledRoleIds.Contains);

    private void Add(GrammarToken token)
    {
        if (!_byClass[token.Class].Exists(t =>
                string.Equals(t.Phrase, token.Phrase, StringComparison.Ordinal)
                && string.Equals(t.Id, token.Id, StringComparison.Ordinal)
                && string.Equals(t.SecondaryId, token.SecondaryId, StringComparison.Ordinal)))
        {
            _byClass[token.Class].Add(token);
        }
    }

    private void AddRequestTypes()
    {
        foreach (var type in Catalog.RequestTypes.Where(RoleEnabled))
        {
            foreach (var alias in type.SpokenAliases)
            {
                Add(new GrammarToken
                {
                    Phrase = alias,
                    Class = PositionClass.Initial,
                    Kind = GrammarTokenKind.RequestType,
                    Id = type.Id,
                });
            }

            foreach (var alias in type.AmbiguousAliases)
            {
                Add(new GrammarToken
                {
                    Phrase = alias,
                    Class = PositionClass.Initial,
                    Kind = GrammarTokenKind.RequestType,
                    Id = type.Id,
                    Ambiguous = true,
                });
            }
        }
    }

    private void AddKinds()
    {
        var supplyOwners = Catalog.RequestTypes
            .Where(t => RoleEnabled(t) && t.KindShortcutAliases && (t.RequiresSupplyKind || t.DefaultSupplyKind is not null))
            .ToList();
        var structureOwners = Catalog.RequestTypes
            .Where(t => RoleEnabled(t) && t.KindShortcutAliases && !t.RequiresSupplyKind && t.DefaultSupplyKind is null)
            .ToList();

        AddKindGroup(Catalog.SupplyKinds, GrammarTokenKind.SupplyKind, supplyOwners);
        AddKindGroup(Catalog.StructureKinds, GrammarTokenKind.StructureKind, structureOwners);
    }

    private void AddKindGroup(
        IReadOnlyList<KindDef> kinds,
        GrammarTokenKind kindClass,
        IReadOnlyList<RequestTypeDef> shortcutOwners)
    {
        foreach (var kind in kinds)
        {
            foreach (var alias in kind.SpokenAliases)
            {
                Add(new GrammarToken
                {
                    Phrase = alias,
                    Class = PositionClass.Kind,
                    Kind = kindClass,
                    Id = kind.Id,
                });

                foreach (var owner in shortcutOwners)
                {
                    Add(new GrammarToken
                    {
                        Phrase = alias,
                        Class = PositionClass.Initial,
                        Kind = GrammarTokenKind.KindShortcut,
                        Id = owner.Id,
                        SecondaryId = kind.Id,
                    });
                }
            }
        }
    }

    private void AddVerbs()
    {
        foreach (var verb in Catalog.CommandVerbs)
        {
            if (!VerbLegal(verb))
            {
                continue;
            }

            var aliasClass = PositionClasses.TryParse(verb.PositionClass) ?? PositionClass.Initial;
            var aliasKind = aliasClass == PositionClass.AdjustDirection
                ? GrammarTokenKind.AdjustDirection
                : GrammarTokenKind.CommandVerb;

            foreach (var alias in verb.Aliases)
            {
                Add(new GrammarToken
                {
                    Phrase = alias,
                    Class = aliasClass,
                    Kind = aliasKind,
                    Id = verb.Id,
                    SecondaryId = aliasClass == PositionClass.AdjustDirection ? alias : null,
                });
            }

            foreach (var entry in verb.EntryAliases)
            {
                Add(new GrammarToken
                {
                    Phrase = entry,
                    Class = PositionClass.Initial,
                    Kind = GrammarTokenKind.VerbEntry,
                    Id = verb.Id,
                });
            }
        }
    }

    private bool VerbLegal(CommandVerbDef verb)
    {
        if (!VerbSlotRequirement.TryGetValue(verb.Id, out var needs))
        {
            // A verb that names no row is always legal, including on an empty board: the cold-start
            // path is somebody with nothing saying join plus six digits.
            return verb.NoSlotRef
                || string.Equals(verb.Takes, "invite_code", StringComparison.Ordinal)
                || string.Equals(verb.Takes, "role_toggle", StringComparison.Ordinal)
                || Context.HasAnyRows;
        }

        return needs[0] switch
        {
            "claimed" => Context.HasClaimedRows,
            "started" => Context.HasStartedRows,
            "adjustable" => Context.HasAdjustableRows,
            _ => Context.HasAnyRows,
        };
    }

    private void AddModifiers()
    {
        var modifiers = Catalog.RequestTypes
            .Where(RoleEnabled)
            .SelectMany(t => t.Modifiers)
            .Distinct(StringComparer.Ordinal);

        foreach (var modifier in modifiers)
        {
            var isPriority = string.Equals(modifier, "urgent", StringComparison.Ordinal);
            var phrases = ModifierSynonyms.TryGetValue(modifier, out var synonyms)
                ? synonyms
                : [modifier.Replace('_', ' ')];

            foreach (var phrase in phrases)
            {
                Add(new GrammarToken
                {
                    Phrase = phrase,
                    Class = PositionClass.Modifier,
                    Kind = isPriority ? GrammarTokenKind.PriorityModifier : GrammarTokenKind.Modifier,
                    Id = modifier,
                });
            }
        }

        var units = Catalog.RequestTypes
            .Where(RoleEnabled)
            .Select(t => t.TakesQuantity)
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            Add(new GrammarToken
            {
                Phrase = unit!,
                Class = PositionClass.Modifier,
                Kind = GrammarTokenKind.QuantityUnit,
                Id = unit,
            });

            if (unit!.EndsWith('s'))
            {
                Add(new GrammarToken
                {
                    Phrase = unit[..^1],
                    Class = PositionClass.Modifier,
                    Kind = GrammarTokenKind.QuantityUnit,
                    Id = unit,
                });
            }
        }

        foreach (var word in NumberWords.All)
        {
            Add(new GrammarToken
            {
                Phrase = word,
                Class = PositionClass.Modifier,
                Kind = GrammarTokenKind.Number,
                Id = word,
                Value = NumberWords.Value(word),
            });
        }
    }

    private void AddSlotRefs()
    {
        foreach (var slotRef in Catalog.SlotRefs)
        {
            var numeric = int.TryParse(slotRef, NumberStyles.None, CultureInfo.InvariantCulture, out var digit);
            Add(new GrammarToken
            {
                Phrase = slotRef,
                Class = PositionClass.Slot,
                Kind = GrammarTokenKind.SlotRef,
                Id = slotRef,
                Value = numeric ? digit : null,
            });

            if (numeric && digit is >= 1 and <= 9)
            {
                Add(new GrammarToken
                {
                    Phrase = NumberWords.SlotDigitWords[digit - 1],
                    Class = PositionClass.Slot,
                    Kind = GrammarTokenKind.SlotRef,
                    Id = slotRef,
                    Value = digit,
                });
            }
        }
    }

    private void AddDigits()
    {
        foreach (var word in NumberWords.CodeDigitWords)
        {
            Add(new GrammarToken
            {
                Phrase = word,
                Class = PositionClass.Digit,
                Kind = GrammarTokenKind.Digit,
                Id = word,
                Value = NumberWords.Value(word),
            });
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            Add(new GrammarToken
            {
                Phrase = digit.ToString(CultureInfo.InvariantCulture),
                Class = PositionClass.Digit,
                Kind = GrammarTokenKind.Digit,
                Id = digit.ToString(CultureInfo.InvariantCulture),
                Value = digit,
            });
        }
    }

    private void AddFunctionWords()
    {
        foreach (var phrase in new[] { "at", "here", "point" })
        {
            Add(new GrammarToken
            {
                Phrase = phrase,
                Class = PositionClass.Modifier,
                Kind = GrammarTokenKind.Function,
                Id = phrase,
            });
        }
    }

    private void AddRoles()
    {
        var roles = new List<GrammarToken>();
        foreach (var role in Catalog.Roles)
        {
            if (Context.EnabledRoleIds.Count > 0 && !Context.EnabledRoleIds.Contains(role.Id))
            {
                continue;
            }

            foreach (var phrase in new[] { role.Id.Replace('_', ' '), role.Display.ToLowerInvariant() }
                         .Distinct(StringComparer.Ordinal))
            {
                if (!roles.Exists(t => string.Equals(t.Phrase, phrase, StringComparison.Ordinal)))
                {
                    roles.Add(new GrammarToken
                    {
                        Phrase = phrase,
                        Class = PositionClass.Modifier,
                        Kind = GrammarTokenKind.RoleName,
                        Id = role.Id,
                    });
                }
            }
        }

        RoleTokens = roles;
    }
}

/// <summary>A matched token and how many spoken words it consumed.</summary>
public sealed record GrammarMatch(GrammarToken Token, int Start, int WordCount)
{
    public int End => Start + WordCount;
}
