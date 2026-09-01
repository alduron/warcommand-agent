namespace WarCommand.Agent.Core.Abstractions;

/// <summary>
/// This machine's clock. Injected rather than called, so every timing rule in Core is testable with
/// no wall clock. The slot allocator and the state machines take <c>now</c> as a parameter and do
/// not depend on this at all.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. The only implementation that reads the machine.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
