namespace WarCommand.Agent.Core.Updates;

/// <summary>
/// A MAJOR.MINOR.PATCH version, with any pre-release or build metadata discarded after parsing.
/// </summary>
/// <remarks>
/// Ordering is on the three numbers only. A published release is always a plain triple (the
/// release workflow refuses anything else), so the suffix a local build carries never has to
/// order against one: it only has to lose, which it does because its numbers are 0.0.0.
/// </remarks>
public readonly record struct SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static SemVersion Zero { get; }

    /// <summary>Parses "1.4.0", "1.4.0-dev" and "1.4.0+abc123". Returns false for anything else.</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            core = core[..cut];
        }

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0
            || !int.TryParse(parts[1], out var minor) || minor < 0
            || !int.TryParse(parts[2], out var patch) || patch < 0)
        {
            return false;
        }

        version = new SemVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>What the API published, reduced to the fields the agent acts on.</summary>
public sealed record PublishedRelease
{
    public required string Version { get; init; }

    public string? Notes { get; init; }

    /// <summary>The installer. Must be https and must carry a digest, or it is not installable.</summary>
    public string? Url { get; init; }

    /// <summary>Lowercase hex, 64 characters.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>What the agent should do about the published release.</summary>
public enum UpdateAvailability
{
    /// <summary>Nothing published, not newer, or not installable. The tray shows no update row.</summary>
    None = 0,

    /// <summary>Newer and installable. Offer it.</summary>
    Ready,

    /// <summary>Newer and installable, but the game is running. Offer it, refuse to install now.</summary>
    WaitingForGameToClose,
}

/// <summary>The offer, once it survives every check.</summary>
public sealed record UpdateOffer
{
    public required SemVersion Version { get; init; }

    public required string Url { get; init; }

    public required string Sha256 { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Whether to offer the published release, and whether it may be installed now. Pure: the caller
/// supplies the running version and whether the game is up, so every branch is testable with no
/// socket, no registry and no game.
/// </summary>
/// <remarks>
/// This is the one place that refuses an update, and it refuses by default. The API validates the
/// manifest too, but the agent is the party about to execute the file, so it re-checks rather than
/// trusting a server that could be wrong or replaced.
/// </remarks>
public static class UpdateDecision
{
    /// <summary>Evaluates the release. <paramref name="offer"/> is null unless the result is actionable.</summary>
    public static UpdateAvailability Evaluate(
        SemVersion running,
        PublishedRelease? published,
        bool gameIsRunning,
        out UpdateOffer? offer)
    {
        offer = null;

        if (published is null || !SemVersion.TryParse(published.Version, out var candidate))
        {
            return UpdateAvailability.None;
        }

        // Never a downgrade and never a reinstall of what is already running.
        if (candidate <= running)
        {
            return UpdateAvailability.None;
        }

        // No URL or no digest means nothing to run, or nothing to check what we ran. Either way
        // there is no update, rather than an update we take on faith.
        if (!IsInstallableUrl(published.Url) || !IsSha256(published.Sha256))
        {
            return UpdateAvailability.None;
        }

        offer = new UpdateOffer
        {
            Version = candidate,
            Url = published.Url!,
            Sha256 = published.Sha256!,
            Notes = string.IsNullOrWhiteSpace(published.Notes) ? null : published.Notes,
        };

        // 10-agent-spec.md "Updates": never auto-install while the game is running. Notify, and
        // install on next launch. An installer that closes the game mid-firefight is worse than a
        // stale agent.
        return gameIsRunning ? UpdateAvailability.WaitingForGameToClose : UpdateAvailability.Ready;
    }

    /// <summary>https only. An installer fetched over http is one an attacker on the path chooses.</summary>
    public static bool IsInstallableUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;

    /// <summary>64 lowercase hex characters, which is what the release workflow writes.</summary>
    public static bool IsSha256(string? digest)
    {
        if (digest is null || digest.Length != 64)
        {
            return false;
        }

        foreach (var c in digest)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
