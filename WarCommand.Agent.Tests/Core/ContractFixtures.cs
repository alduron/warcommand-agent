using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The real served contracts, read from the copies bundled into WarCommand.Agent.Core. Nothing here
/// invents a catalog: a grammar test against a hand-written catalog measures the test's opinion of
/// the catalog. The bundle is what the agent falls back to, so testing against it tests the fallback
/// and lets the suite pass in a standalone clone of this repo.
/// </summary>
internal static class ContractFixtures
{
    private static readonly Lazy<Catalog> LazyCatalog =
        new(() => Load<Catalog>(BundledContracts.RequestTypesResource));

    private static readonly Lazy<GameProfile> LazyProfile =
        new(() => Load<GameProfile>(BundledContracts.GameProfileResource));

    private static readonly Lazy<Ballistics> LazyBallistics =
        new(() => Load<Ballistics>(BundledContracts.BallisticsResource));

    private static readonly Lazy<string?> LazyNearFloorJson = new(() => TryReadUmbrella("contracts/generated/near-floor-pairs.json"));

    private static readonly Lazy<string?> LazyUtterancesYaml =
        new(() => TryReadUmbrella("warcommand-api/tests/unit/fixtures/utterances.yaml"));

    public static Catalog Catalog => LazyCatalog.Value;

    public static GameProfile Profile => LazyProfile.Value;

    public static Ballistics Ballistics => LazyBallistics.Value;

    /// <summary>
    /// The generated pair list, or null outside the umbrella. Generated output rather than a served
    /// contract, so it is not bundled and the parser degrades without it.
    /// </summary>
    public static string? NearFloorPairsJson => LazyNearFloorJson.Value;

    /// <summary>
    /// The shared parse spec, read from the API repo. One file, two suites: per
    /// Convention_WarCommandUtteranceFixtureIsSharedByBothSuites it is never copied or forked, so it
    /// is reachable only from inside the umbrella.
    /// </summary>
    public static string UtterancesYaml =>
        LazyUtterancesYaml.Value
        ?? throw new InvalidOperationException(
            "warcommand-api/tests/unit/fixtures/utterances.yaml was not found above the solution. It is "
            + "the shared parse spec and is never copied into this repo; run scripts/bootstrap.ps1 in "
            + "the umbrella.");

    public static GrammarRulesDef Rules => Catalog.GrammarRules;

    /// <summary>The umbrella's copy of a served contract, or null in a standalone clone.</summary>
    public static string? UmbrellaContract(string fileName) => TryReadUmbrella($"contracts/{fileName}");

    private static T Load<T>(string resourceName)
        where T : class, IValidatableContract
    {
        var validation = new ContractValidation();
        return ContractStore.Parse<T>(BundledContracts.Read(resourceName), validation)
               ?? throw new InvalidOperationException($"bundled {resourceName} did not parse: {validation}");
    }

    private static string? TryReadUmbrella(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>Board row builder. Every test names only the field it is about.</summary>
internal static class Rows
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static BoardRow A(
        Guid? id = null,
        string typeId = "mortar_fire",
        Priority priority = Priority.Normal,
        DateTimeOffset? createdAt = null,
        RequestState state = RequestState.Open,
        Guid? requester = null,
        Guid? claimant = null,
        Guid? relatedRequestId = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DeploymentId = Deployment,
        TicketCode = "MTR-1",
        TypeId = typeId,
        OverlayLabel = "MORTAR",
        TargetRoleIds = ["mortar"],
        Priority = priority,
        Points = [],
        RequestedByParticipantId = requester ?? Guid.NewGuid(),
        RequestedByCallsign = "GHOST",
        State = state,
        ClaimantParticipantId = claimant,
        ClaimantCallsign = claimant is null ? null : "BEAR",
        ExpiresAt = (createdAt ?? Epoch).AddSeconds(600),
        CreatedAt = createdAt ?? Epoch,
        Version = 1,
        RelatedRequestId = relatedRequestId,
    };

    public static Guid Deployment { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Guid Viewer { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
}
