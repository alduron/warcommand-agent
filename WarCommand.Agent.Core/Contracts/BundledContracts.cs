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
/// <para>
/// One store per contract for the whole process, built once. Each accessor used to construct a new
/// store, so every call re-read and re-validated the document: the board render did that per row,
/// once a second, on the UI thread. It also meant a served document adopted into one store was
/// invisible to the next caller.
/// </para>
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

    private static readonly Lazy<ContractStore<Catalog>> CatalogStore =
        new(() => Load<Catalog>(RequestTypesResource), isThreadSafe: true);

    private static readonly Lazy<ContractStore<GameProfile>> GameProfileStore =
        new(() => Load<GameProfile>(GameProfileResource), isThreadSafe: true);

    private static readonly Lazy<ContractStore<Ballistics>> BallisticsStore =
        new(() => Load<Ballistics>(BallisticsResource), isThreadSafe: true);

    /// <summary>The catalog in force, already validated. Throws when the bundle itself is bad.</summary>
    public static ContractStore<Catalog> Catalog() => CatalogStore.Value;

    /// <summary>The game profile in force, already validated.</summary>
    public static ContractStore<GameProfile> GameProfile() => GameProfileStore.Value;

    /// <summary>The firing tables in force, already validated.</summary>
    public static ContractStore<Ballistics> Ballistics() => BallisticsStore.Value;

    /// <summary>
    /// A fresh store off the bundle, for a caller that must not share the process-wide one.
    /// </summary>
    public static ContractStore<T> Load<T>(string resourceName)
        where T : class, IValidatableContract =>
        ContractStore.FromBundledJson<T>(Read(resourceName));
}
