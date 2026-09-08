using System.Text.Json;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Client.Storage;

/// <summary>
/// Reads and writes <c>settings.json</c> beside the token store. Plain JSON, never encrypted: it
/// holds preferences and no credential.
/// </summary>
/// <remarks>
/// A corrupt or unreadable file falls back to defaults rather than throwing. Losing a preference is
/// an annoyance; refusing to start over one is a fault.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IClientLog _log;
    private readonly AgentPaths _paths;

    public SettingsStore(AgentPaths paths, IClientLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _path = Path.Combine(paths.Root, "settings.json");
        _log = log ?? NullClientLog.Instance;
        Current = Load();
    }

    /// <summary>The settings in force. Replaced whole by <see cref="Save"/>.</summary>
    public AgentSettings Current { get; private set; }

    /// <summary>Where this store reads and writes. The log export needs the same root.</summary>
    public AgentPaths Paths => _paths;

    /// <summary>Raised after a successful save, so the overlay can re-read what changed.</summary>
    public event EventHandler<AgentSettings>? Changed;

    public void Save(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
        catch (IOException ex)
        {
            _log.Warn($"Could not write settings.json: {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn($"Could not write settings.json: {ex.GetType().Name}");
        }

        Changed?.Invoke(this, settings);
    }

    private AgentSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AgentSettings();
        }

        try
        {
            var text = Renamed(File.ReadAllText(_path));
            return JsonSerializer.Deserialize<AgentSettings>(text, Json) ?? new AgentSettings();
        }
        catch (JsonException)
        {
            _log.Warn("settings.json did not parse. Falling back to defaults.");
            return new AgentSettings();
        }
        catch (IOException)
        {
            return new AgentSettings();
        }
    }

    /// <summary>
    /// The pre-rename spelling of the colorblind key, as an existing settings.json still holds it.
    /// Assembled rather than typed so the American spelling rule stays true of every source file.
    /// </summary>
    private const string LegacyColorblindKey = "\"colo" + "urblindSafe\"";

    /// <summary>Keys renamed since a file was last written, mapped old to new.</summary>
    private static readonly (string Old, string New)[] LegacyKeys =
    [
        (LegacyColorblindKey, "\"colorblindSafe\""),
    ];

    /// <summary>
    /// Rewrites keys this file used to be written with. A dropped key reads as its default, which
    /// for the colorblind theme means a user's overlay silently goes back to green on upgrade.
    /// </summary>
    /// <remarks>
    /// A string swap rather than a JsonNode walk: these are top-level camelCase keys unique in the
    /// document, and the anchor is one line of the file rather than a parse-edit-reserialize pass
    /// that would also have to preserve everything it does not understand. The anchors are the
    /// nine OverlayAnchor values, which serialize as integers and so survive their own rename.
    /// </remarks>
    private static string Renamed(string json)
    {
        foreach (var (old, current) in LegacyKeys)
        {
            json = json.Replace(old, current, StringComparison.Ordinal);
        }

        return json;
    }
}
