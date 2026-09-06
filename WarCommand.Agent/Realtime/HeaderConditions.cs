using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Realtime;

/// <summary>Which source a standing header word belongs to. One key per source, never shared.</summary>
public enum HeaderCondition
{
    /// <summary>The event channel stalled while the socket stayed up.</summary>
    BoardStale,

    /// <summary>Another live session holds the same participant, so the digits differ per device.</summary>
    AnotherDevice,
}

/// <summary>
/// How a standing condition is guaranteed to leave the header. Every condition declares one.
/// </summary>
public enum ConditionExit
{
    /// <summary>
    /// The source repeats it on a cadence. Stops arriving, it drops on its own.
    /// </summary>
    /// <remarks>
    /// For anything derived from a running signal. A word that outlives the signal it was derived
    /// from is a lie about the current state, and the source going quiet is not evidence the
    /// condition ended, so the only safe reading is to stop claiming it.
    /// </remarks>
    Renewed,

    /// <summary>
    /// It stands until its own signal clears it, and it never survives the session it describes.
    /// </summary>
    /// <remarks>
    /// Dropped unconditionally by the deployment hop, by detaching, and by the socket leaving
    /// Connected. Those are the three moments the agent stops being able to vouch for anything it
    /// was told, so anything latched to them can be raised without a deadline.
    /// </remarks>
    Session,
}

/// <summary>
/// The standing words on the header, keyed by source, each with a guaranteed way out.
/// </summary>
/// <remarks>
/// It was one nullable string and two independent sources writing it. They clobbered each other in
/// both directions, and the header's right cell is the SAME cell the join code lives in, so a word
/// nothing ever took down sat on top of the invite code for the rest of the session. That is the
/// reported bug and this type is the reason it cannot happen again: a source may only raise under
/// its own key, and a key cannot be raised without naming how it leaves.
/// <para>
/// Not thread safe by design, exactly like <see cref="Core.Board.BoardState"/>: every caller is
/// already on the dispatcher.
/// </para>
/// </remarks>
public sealed class HeaderConditions
{
    /// <summary>
    /// How long a renewed condition outlives its last renewal before the sweep drops it.
    /// </summary>
    /// <remarks>
    /// Generous against the cadence that renews it, so an ordinary gap never blinks the word off,
    /// and short enough that a source which has genuinely stopped does not hold the cell for a
    /// session. It is a backstop, not the mechanism: the source clearing itself is the mechanism.
    /// </remarks>
    public static readonly TimeSpan RenewalGrace = TimeSpan.FromSeconds(90);

    private readonly Dictionary<HeaderCondition, Held> _standing = [];

    private readonly record struct Held(
        string Text,
        StatusSeverity Severity,
        ConditionExit Exit,
        DateTimeOffset Until);

    /// <summary>
    /// Everything standing, as strip items. Worst first, then by the order the keys are declared.
    /// </summary>
    /// <remarks>
    /// A LIST. The single-string version meant a second condition silently replaced the first, so
    /// the overlay reported one of two things wrong and gave no sign there was another.
    /// </remarks>
    public IReadOnlyList<StatusItem> Items() =>
        [.. _standing
            .OrderBy(pair => pair.Value.Severity)
            .ThenBy(pair => pair.Key)
            .Select(pair => new StatusItem(pair.Value.Text, pair.Value.Severity))];

    /// <summary>
    /// Raises one condition under its own key. Returns true when the drawn word changed.
    /// </summary>
    /// <param name="key">The source. Raising twice under one key replaces, never stacks.</param>
    /// <param name="text">What the header says.</param>
    /// <param name="exit">How it is guaranteed to leave. There is no third option.</param>
    /// <param name="now">Server time, for the renewal deadline.</param>
    public bool Raise(
        HeaderCondition key,
        string text,
        StatusSeverity severity,
        ConditionExit exit,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        _standing[key] = new Held(
            text,
            severity,
            exit,
            exit == ConditionExit.Renewed ? now + RenewalGrace : DateTimeOffset.MaxValue);
        return true;
    }

    /// <summary>Takes one condition down. Returns true when it was standing.</summary>
    public bool Clear(HeaderCondition key) => _standing.Remove(key);

    /// <summary>
    /// Drops every renewed condition whose source stopped renewing. Driven by the board tick.
    /// </summary>
    public bool Sweep(DateTimeOffset now)
    {
        var dropped = false;
        foreach (var key in _standing
            .Where(pair => pair.Value.Exit == ConditionExit.Renewed && now >= pair.Value.Until)
            .Select(pair => pair.Key)
            .ToList())
        {
            dropped |= _standing.Remove(key);
        }

        return dropped;
    }

    /// <summary>
    /// The hop, the detach, and the socket dropping. Nothing the agent was told survives these.
    /// </summary>
    public bool ClearAll()
    {
        if (_standing.Count == 0)
        {
            return false;
        }

        _standing.Clear();
        return true;
    }
}
