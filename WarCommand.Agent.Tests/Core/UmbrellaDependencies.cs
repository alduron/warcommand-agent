namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// One file this suite reads from OUTSIDE this repo: the umbrella, or a sibling repo cloned beside
/// it. Locally those are simply there, because the gate runs inside the umbrella clone. In
/// release.yml they are not: the agent is checked out alone and every one of these has to be
/// fetched, placed and confirmed by hand. A dependency that is read but not declared here is the
/// shape of the bug that failed v0.4.4 after a green local push.
/// </summary>
/// <param name="Repository">owner/name of the repo the file really lives in.</param>
/// <param name="RepositoryPath">Its path INSIDE that repo, forward-slashed.</param>
/// <param name="WorkspacePath">
/// Where it has to sit relative to the directory holding warcommand-agent/, which is what
/// ContractFixtures walks up to find. Always ends with <see cref="RepositoryPath"/>.
/// </param>
/// <param name="Required">
/// True when a test FAILS without it, which is what obliges release.yml to fetch it. False when the
/// suite degrades on purpose and the file is deliberately absent from CI.
/// </param>
/// <param name="Why">One line, used in the not-found message.</param>
internal sealed record UmbrellaDependency(
    string Repository,
    string RepositoryPath,
    string WorkspacePath,
    bool Required,
    string Why);

/// <summary>
/// THE list. ContractFixtures reads an outside file only through an entry here, and
/// ReleaseWorkflowParityTests fails the build when a required entry is not wired into
/// .github/workflows/release.yml. Adding a dependency is adding a line here; there is no second
/// way in.
/// </summary>
internal static class UmbrellaDependencies
{
    public const string UmbrellaRepository = "alduron/warcommand";

    public const string ApiRepository = "alduron/warcommand-api";

    /// <summary>Shared with the web suite, never copied into this repo.</summary>
    public static readonly UmbrellaDependency RowFields = new(
        UmbrellaRepository,
        "contracts/row-fields.json",
        "contracts/row-fields.json",
        Required: true,
        "It is the row parity list this suite and the web suite both answer to.");

    /// <summary>Generated output. Without it the parser degrades and two IntentParser rows differ.</summary>
    public static readonly UmbrellaDependency NearFloorPairs = new(
        UmbrellaRepository,
        "contracts/generated/near-floor-pairs.json",
        "contracts/generated/near-floor-pairs.json",
        Required: true,
        "It is generated output, so it is not bundled with the agent.");

    /// <summary>The shared parse spec, per Convention_WarCommandUtteranceFixtureIsSharedByBothSuites.</summary>
    public static readonly UmbrellaDependency Utterances = new(
        ApiRepository,
        "tests/unit/fixtures/utterances.yaml",
        "warcommand-api/tests/unit/fixtures/utterances.yaml",
        Required: true,
        "It is the parse spec both suites read, and it is never copied or forked.");

    // The three served contracts. Bundled into WarCommand.Agent.Core, so the suite runs without
    // them; the umbrella copy is only the earlier of two checks that the bundle is current, the
    // later one being scripts/contracts.ps1 -Check in the umbrella's own gate. Deliberately NOT
    // fetched by release.yml, which is why they are not Required.
    public static readonly UmbrellaDependency RequestTypes = new(
        UmbrellaRepository,
        "contracts/request-types.json",
        "contracts/request-types.json",
        Required: false,
        "It is bundled into the agent; the umbrella copy only cross-checks the bundle.");

    public static readonly UmbrellaDependency GameProfile = new(
        UmbrellaRepository,
        "contracts/game-profile.json",
        "contracts/game-profile.json",
        Required: false,
        "It is bundled into the agent; the umbrella copy only cross-checks the bundle.");

    public static readonly UmbrellaDependency Ballistics = new(
        UmbrellaRepository,
        "contracts/ballistics.json",
        "contracts/ballistics.json",
        Required: false,
        "It is bundled into the agent; the umbrella copy only cross-checks the bundle.");

    public static IReadOnlyList<UmbrellaDependency> All { get; } =
    [
        RowFields,
        NearFloorPairs,
        Utterances,
        RequestTypes,
        GameProfile,
        Ballistics,
    ];

    /// <summary>
    /// The declared dependency at that workspace path. Throws rather than returning null: an
    /// undeclared path is the hole this list exists to close, so naming one is a failure here and
    /// not a silently missing file at the far end.
    /// </summary>
    public static UmbrellaDependency ByWorkspacePath(string workspacePath) =>
        All.FirstOrDefault(d => string.Equals(d.WorkspacePath, workspacePath, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"'{workspacePath}' is not a declared umbrella dependency. Add it to UmbrellaDependencies "
            + "and wire it into .github/workflows/release.yml; reading it any other way passes locally "
            + "and fails the release.");
}
