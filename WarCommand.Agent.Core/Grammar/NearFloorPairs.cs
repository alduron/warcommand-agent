using System.Text.Json;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Core.Grammar;

/// <summary>One side of a scored pair. <c>Owner</c> is 'type:x', 'verb:x', 'slot:x'.</summary>
public sealed record NearFloorAlias
{
    public string Alias { get; init; } = string.Empty;

    public string? Owner { get; init; }

    /// <summary>True when this side is declared in the catalog's ambiguous_aliases.</summary>
    public bool Ambiguous { get; init; }
}

/// <summary>One entry of <c>contracts/generated/near-floor-pairs.json</c>.</summary>
public sealed record NearFloorPair
{
    /// <summary>The class both aliases live in. A pair is never scored across classes.</summary>
    public string? PositionClass { get; init; }

    /// <summary>near_floor, forced_menu, or below_floor_unresolved.</summary>
    public string? Reason { get; init; }

    /// <summary>How far above the floor the pair sits. Lower is more confusable.</summary>
    public double Score { get; init; }

    /// <summary>features, segment_distance, or nothing.</summary>
    public string? ClearedBy { get; init; }

    /// <summary>Share of phoneme segments that differ. Lower is more confusable.</summary>
    public double SegmentDistance { get; init; }

    public IReadOnlyList<string> DifferingFeatures { get; init; } = [];

    public NearFloorAlias A { get; init; } = new();

    public NearFloorAlias B { get; init; } = new();
}

/// <summary>Envelope of the generated file. Fields the agent does not read are ignored.</summary>
internal sealed record NearFloorDocument
{
    public IReadOnlyList<NearFloorPair> Pairs { get; init; } = [];
}

/// <summary>
/// The pairs the contract generator scored close to the phonetic floor, emitted as
/// <c>contracts/generated/near-floor-pairs.json</c>. The agent reads it at runtime to name the
/// rival on a disambiguation menu rather than guess between two confusables.
/// </summary>
/// <remarks>
/// The file is optional: a standalone clone may not have one. With none present the parser still
/// refuses to resolve an ambiguous alias by confidence; it simply cannot name the rival, so the
/// menu carries one option to confirm instead of two to choose between.
/// <para>
/// The index spans every position class the generator scored. Only initial-class aliases are ever
/// queried, because that is the only class in which the parser disambiguates.
/// </para>
/// </remarks>
public sealed class NearFloorPairs
{
    private readonly Dictionary<string, List<Partner>> _partners = new(StringComparer.OrdinalIgnoreCase);

    private NearFloorPairs(IReadOnlyList<NearFloorPair> pairs)
    {
        Pairs = pairs;
        foreach (var pair in pairs)
        {
            Link(pair, pair.A.Alias, pair.B.Alias);
            Link(pair, pair.B.Alias, pair.A.Alias);
        }

        foreach (var list in _partners.Values)
        {
            list.Sort(static (x, y) => x.CompareTo(y));
        }
    }

    /// <summary>No pair list. The degraded, always-safe state.</summary>
    public static NearFloorPairs Empty { get; } = new([]);

    /// <summary>Every pair as read, in file order.</summary>
    public IReadOnlyList<NearFloorPair> Pairs { get; }

    public bool IsEmpty => _partners.Count == 0;

    /// <summary>
    /// Aliases this one is confusable with, closest first. Empty when the list does not name it.
    /// </summary>
    public IReadOnlyList<string> PartnersOf(string alias) =>
        !string.IsNullOrEmpty(alias) && _partners.TryGetValue(alias, out var partners)
            ? [.. partners.Select(p => p.Alias)]
            : [];

    /// <summary>The scored entry naming both aliases, or null.</summary>
    public NearFloorPair? PairFor(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second) || !_partners.TryGetValue(first, out var list))
        {
            return null;
        }

        return list.Find(p => string.Equals(p.Alias, second, StringComparison.OrdinalIgnoreCase))?.Pair;
    }

    public bool IsPair(string first, string second) => PairFor(first, second) is not null;

    /// <summary>
    /// Parses the generated pair list: <c>{"pairs":[{"a":{"alias":..},"b":{"alias":..},..}]}</c>.
    /// A missing, empty or unreadable file degrades to <see cref="Empty"/> rather than failing the
    /// agent's startup, because a fallback that throws is not a fallback.
    /// </summary>
    public static NearFloorPairs FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        NearFloorDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<NearFloorDocument>(json, AgentJson.Options);
        }
        catch (JsonException)
        {
            return Empty;
        }

        var usable = document?.Pairs
            .Where(p => !string.IsNullOrEmpty(p.A.Alias) && !string.IsNullOrEmpty(p.B.Alias))
            .ToList();

        return usable is null || usable.Count == 0 ? Empty : new NearFloorPairs(usable);
    }

    private void Link(NearFloorPair pair, string from, string to)
    {
        if (!_partners.TryGetValue(from, out var list))
        {
            list = [];
            _partners[from] = list;
        }

        if (!list.Exists(p => string.Equals(p.Alias, to, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new Partner(to, pair));
        }
    }

    /// <summary>A rival and the entry that scored it. Ordered closest first.</summary>
    private sealed record Partner(string Alias, NearFloorPair Pair)
    {
        public int CompareTo(Partner other)
        {
            var byScore = Pair.Score.CompareTo(other.Pair.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var bySegment = Pair.SegmentDistance.CompareTo(other.Pair.SegmentDistance);
            return bySegment != 0 ? bySegment : string.CompareOrdinal(Alias, other.Alias);
        }
    }
}
