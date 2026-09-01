using System.Text.Json;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>Collects the reasons a served contract was refused. Empty means valid.</summary>
public sealed class ContractValidation
{
    private readonly List<string> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<string> Errors => _errors;

    public void Add(string error) => _errors.Add(error);

    /// <summary>Records <paramref name="error"/> when <paramref name="condition"/> is false.</summary>
    public void Require(bool condition, string error)
    {
        if (!condition)
        {
            _errors.Add(error);
        }
    }

    /// <summary>Requires every id in <paramref name="ids"/> to be distinct and non-empty.</summary>
    public void RequireDistinctIds(IEnumerable<string> ids, string what)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _errors.Add($"{what}: empty id");
            }
            else if (!seen.Add(id))
            {
                _errors.Add($"{what}: duplicate id '{id}'");
            }
        }
    }

    public override string ToString() => string.Join("; ", _errors);
}

/// <summary>A served contract validates itself before it may be adopted.</summary>
public interface IValidatableContract
{
    void Validate(ContractValidation validation);
}

/// <summary>Outcome of one adoption attempt. Errors are empty only when Adopted is true.</summary>
public sealed record ContractAdoption(bool Adopted, IReadOnlyList<string> Errors)
{
    public static ContractAdoption Success { get; } = new(true, []);

    public static ContractAdoption Unchanged { get; } = new(false, []);

    public static ContractAdoption Rejected(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// Holds the current copy of one served contract. A bundled copy ships with the agent so first run
/// with no network works; a served document that fails validation is refused and the previous copy
/// is kept, because a bad data push must not brick a fleet of agents mid-match.
/// </summary>
/// <typeparam name="T">The contract document type.</typeparam>
public sealed class ContractStore<T>
    where T : class, IValidatableContract
{
    /// <summary>
    /// Takes the copy shipped with the agent. Throws when the bundle itself is invalid: that is a
    /// build fault, not a runtime one, and there is nothing left to fall back to.
    /// </summary>
    public ContractStore(T bundled)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        var validation = new ContractValidation();
        bundled.Validate(validation);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Bundled {typeof(T).Name} is invalid: {validation}");
        }

        Current = bundled;
        Bundled = bundled;
    }

    /// <summary>The document in force. Never null and never an invalid one.</summary>
    public T Current { get; private set; }

    /// <summary>The copy shipped with the agent. Reachable for a forced fall back.</summary>
    public T Bundled { get; }

    /// <summary>ETag of the served copy in force, or null while running on the bundle.</summary>
    public string? ETag { get; private set; }

    /// <summary>True until a served document has been adopted.</summary>
    public bool IsBundledFallback { get; private set; } = true;

    /// <summary>
    /// Validates a served document and adopts it on success. On failure the previous document stays
    /// in force and the reasons come back for the log and the tray warning.
    /// </summary>
    public ContractAdoption TryAdopt(string servedJson, string? etag)
    {
        var validation = new ContractValidation();
        var parsed = ContractStore.Parse<T>(servedJson, validation);
        if (parsed is null || !validation.IsValid)
        {
            return ContractAdoption.Rejected(validation.Errors);
        }

        Current = parsed;
        ETag = etag;
        IsBundledFallback = false;
        return ContractAdoption.Success;
    }

    /// <summary>Returns to the bundled copy after a served one proved wrong in the field.</summary>
    public void RevertToBundled()
    {
        Current = Bundled;
        ETag = null;
        IsBundledFallback = true;
    }

}

/// <summary>Parsing and bundle loading for the served contracts.</summary>
public static class ContractStore
{
    /// <summary>
    /// Loads the copy shipped with the agent. Throws when the bundle itself is invalid: that is a
    /// build fault, not a runtime one, and there is nothing left to fall back to.
    /// </summary>
    public static ContractStore<T> FromBundledJson<T>(string bundledJson)
        where T : class, IValidatableContract
    {
        var validation = new ContractValidation();
        var parsed = Parse<T>(bundledJson, validation);
        if (parsed is null || !validation.IsValid)
        {
            throw new InvalidOperationException($"Bundled {typeof(T).Name} is invalid: {validation}");
        }

        return new ContractStore<T>(parsed);
    }

    /// <summary>
    /// Parses and validates one document. Accepts either the bare file or the catalog envelope
    /// <c>{"data": {...}, "version": n}</c>. Returns null with reasons in
    /// <paramref name="validation"/> rather than throwing on bad data.
    /// </summary>
    public static T? Parse<T>(string json, ContractValidation validation)
        where T : class, IValidatableContract
    {
        ArgumentNullException.ThrowIfNull(validation);

        if (string.IsNullOrWhiteSpace(json))
        {
            validation.Add("empty document");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                validation.Add($"expected a JSON object, got {root.ValueKind}");
                return null;
            }

            var body = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;

            var parsed = body.Deserialize<T>(AgentJson.Options);
            if (parsed is null)
            {
                validation.Add("document deserialised to null");
                return null;
            }

            parsed.Validate(validation);
            return parsed;
        }
        catch (JsonException ex)
        {
            validation.Add($"malformed JSON: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            validation.Add($"unreadable JSON: {ex.Message}");
            return null;
        }
    }
}
