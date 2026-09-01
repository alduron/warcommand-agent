using System.Security.Cryptography;
using System.Text;

namespace WarCommand.Agent.Client.Tokens;

/// <summary>Encrypts tokens.dat at rest. An implementation never logs, returns or echoes plaintext.</summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// DPAPI, CurrentUser scope, so another user on the same machine cannot read tokens.dat. The
/// entropy is a constant label rather than a secret: DPAPI's key is the user profile.
/// </summary>
public sealed class DpapiTokenProtector : ITokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WarCommand.Agent.tokens.v1");

    public static DpapiTokenProtector Instance { get; } = new();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}
