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

    public SettingsStore(AgentPaths paths, IClientLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.Root, "settings.json");
        _log = log ?? NullClientLog.Instance;
        Current = Load();
    }

    /// <summary>The settings in force. Replaced whole by <see cref="Save"/>.</summary>
    public AgentSettings Current { get; private set; }

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
            return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(_path), Json)
                ?? new AgentSettings();
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
}
