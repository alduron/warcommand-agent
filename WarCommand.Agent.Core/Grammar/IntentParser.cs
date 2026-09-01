using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Grammar;

/// <summary>Why nothing was sent. Stable codes; the overlay maps them to text.</summary>
public static class ParseReasons
{
    /// <summary>No tokens. A stray key press, and no overlay noise.</summary>
    public const string EmptyTranscript = "empty_transcript";

    /// <summary>The alias is in forbidden_aliases and may never resolve.</summary>
    public const string ForbiddenAlias = "forbidden_alias";

    /// <summary>A modifier spoken first. Bare 'smoke' is a modifier and never a request.</summary>
    public const string ModifierOnlyNeverInitial = "modifier_only_never_initial";

    /// <summary>An adjust direction spoken first. 'left' and 'right' are never initial tokens.</summary>
    public const string AdjustDirectionNeverInitial = "adjust_direction_never_initial";

    /// <summary>Nothing in the initial class matched.</summary>
    public const string NotInVocabulary = "not_in_vocabulary";

    /// <summary>join takes exactly invite_code_digits digits, and fewer is never a slot action.</summary>
    public const string NeedSixDigits = "need_six_digits";

    /// <summary>A verb that is not legal against the current board state.</summary>
    public const string VerbNotLegalHere = "verb_not_legal_here";
}

/// <summary>Overlay prompts a parse can ask for. Nothing is sent until they are answered.</summary>
public static class ParsePrompts
{
    /// <summary>A command verb with no slot ref. Highlights claimable rows for 3 s.</summary>
    public const string WhichOne = "which_one";

    /// <summary>A request type with no point available.</summary>
    public const string SpeakGridOrTapMap = "speak_grid_or_tap_map";
}

/// <summary>Where a request's first point came from.</summary>
public enum PointSource
{
    /// <summary>The key-down snapshot from whichever coordinate source answered.</summary>
    Cursor,

    /// <summary>Digits spoken into the grammar.</summary>
    SpokenGrid,
}

/// <summary>What one utterance resolved to. Never a guess: an ambiguity is a menu.</summary>
public abstract record ParseResult
{
    private protected ParseResult()
    {
    }
}

/// <summary>A request to create, at the captured point.</summary>
public sealed record ParsedRequest : ParseResult
{
    public required string TypeId { get; init; }

    public required string OverlayLabel { get; init; }

    public required int Arity { get; init; }

    public required IReadOnlyList<string> PointLabels { get; init; }

    public required Priority Priority { get; init; }

    /// <summary>Catalog modifier ids. urgent is consumed into priority and never listed here.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    public string? SupplyKindId { get; init; }

    public string? StructureKindId { get; init; }

    /// <summary>What the count counts, from takes_quantity. Null when the type has no quantity.</summary>
    public string? QuantityUnit { get; init; }

    public int? Quantity { get; init; }

    public PointSource PointSource { get; init; } = PointSource.Cursor;

    /// <summary>Present only for a spoken grid. Its confidence is the minimum over the grid digits.</summary>
    public MapPoint? SpokenPoint { get; init; }

    /// <summary>The minimum per-token confidence over the grid digits, or null when none were spoken.</summary>
    public decimal? MinDigitConfidence { get; init; }

    /// <summary>Ordinal of the point the overlay must still prompt for, or null when arity is met.</summary>
    public int? AwaitingPointIndex => Arity > 1 ? 1 : null;

    /// <summary>Label of the point still wanted, or null.</summary>
    public string? AwaitingPointLabel =>
        AwaitingPointIndex is { } index && index < PointLabels.Count ? PointLabels[index] : null;
}

/// <summary>A verb against a row on the speaker's own board, or a board-free action.</summary>
public sealed record ParsedCommand : ParseResult
{
    public required string VerbId { get; init; }

    /// <summary>A slot_refs entry, or null when the verb takes none or none was spoken.</summary>
    public string? SlotRef { get; init; }

    /// <summary>The digit a slot ref names, or null for next, top, all.</summary>
    public int? Slot { get; init; }

    public AdjustDirection? Direction { get; init; }

    public int? Metres { get; init; }

    /// <summary>The role the role verb toggles, or null when it is a read back.</summary>
    public string? RoleId { get; init; }

    /// <summary>Six digits, on join only.</summary>
    public string? InviteCode { get; init; }

