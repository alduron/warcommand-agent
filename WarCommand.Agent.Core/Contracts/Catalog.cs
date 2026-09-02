using System.Text.Json.Serialization;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>Two SVG path 'd' strings on a 24x24 box, stroke only. D2 is empty for one path.</summary>
public sealed record RoleIconDef
{
    public string D1 { get; init; } = string.Empty;

    public string D2 { get; init; } = string.Empty;
}

/// <summary>A role a request can be addressed to.</summary>
public sealed record RoleDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    /// <summary>Ticket prefix, 'MTR'. The ticket counter is per group and never resets.</summary>
    public required string TicketPrefix { get; init; }

    /// <summary>What the role DOES: fire, recon, move, build, medic, command. Drives the hue.</summary>
    public string? ColorGroup { get; init; }

    /// <summary>The glyph the overlay and the web both draw. Served, never compiled in.</summary>
    public RoleIconDef? Icon { get; init; }

    /// <summary>Subscribing receives every request in the subscriber's deployment. Never group-wide.</summary>
    public bool ReceivesAll { get; init; }

    /// <summary>Needs an explicit grant. Excluded from the join default even when enabled.</summary>
    public bool GrantOnly { get; init; }
}

/// <summary>A supply or structure kind a request type may require.</summary>
public sealed record KindDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public required string OverlayLabel { get; init; }

    public IReadOnlyList<string> SpokenAliases { get; init; } = [];

    public string? MenuPath { get; init; }

    /// <summary>Hammer tier, on structure kinds only.</summary>
    public string? Hammer { get; init; }
}

/// <summary>One request type. Arity, TTL, target roles and grammar all come from here.</summary>
public sealed record RequestTypeDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    /// <summary>What the overlay row reads. Never derived from Id.</summary>
    public required string OverlayLabel { get; init; }

    public required IReadOnlyList<string> TargetRoles { get; init; }

    /// <summary>Points the agent collects before it ever contacts the server.</summary>
    public required int Arity { get; init; }

    /// <summary>One label per ordinal. Length equals Arity.</summary>
    public required IReadOnlyList<string> PointLabels { get; init; }

    public required int TtlS { get; init; }

    public IReadOnlyList<string> SpokenAliases { get; init; } = [];

    /// <summary>Recognised but never resolved by confidence. Offers a two-item menu instead.</summary>
    public IReadOnlyList<string> AmbiguousAliases { get; init; } = [];

    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Names what the count counts, 'rounds'. Null on a type with no quantity.</summary>
    public string? TakesQuantity { get; init; }

    public bool RequiresSupplyKind { get; init; }

    public bool RequiresStructureKind { get; init; }

    public string? DefaultSupplyKind { get; init; }

    public string? DefaultStructureKind { get; init; }

    /// <summary>A kind alias spoken alone resolves to this type with that kind set.</summary>
    public bool KindShortcutAliases { get; init; }

    public Priority DefaultPriority { get; init; } = Priority.Normal;

    /// <summary>Weapon id from ballistics.json, or null when this type has no bracket.</summary>
    public string? ComputesSolution { get; init; }

    /// <summary>Completes the moment it is claimed. No provider report is expected.</summary>
    public bool AutoCompleteOnClaim { get; init; }

    /// <summary>category.slot leaves. A type may sit at more than one leaf.</summary>
    public IReadOnlyList<string> MenuPaths { get; init; } = [];

    [JsonIgnore]
    public bool TakesPoint => Arity > 0;

    [JsonIgnore]
    public bool RequiresKind => RequiresSupplyKind || RequiresStructureKind;
}

/// <summary>A verb spoken against an existing row, or a client-only board action.</summary>
public sealed record CommandVerbDef
{
    public required string Id { get; init; }

    /// <summary>Aliases in this verb's position class. For a verb declaring one, not the initial class.</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>Initial-class words that open this verb. Set when Aliases live in another class.</summary>
    public IReadOnlyList<string> EntryAliases { get; init; } = [];

    /// <summary>Position class the aliases belong to. Null means the initial class.</summary>
    public string? PositionClass { get; init; }

    /// <summary>What follows the verb: 'slot_ref', 'invite_code', 'role_toggle'. Null means a bare slot ref.</summary>
    public string? Takes { get; init; }

    /// <summary>The verb takes no slot ref at all.</summary>
    public bool NoSlotRef { get; init; }

