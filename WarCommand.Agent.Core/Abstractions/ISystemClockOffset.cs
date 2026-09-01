namespace WarCommand.Agent.Core.Abstractions;

/// <summary>
/// The skew between this machine's clock and the server's. <c>expires_at</c> is server wall clock,
/// so every countdown renders <c>expires_at + offset</c>: an agent 30 s fast otherwise pulses a row
/// with half a minute left, and an agent 30 s slow shows time on a request that already expired.
/// </summary>
/// <remarks>
/// Derived from <c>ready.server_time</c> and re-derived on EVERY ready, including after every
/// reconnect. A type rather than an ambient static, so a test can hand a subsystem a known skew.
/// </remarks>
public interface ISystemClockOffset
{
    /// <summary>Local minus server. Positive when this machine's clock runs fast. Zero until derived.</summary>
    TimeSpan Offset { get; }

    /// <summary>False until the first ready frame has landed.</summary>
    bool IsDerived { get; }

    /// <summary>Local clock at the last derivation, or null.</summary>
    DateTimeOffset? DerivedAt { get; }

    /// <summary>Re-derives the offset. Called on every ready frame, never only the first.</summary>
    void DeriveFrom(DateTimeOffset serverTime, DateTimeOffset localNow);

    /// <summary>Turns a server instant, such as expires_at, into the local instant to count down to.</summary>
    DateTimeOffset ToLocal(DateTimeOffset serverInstant);

    /// <summary>Turns a local instant into the server instant it corresponds to.</summary>
    DateTimeOffset ToServer(DateTimeOffset localInstant);

    /// <summary>Time left on a server deadline, measured against this machine's clock.</summary>
    TimeSpan Remaining(DateTimeOffset serverDeadline, DateTimeOffset localNow);
}

/// <summary>The live offset. Mutated only by <see cref="DeriveFrom"/> on a ready frame.</summary>
public sealed class SystemClockOffset : ISystemClockOffset
{
    public TimeSpan Offset { get; private set; }

    public bool IsDerived { get; private set; }

    public DateTimeOffset? DerivedAt { get; private set; }

    public void DeriveFrom(DateTimeOffset serverTime, DateTimeOffset localNow)
    {
        Offset = localNow - serverTime;
        DerivedAt = localNow;
        IsDerived = true;
    }

    public DateTimeOffset ToLocal(DateTimeOffset serverInstant) => serverInstant + Offset;

    public DateTimeOffset ToServer(DateTimeOffset localInstant) => localInstant - Offset;

    public TimeSpan Remaining(DateTimeOffset serverDeadline, DateTimeOffset localNow) =>
        ToLocal(serverDeadline) - localNow;
}
