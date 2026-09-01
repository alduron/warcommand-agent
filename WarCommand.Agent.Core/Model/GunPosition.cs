namespace WarCommand.Agent.Core.Model;

/// <summary>
/// Where one weapon is firing from. Per weapon, local only, and never sent to the server.
/// </summary>
/// <param name="WeaponId">A weapon id from contracts/ballistics.json.</param>
/// <param name="Position">The gun's map coordinate.</param>
/// <param name="SetAt">Local clock at the moment the user set it.</param>
public sealed record GunPosition(string WeaponId, MapPoint Position, DateTimeOffset SetAt)
{
    /// <summary>Past this age the solutions dim and read GUN POS STALE. The SPH-2 drives.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>True once the position is older than five minutes.</summary>
    public bool Stale(DateTimeOffset now) => Stale(now, DefaultStaleAfter);

    /// <summary>True once the position is older than <paramref name="staleAfter"/>.</summary>
    public bool Stale(DateTimeOffset now, TimeSpan staleAfter) => now - SetAt >= staleAfter;

    public TimeSpan AgeAt(DateTimeOffset now) => now - SetAt;
}