    public bool TakesMetres { get; init; }

    public bool TakesQuantity { get; init; }

    /// <summary>Only a terminal verb closes a request. splash and rounds out are not terminal.</summary>
    public bool Terminal { get; init; }

    /// <summary>Never reaches the server. mute, copy and pass are local board actions.</summary>
    public bool ClientOnly { get; init; }
}

/// <summary>The tunable grammar and board constants. None of these is a literal in agent code.</summary>
public sealed record GrammarRulesDef
{
    public required int MaxSlots { get; init; }

    public bool LongestMatchWins { get; init; } = true;

    public required double AmbiguityMargin { get; init; }

    /// <summary>'least_recently_released'. There is no quarantine.</summary>
    public required string SlotAllocation { get; init; }

    public required int AcceptAllCap { get; init; }

    public IReadOnlyList<string> AcceptAllRequires { get; init; } = [];

    public required double MinIntentConfidence { get; init; }

    public required int PreviewHoldMs { get; init; }

    public required int RecallWindowS { get; init; }

    public required int AwaitingPointTimeoutS { get; init; }

    /// <summary>A press shorter than this is a tap, which adds a point to a pending draft.</summary>
    public required int TapMaxMs { get; init; }

    public required int InviteCodeDigits { get; init; }

    /// <summary>A low priority row past this loses its digit and stays open.</summary>
    public required int LowPrioritySlotResidencyS { get; init; }

    public required decimal CoalesceRadiusUnits { get; init; }

    public required int CoalesceWindowS { get; init; }
}

/// <summary>The phonetic floor the contract generator enforces. Read-only here.</summary>
public sealed record PhoneticFloorDef
{
    public string? Rule { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];

    public int MinDifferingFeatures { get; init; }

    public string? PronunciationSource { get; init; }

    public IReadOnlyDictionary<string, string> PronunciationOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// contracts/request-types.json. Roles, request types, aliases and grammar constants, served at
/// GET /v1/catalog/request-types. The speech grammar and the request menu are both compiled from it.
/// </summary>
public sealed record Catalog : IValidatableContract
{
    public required int Version { get; init; }

    public required IReadOnlyList<RoleDef> Roles { get; init; }

    public IReadOnlyList<string> DefaultEnabledRoles { get; init; } = [];

    public IReadOnlyList<KindDef> StructureKinds { get; init; } = [];

    public IReadOnlyList<KindDef> SupplyKinds { get; init; } = [];

    /// <summary>Top-level menu category to digit.</summary>
    public IReadOnlyDictionary<string, int> MenuCategories { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public required IReadOnlyList<RequestTypeDef> RequestTypes { get; init; }

    public IReadOnlyList<CommandVerbDef> CommandVerbs { get; init; } = [];

    public IReadOnlyList<string> SlotRefs { get; init; } = [];

    public PhoneticFloorDef? PhoneticFloor { get; init; }

    public required GrammarRulesDef GrammarRules { get; init; }

    /// <summary>Alias to the reason it may never be used. Read by the collision test.</summary>
    public IReadOnlyDictionary<string, string> ForbiddenAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public RoleDef? Role(string id) => Roles.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    public RequestTypeDef? RequestType(string id) =>
        RequestTypes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public CommandVerbDef? CommandVerb(string id) =>
        CommandVerbs.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));

    public KindDef? SupplyKind(string id) =>
        SupplyKinds.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

    public KindDef? StructureKind(string id) =>
        StructureKinds.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

    /// <summary>Types a member holding these role subscriptions can be shown.</summary>
    public IReadOnlyList<RequestTypeDef> TypesForRoles(IReadOnlyCollection<string> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var receivesAll = roleIds.Any(id => Role(id)?.ReceivesAll == true);
        return receivesAll
            ? RequestTypes
            : [.. RequestTypes.Where(t => t.TargetRoles.Any(roleIds.Contains))];
    }

    public void Validate(ContractValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        validation.Require(Version > 0, "catalog: version must be positive");
        validation.Require(Roles.Count > 0, "catalog: no roles");
        validation.Require(RequestTypes.Count > 0, "catalog: no request types");
        validation.RequireDistinctIds(Roles.Select(r => r.Id), "catalog.roles");
        validation.RequireDistinctIds(RequestTypes.Select(t => t.Id), "catalog.request_types");
        validation.RequireDistinctIds(CommandVerbs.Select(v => v.Id), "catalog.command_verbs");
        validation.RequireDistinctIds(SupplyKinds.Select(k => k.Id), "catalog.supply_kinds");
        validation.RequireDistinctIds(StructureKinds.Select(k => k.Id), "catalog.structure_kinds");

        var roleIds = Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var role in Roles)
        {
            validation.Require(!string.IsNullOrWhiteSpace(role.TicketPrefix), $"role {role.Id}: no ticket_prefix");
        }