    /// <summary>What a verb with no slot ref does: show_card, set_gun_position, dismiss_transient.</summary>
    public string? Action { get; init; }

    /// <summary>A <see cref="ParsePrompts"/> code when the utterance is incomplete. Nothing is sent.</summary>
    public string? Prompt { get; init; }

    /// <summary>Never reaches the server. mute, copy and pass are local board actions.</summary>
    public bool ClientOnly { get; init; }
}

/// <summary>One item on a disambiguation menu.</summary>
public sealed record DisambiguationOption(string TypeId, string Label, string? SupplyKindId, string? StructureKindId);

/// <summary>
/// A near tie between known confusables. One keypress, and a confidently wrong request becomes a
/// half-second pause. Never resolved by confidence.
/// </summary>
public sealed record ParsedDisambiguation : ParseResult
{
    public required IReadOnlyList<DisambiguationOption> Options { get; init; }

    /// <summary>The alias that triggered it, for the overlay.</summary>
    public required string Alias { get; init; }
}

/// <summary>Nothing is sent, and the overlay says why.</summary>
public sealed record ParsedRejection : ParseResult
{
    public required string Reason { get; init; }

    public required string Transcript { get; init; }
}

/// <summary>
/// Above the noise but matching nothing, or below min_intent_confidence. The overlay shows the
/// transcript with a '?' for 2 s and sends nothing.
/// </summary>
public sealed record ParsedUnrecognized : ParseResult
{
    public required string Transcript { get; init; }

    public required double Confidence { get; init; }

    /// <summary>True when it cleared the floor and simply matched nothing. Adds SAY "HELP".</summary>
    public bool AboveFloor { get; init; }
}

/// <summary>
/// Turns an <see cref="Utterance"/> into an intent using the compiled per-class vocabulary. Pure:
/// no clock, no board mutation, no server.
/// </summary>
public sealed class IntentParser
{
    private static readonly Dictionary<string, string> NoSlotActions =
        new(StringComparer.Ordinal)
        {
            ["recall"] = "cancel_last_within_window",
            ["clear"] = "dismiss_transient",
            ["help"] = "show_card",
            ["gun"] = "set_gun_position",
        };

    private readonly Grammar _grammar;
    private readonly Catalog _catalog;
    private readonly NearFloorPairs _nearFloorPairs;
    private readonly double _minIntentConfidence;
    private readonly double _ambiguityMargin;
    private readonly int _inviteCodeDigits;

