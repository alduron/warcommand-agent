namespace WarCommand.Agent.Client.Storage;

/// <summary>
/// Everything the agent keeps on disk, under %LOCALAPPDATA%\WarCommand. A test points this at a
/// temporary directory instead.
/// </summary>
public sealed class AgentPaths
{
    public AgentPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    /// <summary>%LOCALAPPDATA%\WarCommand.</summary>
    public static AgentPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarCommand"));

    public string Root { get; }

    /// <summary>128-bit random hex, written once, preserved by the installer across updates.</summary>
    public string InstallIdFile => Path.Combine(Root, "install.id");

    /// <summary>The server config payload plus local settings. Never a token.</summary>
    public string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>Agent and refresh tokens, DPAPI CurrentUser. The only file that holds a credential.</summary>
    public string TokensFile => Path.Combine(Root, "tokens.dat");

    /// <summary>One file per queued submit, named by its idempotency key.</summary>
    public string QueueDirectory => Path.Combine(Root, "queue");

    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Creates the root and the queue directory. Idempotent.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(QueueDirectory);
    }
}
