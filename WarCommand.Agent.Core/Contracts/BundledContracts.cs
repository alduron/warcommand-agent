using System.Reflection;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>
/// The copies of the served contracts that ship inside the assembly. First run with no network
/// works, and a served document that fails validation falls back to one of these.
/// </summary>
/// <remarks>
/// The files are embedded, not read from disk beside the executable: a fallback a user can delete
/// or a packager can drop is not a fallback. scripts/contracts.ps1 copies them in and fails
/// -Check on a stale copy.
/// </remarks>
public static class BundledContracts
{
    /// <summary>contracts/request-types.json as shipped.</summary>
    public const string RequestTypesResource = "WarCommand.Agent.Core.Bundled.request-types.json";

    /// <summary>contracts/game-profile.json as shipped.</summary>
    public const string GameProfileResource = "WarCommand.Agent.Core.Bundled.game-profile.json";

    /// <summary>contracts/ballistics.json as shipped.</summary>
    public const string BallisticsResource = "WarCommand.Agent.Core.Bundled.ballistics.json";

    /// <summary>Every bundled resource name, in the order the agent loads them.</summary>
    public static IReadOnlyList<string> ResourceNames { get; } =
        [RequestTypesResource, GameProfileResource, BallisticsResource];

    /// <summary>Raw JSON for one bundled contract. Throws when the build did not embed it.</summary>
    public static string Read(string resourceName)
    {
        var assembly = typeof(BundledContracts).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"'{resourceName}' is not embedded in {assembly.GetName().Name}. "
                               + "Run scripts/contracts.ps1 to refresh Contracts/Bundled.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The shipped catalog, already validated. Throws when the bundle itself is bad.</summary>
    public static ContractStore<Catalog> Catalog() =>
        ContractStore.FromBundledJson<Catalog>(Read(RequestTypesResource));

    /// <summary>The shipped game profile, already validated.</summary>
    public static ContractStore<GameProfile> GameProfile() =>
        ContractStore.FromBundledJson<GameProfile>(Read(GameProfileResource));

    /// <summary>The shipped firing tables, already validated.</summary>
    public static ContractStore<Ballistics> Ballistics() =>
        ContractStore.FromBundledJson<Ballistics>(Read(BallisticsResource));
}
