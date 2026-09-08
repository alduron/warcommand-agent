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

    private static readonly Lazy<string?> LazyNearFloorJson =
        new(() => TryRead(UmbrellaDependencies.NearFloorPairs));

    private static readonly Lazy<string?> LazyUtterancesYaml =
        new(() => TryRead(UmbrellaDependencies.Utterances));

    private static readonly Lazy<string?> LazyRowFieldsJson =
        new(() => TryRead(UmbrellaDependencies.RowFields));

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
    public static string UtterancesYaml => Require(UmbrellaDependencies.Utterances, LazyUtterancesYaml.Value);

    public static GrammarRulesDef Rules => Catalog.GrammarRules;

    /// <summary>
    /// The row-field parity list. Not a served contract and not bundled: it is the shared fixture
    /// the web suite reads too, so it is reachable only from inside the umbrella.
    /// </summary>
    public static string RowFieldsJson => Require(UmbrellaDependencies.RowFields, LazyRowFieldsJson.Value);

    /// <summary>
    /// The contents of a DECLARED outside file, or null in a standalone clone. Taking the
    /// dependency rather than a path is the point: a caller cannot name a file that release.yml
    /// knows nothing about, which is what made a green local push ship a red release.
    /// </summary>
    public static string? TryRead(UmbrellaDependency dependency)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = dependency.WorkspacePath.Replace('/', Path.DirectorySeparatorChar);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string Require(UmbrellaDependency dependency, string? content) =>
        content
        ?? throw new InvalidOperationException(
            $"{dependency.WorkspacePath} was not found above the solution. {dependency.Why} Run "
            + "scripts/bootstrap.ps1 in the umbrella, and see UmbrellaDependencies for how CI fetches it.");

    private static T Load<T>(string resourceName)
        where T : class, IValidatableContract
    {
        var validation = new ContractValidation();
        return ContractStore.Parse<T>(BundledContracts.Read(resourceName), validation)
               ?? throw new InvalidOperationException($"bundled {resourceName} did not parse: {validation}");
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
