namespace WarCommand.Agent.Core.Settings;

/// <summary>Which API this agent talks to. One build, one tray icon, this switch.</summary>
public enum AgentBackend
{
    /// <summary>api.warcommand.app. The default, and what an install ships pointed at.</summary>
    Production = 0,

    /// <summary>The local stack through the dev TLS proxy. See warcommand-agent/DEVELOPING.md.</summary>
    Local,
}

/// <summary>
/// Where the backend choice is kept: one word, in the production root, whatever it selects.
/// </summary>
/// <remarks>
/// Not in settings.json, and that is the whole point. settings.json lives under the root the
/// backend chooses, so reading the choice out of it would need the choice already made. This file
/// is at the fixed root and is read before anything else exists.
/// </remarks>
public static class BackendFile
{
    /// <summary>%LOCALAPPDATA%\WarCommand\backend, whichever backend is selected.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarCommand",
        "backend");

    /// <summary>The stored choice. Production for a missing, empty or unreadable file.</summary>
    public static AgentBackend Read()
    {
        try
        {
            var raw = File.Exists(Path) ? File.ReadAllText(Path).Trim() : string.Empty;
            return Enum.TryParse<AgentBackend>(raw, ignoreCase: true, out var backend)
                ? backend
                : AgentBackend.Production;
        }
        catch (IOException)
        {
            return AgentBackend.Production;
        }
        catch (UnauthorizedAccessException)
        {
            return AgentBackend.Production;
        }
    }

    /// <summary>Stores the choice. The caller restarts; nothing re-points a live client.</summary>
    public static void Write(AgentBackend backend)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, backend.ToString());
    }
}