        foreach (var id in DefaultEnabledRoles)
        {
            validation.Require(roleIds.Contains(id), $"default_enabled_roles: unknown role '{id}'");
        }

        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in RequestTypes)
        {
            validation.Require(!string.IsNullOrWhiteSpace(type.OverlayLabel), $"type {type.Id}: no overlay_label");
            validation.Require(type.TargetRoles.Count > 0, $"type {type.Id}: no target_roles");
            validation.Require(type.Arity >= 0, $"type {type.Id}: negative arity");
            validation.Require(
                type.PointLabels.Count == type.Arity,
                $"type {type.Id}: {type.PointLabels.Count} point_labels for arity {type.Arity}");
            validation.Require(type.TtlS > 0, $"type {type.Id}: ttl_s must be positive");
            validation.Require(
                !(type.RequiresSupplyKind && type.RequiresStructureKind),
                $"type {type.Id}: requires both a supply kind and a structure kind");

            foreach (var role in type.TargetRoles)
            {
                validation.Require(roleIds.Contains(role), $"type {type.Id}: unknown target role '{role}'");
            }

            if (type.DefaultSupplyKind is { } supply)
            {
                validation.Require(SupplyKind(supply) is not null, $"type {type.Id}: unknown supply kind '{supply}'");
            }

            if (type.DefaultStructureKind is { } structure)
            {
                validation.Require(
                    StructureKind(structure) is not null,
                    $"type {type.Id}: unknown structure kind '{structure}'");
            }

            foreach (var path in type.MenuPaths)
            {
                if (leaves.TryGetValue(path, out var owner) && !string.Equals(owner, type.Id, StringComparison.Ordinal))
                {
                    validation.Add($"menu leaf '{path}' claimed by both {owner} and {type.Id}");
                }
                else
                {
                    leaves[path] = type.Id;
                }
            }
        }

        foreach (var verb in CommandVerbs)
        {
            validation.Require(verb.Aliases.Count > 0, $"verb {verb.Id}: no aliases");
            validation.Require(
                verb.PositionClass is null || verb.EntryAliases.Count > 0,
                $"verb {verb.Id}: declares a position class but no entry_aliases, so it is unreachable");
        }

        var rules = GrammarRules;
        validation.Require(rules.MaxSlots is > 0 and <= 9, "grammar_rules: max_slots must be 1..9");
        validation.Require(rules.AcceptAllCap > 0, "grammar_rules: accept_all_cap must be positive");
        validation.Require(
            rules.MinIntentConfidence is > 0 and <= 1,
            "grammar_rules: min_intent_confidence must be in (0,1]");
        validation.Require(rules.AmbiguityMargin is >= 0 and < 1, "grammar_rules: ambiguity_margin must be in [0,1)");
        validation.Require(rules.PreviewHoldMs > 0, "grammar_rules: preview_hold_ms must be positive");
        validation.Require(rules.TapMaxMs > 0, "grammar_rules: tap_max_ms must be positive");
        validation.Require(rules.AwaitingPointTimeoutS > 0, "grammar_rules: awaiting_point_timeout_s must be positive");
        validation.Require(rules.RecallWindowS > 0, "grammar_rules: recall_window_s must be positive");
        validation.Require(rules.InviteCodeDigits > 0, "grammar_rules: invite_code_digits must be positive");
        validation.Require(
            rules.LowPrioritySlotResidencyS > 0,
            "grammar_rules: low_priority_slot_residency_s must be positive");
        validation.Require(rules.CoalesceRadiusUnits > 0, "grammar_rules: coalesce_radius_units must be positive");
        validation.Require(rules.CoalesceWindowS > 0, "grammar_rules: coalesce_window_s must be positive");
        validation.Require(
            string.Equals(rules.SlotAllocation, "least_recently_released", StringComparison.Ordinal),
            $"grammar_rules: unsupported slot_allocation '{rules.SlotAllocation}'");
    }
}
