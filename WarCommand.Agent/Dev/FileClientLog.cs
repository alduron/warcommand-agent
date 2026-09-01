using System.Globalization;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Storage;

namespace WarCommand.Agent.Dev;

/// <summary>
/// Appends to <c>logs\agent-dev.log</c> under the resolved <see cref="AgentPaths"/>. Not the rolling,
/// size-capped writer 10-agent-spec.md describes for the shipped agent: this is the dev loop's own
/// minimal sink, so a run can be inspected after the window closes. Never handed a token, a ticket,
/// a device token, a pairing code or a key code, per <see cref="IClientLog"/>'s own contract.
/// </summary>
public sealed class FileClientLog : IClientLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileClientLog(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogDirectory);
        _path = Path.Combine(paths.LogDirectory, "agent-dev.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message} :: {error.GetType().Name}: {error.Message}");

    private void Write(string level, string message)
    {
        var line = FormattableString.Invariant(
            $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [{level}] {message}");

        lock (_gate)
        {
            File.AppendAllLines(_path, [line]);
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