    public IntentParser(Grammar grammar, NearFloorPairs? nearFloorPairs = null, double? minIntentConfidence = null)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        _grammar = grammar;
        _catalog = grammar.Catalog;
        _nearFloorPairs = nearFloorPairs ?? NearFloorPairs.Empty;
        _minIntentConfidence = minIntentConfidence ?? _catalog.GrammarRules.MinIntentConfidence;
        _ambiguityMargin = _catalog.GrammarRules.AmbiguityMargin;
        _inviteCodeDigits = _catalog.GrammarRules.InviteCodeDigits;
    }

    /// <summary>The one entry point. Never throws on bad input; it returns a refusal.</summary>
    public ParseResult Parse(Utterance utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);

        if (utterance.IsEmpty)
        {
            return new ParsedRejection { Reason = ParseReasons.EmptyTranscript, Transcript = string.Empty };
        }

        if (utterance.Confidence < _minIntentConfidence)
        {
            return new ParsedUnrecognized { Transcript = utterance.Text, Confidence = utterance.Confidence };
        }

        var tokens = utterance.Tokens;
        var initial = _grammar.LongestMatch(PositionClass.Initial, tokens, 0);
        if (initial is null)
        {
            return RejectInitial(utterance);
        }

        return initial.Token.Kind switch
        {
            GrammarTokenKind.RequestType or GrammarTokenKind.KindShortcut => ParseRequest(utterance, initial),
            GrammarTokenKind.CommandVerb or GrammarTokenKind.VerbEntry => ParseCommand(utterance, initial),
            _ => new ParsedUnrecognized
            {
                Transcript = utterance.Text,
                Confidence = utterance.Confidence,
                AboveFloor = true,
            },
        };
    }

    private ParsedRejection RejectInitial(Utterance utterance)
    {
        var first = utterance.Tokens[0].Text;
        var whole = utterance.Text;

        // Position first: a word that is legal in another class was heard in the wrong position,
        // which is a more useful thing to say than 'forbidden'.
        if (_grammar.LongestMatch(PositionClass.AdjustDirection, utterance.Tokens, 0) is not null)
        {
            return new ParsedRejection
            {
                Reason = ParseReasons.AdjustDirectionNeverInitial,
                Transcript = whole,
            };
        }

        if (_grammar.LongestMatch(PositionClass.Modifier, utterance.Tokens, 0) is { } modifier
            && modifier.Token.Kind is GrammarTokenKind.Modifier or GrammarTokenKind.PriorityModifier)
        {
            return new ParsedRejection
            {
                Reason = ParseReasons.ModifierOnlyNeverInitial,
                Transcript = whole,
            };
        }

        foreach (var candidate in new[] { whole, first })
        {
            if (_catalog.ForbiddenAliases.ContainsKey(candidate))
            {
                return new ParsedRejection { Reason = ParseReasons.ForbiddenAlias, Transcript = candidate };
            }
        }

        return new ParsedRejection { Reason = ParseReasons.NotInVocabulary, Transcript = whole };
    }

    private ParseResult ParseRequest(Utterance utterance, GrammarMatch initial)
    {
        var type = _catalog.RequestType(initial.Token.Id!);
        if (type is null)
        {
            return new ParsedUnrecognized
            {
                Transcript = utterance.Text,
                Confidence = utterance.Confidence,
                AboveFloor = true,
            };
        }

        if (Disambiguate(utterance, initial, type) is { } menu)
        {
            return menu;
        }

        var tokens = utterance.Tokens;
        var index = initial.End;
        var priority = type.DefaultPriority;
        var modifiers = new List<string>();
        string? supplyKind = initial.Token.Kind == GrammarTokenKind.KindShortcut
            ? SupplyKindOrNull(initial.Token.SecondaryId)
            : null;
        string? structureKind = initial.Token.Kind == GrammarTokenKind.KindShortcut
            ? StructureKindOrNull(initial.Token.SecondaryId)
            : null;
        int? quantity = null;
        int? pendingNumber = null;
        MapPoint? spokenPoint = null;
        decimal? minDigitConfidence = null;

        while (index < tokens.Count)
        {
            if (_grammar.LongestMatch(PositionClass.Kind, tokens, index) is { } kindMatch
                && (type.RequiresKind || type.KindShortcutAliases))
            {
                var supply = SupplyKindOrNull(kindMatch.Token.Id);
                var structure = StructureKindOrNull(kindMatch.Token.Id);
                if (supply is not null || structure is not null)
                {
                    supplyKind = supply ?? supplyKind;
                    structureKind = structure ?? structureKind;
                    index = kindMatch.End;
                    continue;
                }
            }

            var match = _grammar.LongestMatch(PositionClass.Modifier, tokens, index);
            if (match is null)
            {
                // A written numeral is a count. Numerals stay out of the loaded vocabulary, which
                // holds spoken words only.
                if (tokens[index].Text is { Length: > 0 } text
                    && char.IsAsciiDigit(text[0])
                    && NumberWords.Value(text) is { } numeral)
                {
                    pendingNumber = numeral;
                    if (type.TakesQuantity is not null)
                    {
                        quantity = numeral;
                    }
                }

                // Anything else is dropped: a mortar mission without the word 'smoke' is better
                // than no mortar mission.
                index++;
                continue;
            }

            switch (match.Token.Kind)
            {
                case GrammarTokenKind.PriorityModifier:
                    priority = Priority.Urgent;
                    break;

                case GrammarTokenKind.Modifier:
                    if (type.Modifiers.Contains(match.Token.Id!, StringComparer.Ordinal))
                    {
                        modifiers.Add(match.Token.Id!);
                        if (string.Equals(match.Token.Id, "danger_close", StringComparison.Ordinal))
                        {
                            priority = Priority.Urgent;
                        }
                    }

                    break;

                case GrammarTokenKind.Number:
                    pendingNumber = match.Token.Value;
                    if (type.TakesQuantity is not null)
                    {
                        quantity = pendingNumber;
                    }

                    break;

                case GrammarTokenKind.QuantityUnit:
                    if (string.Equals(type.TakesQuantity, match.Token.Id, StringComparison.Ordinal))
                    {
                        quantity = pendingNumber ?? 1;
                    }

                    break;

                case GrammarTokenKind.Function when string.Equals(match.Token.Id, "at", StringComparison.Ordinal):
                    var grid = GridParser.TryParse(tokens, match.End);
                    if (grid is not null)
                    {
                        var raw = string.Join(' ', tokens.Skip(grid.Start).Take(grid.TokensConsumed).Select(t => t.Text));
                        minDigitConfidence = grid.MinDigitConfidence;
                        spokenPoint = new MapPoint(grid.X, grid.Y, "spoken_grid", raw, grid.MinDigitConfidence);
                        index = grid.End;
                        continue;
                    }

                    break;

                default:
                    break;
            }

            index = match.End;
        }

        if (type.RequiresSupplyKind && supplyKind is null)
        {
            supplyKind = type.DefaultSupplyKind;
        }

        if (type.RequiresStructureKind && structureKind is null)
        {
            structureKind = type.DefaultStructureKind;
        }

        return new ParsedRequest
        {
            TypeId = type.Id,
            OverlayLabel = type.OverlayLabel,
            Arity = type.Arity,
            PointLabels = type.PointLabels,
            Priority = priority,
            Modifiers = modifiers,
            SupplyKindId = supplyKind,
            StructureKindId = structureKind,
            QuantityUnit = quantity is null ? null : type.TakesQuantity,
            Quantity = quantity,
            PointSource = spokenPoint is null ? PointSource.Cursor : PointSource.SpokenGrid,
            SpokenPoint = spokenPoint,
            MinDigitConfidence = minDigitConfidence,
        };
    }

    private ParseResult ParseCommand(Utterance utterance, GrammarMatch initial)
    {
        var verb = _catalog.CommandVerb(initial.Token.Id!);
        if (verb is null)
        {
            return new ParsedRejection { Reason = ParseReasons.VerbNotLegalHere, Transcript = utterance.Text };
        }

        var tokens = utterance.Tokens;
        var index = initial.End;

        if (string.Equals(verb.Takes, "invite_code", StringComparison.Ordinal))
        {
            var code = GridParser.TryParseDigits(tokens, index, _inviteCodeDigits, out _);
            return code is null
                ? new ParsedRejection { Reason = ParseReasons.NeedSixDigits, Transcript = utterance.Text }
                : new ParsedCommand { VerbId = verb.Id, InviteCode = code };
        }

        if (string.Equals(verb.Takes, "role_toggle", StringComparison.Ordinal))
        {
            var role = MatchRole(tokens, index);
            return new ParsedCommand
            {
                VerbId = verb.Id,
                RoleId = role,
                Action = role is null ? "read_back" : "toggle",
            };
        }

        if (verb.NoSlotRef)
        {
            return new ParsedCommand
            {
                VerbId = verb.Id,
                Action = NoSlotActions.TryGetValue(verb.Id, out var action) ? action : null,
                ClientOnly = verb.ClientOnly,
            };
        }

        var slot = _grammar.LongestMatch(PositionClass.Slot, tokens, index);
        if (slot is null)
        {
            return new ParsedCommand { VerbId = verb.Id, Prompt = ParsePrompts.WhichOne, ClientOnly = verb.ClientOnly };
        }

        index = slot.End;

        AdjustDirection? direction = null;
        int? metres = null;
        if (initial.Token.Kind == GrammarTokenKind.VerbEntry)
        {
            var directionMatch = _grammar.LongestMatch(PositionClass.AdjustDirection, tokens, index);
            if (directionMatch is null)
            {
                return new ParsedCommand
                {
                    VerbId = verb.Id,
                    SlotRef = slot.Token.Id,
                    Slot = slot.Token.Value,
                    Prompt = ParsePrompts.WhichOne,
                };
            }

            direction = ToDirection(directionMatch.Token.SecondaryId ?? directionMatch.Token.Phrase);
            index = directionMatch.End;

            if (verb.TakesMetres && index < tokens.Count)
            {
                metres = ReadNumber(tokens, index);
            }
        }

        return new ParsedCommand
        {
            VerbId = verb.Id,
            SlotRef = slot.Token.Id,
            Slot = slot.Token.Value,
            Direction = direction,
            Metres = metres,
            ClientOnly = verb.ClientOnly,
        };
    }

    private ParsedDisambiguation? Disambiguate(Utterance utterance, GrammarMatch initial, RequestTypeDef type)
    {
        var alias = initial.Token.Phrase;
        var options = new List<DisambiguationOption> { Option(type, initial.Token) };

        // An ambiguous alias is never resolved by confidence, with or without a competing
        // hypothesis. The pair list only supplies the rival's name.
        if (initial.Token.Ambiguous)
        {
            // Two items, never three: ambiguous_aliases_note in the catalog says a two-item menu,
            // and PartnersOf is ordered closest first, so the rival named is the one the pair list
            // scored nearest the floor.
            foreach (var partner in _nearFloorPairs.PartnersOf(alias))
            {
                var named = ResolveInitialAlias(partner);
                if (named is not null && !string.Equals(named.TypeId, type.Id, StringComparison.Ordinal))
                {
                    options.Add(named);
                    break;
                }
            }

            return new ParsedDisambiguation { Options = options, Alias = alias };
        }

        foreach (var alternative in utterance.Alternatives)
        {
            if (alternative.IsEmpty || Math.Abs(utterance.Confidence - alternative.Confidence) >= _ambiguityMargin)
            {
                continue;
            }

            var other = _grammar.LongestMatch(PositionClass.Initial, alternative.Tokens, 0);
            if (other is null
                || other.Token.Kind is not (GrammarTokenKind.RequestType or GrammarTokenKind.KindShortcut)
                || string.Equals(other.Token.Id, type.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var known = other.Token.Ambiguous || _nearFloorPairs.IsPair(alias, other.Token.Phrase);
            if (!known)
            {
                continue;
            }

            var rival = _catalog.RequestType(other.Token.Id!);
            if (rival is not null && !options.Exists(o => string.Equals(o.TypeId, rival.Id, StringComparison.Ordinal)))
            {
                options.Add(Option(rival, other.Token));
            }
        }

        return options.Count > 1 ? new ParsedDisambiguation { Options = options, Alias = alias } : null;
    }

    private DisambiguationOption? ResolveInitialAlias(string alias)
    {
        var token = _grammar.TokensFor(PositionClass.Initial)
            .FirstOrDefault(t => string.Equals(t.Phrase, alias, StringComparison.OrdinalIgnoreCase)
                                 && t.Kind is GrammarTokenKind.RequestType or GrammarTokenKind.KindShortcut);
        if (token?.Id is null)
        {
            return null;
        }

        var type = _catalog.RequestType(token.Id);
        return type is null ? null : Option(type, token);
    }

    private static DisambiguationOption Option(RequestTypeDef type, GrammarToken token) => new(
        type.Id,
        type.OverlayLabel,
        token.Kind == GrammarTokenKind.KindShortcut ? token.SecondaryId : null,
        null);

    private string? SupplyKindOrNull(string? kindId) =>
        kindId is not null && _catalog.SupplyKind(kindId) is not null ? kindId : null;

    private string? StructureKindOrNull(string? kindId) =>
        kindId is not null && _catalog.StructureKind(kindId) is not null ? kindId : null;

    private string? MatchRole(IReadOnlyList<RecognizedToken> tokens, int start)
    {
        GrammarToken? best = null;
        var bestWords = 0;
        foreach (var candidate in _grammar.RoleTokens)
        {
            var words = candidate.WordCount;
            if (words <= bestWords || start + words > tokens.Count)
            {
                continue;
            }

            var phrase = string.Join(' ', tokens.Skip(start).Take(words).Select(t => t.Text));
            if (string.Equals(phrase, candidate.Phrase, StringComparison.OrdinalIgnoreCase))
            {
                best = candidate;
                bestWords = words;
            }
        }

        return best?.Id;
    }

    private int? ReadNumber(IReadOnlyList<RecognizedToken> tokens, int start)
    {
        var match = _grammar.LongestMatch(PositionClass.Modifier, tokens, start);
        if (match?.Token.Kind == GrammarTokenKind.Number && match.Token.Value is { } tens)
        {
            var next = _grammar.LongestMatch(PositionClass.Modifier, tokens, match.End);
            if (NumberWords.IsTens(match.Token.Phrase)
                && next?.Token.Kind == GrammarTokenKind.Number
                && next.Token.Value is { } units and < 10)
            {
                return tens + units;
            }

            return tens;
        }

        return NumberWords.Value(tokens[start].Text);
    }

    private static AdjustDirection? ToDirection(string phrase) => phrase switch
    {
        "over" => AdjustDirection.Over,
        "short" => AdjustDirection.Short,
        "left" => AdjustDirection.Left,
        "right" => AdjustDirection.Right,
        _ => null,
    };
}
