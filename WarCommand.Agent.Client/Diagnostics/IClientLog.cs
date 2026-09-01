using System.Diagnostics.CodeAnalysis;

namespace WarCommand.Agent.Client.Diagnostics;

/// <summary>
/// The only logging seam in this assembly. No implementation may be handed a token, a ticket, a
/// device token, a pairing code or a key code: those are never passed to it in the first place.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Info, Warn and Error are the level names every log sink already uses.")]
public interface IClientLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? error = null);
}

/// <summary>Discards everything. The default, so nothing is logged unless a host asks for it.</summary>
public sealed class NullClientLog : IClientLog
{
    public static NullClientLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? error = null)
    {
    }
}
