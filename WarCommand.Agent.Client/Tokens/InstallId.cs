using System.Security.Cryptography;
using WarCommand.Agent.Client.Storage;

namespace WarCommand.Agent.Client.Tokens;

/// <summary>
/// install.id: 128 bits of CSPRNG as lowercase hex, written once and preserved by the installer
/// across updates so an update does not orphan the pairing.
/// </summary>
/// <remarks>
/// It is deliberately NOT a hardware fingerprint. Deriving it from a MAC address, a disk serial or
/// a machine GUID would make it a device identifier we did not ask permission to collect, would
/// change under a NIC swap, and would collide across cloned images.
/// </remarks>
public static class InstallId
{
    /// <summary>32 lowercase hex characters.</summary>
    public const int HexLength = 32;

    /// <summary>Reads install.id, minting and writing one on first run.</summary>
    public static string LoadOrCreate(AgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();

        if (File.Exists(paths.InstallIdFile))
        {
            var existing = File.ReadAllText(paths.InstallIdFile).Trim();
            if (IsWellFormed(existing))
            {
                return existing;
            }
        }

        var minted = Mint();
        File.WriteAllText(paths.InstallIdFile, minted);
        return minted;
    }

    /// <summary>A fresh 128-bit value. Callers persist it; this does not.</summary>
    public static string Mint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsWellFormed(string? value) =>
        value is { Length: HexLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
