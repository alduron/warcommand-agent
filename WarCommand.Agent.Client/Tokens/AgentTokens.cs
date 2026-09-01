namespace WarCommand.Agent.Client.Tokens;

/// <summary>
/// The live credential pair. Every member is a secret, so <see cref="ToString"/> is overridden:
/// a record's generated ToString prints its members, and one interpolated log line would put an
/// agent token in a file on disk.
/// </summary>
public sealed record AgentTokens
{
    /// <summary>Bearer, 15 minutes.</summary>
    public required string AgentToken { get; init; }

    /// <summary>Rotating, 30 days, with reuse detection.</summary>
    public required string RefreshToken { get; init; }

    /// <summary>Local instant the agent token lapses, or null when the server did not say.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>True inside <paramref name="margin"/> of expiry, or when expiry is unknown.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan margin) =>
        ExpiresAt is null || now + margin >= ExpiresAt.Value;

    public override string ToString() => "AgentTokens { <redacted> }";
}

/// <summary>
/// A refresh token that already rotated was presented, which means the file was copied. The chain
/// is dead: the store wipes itself and the device must pair again.
/// </summary>
public sealed class TokenReuseDetectedException : Exception
{
    public TokenReuseDetectedException()
        : base("A refresh token that had already rotated was presented. The token file was copied.")
    {
    }

    public TokenReuseDetectedException(string message)
        : base(message)
    {
    }

    public TokenReuseDetectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
