using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Client.Tokens;

/// <summary>The credential file. Nothing here is ever written to config.json, a log or a metric.</summary>
public interface ITokenStore
{
    /// <summary>Null until the device activates or redeems a pairing intent.</summary>
    AgentTokens? Current { get; }

    /// <summary>The unpaired device credential, or null.</summary>
    string? DeviceToken { get; }

    Guid? DeviceId { get; }

    void SaveDeviceRegistration(Guid deviceId, string deviceToken);

    /// <summary>Stores the pair minted by activate or pairing claim.</summary>
    void SaveIssued(AgentTokens tokens);

    /// <summary>
    /// Hands out the refresh token to present. Throws <see cref="TokenReuseDetectedException"/>
    /// when it already rotated, which means this file is a copy.
    /// </summary>
    string BeginRotation();

    /// <summary>Records that <paramref name="presented"/> is spent and stores its replacement.</summary>
    void CompleteRotation(string presented, AgentTokens rotated);

    /// <summary>True when this refresh token has already been rotated away from.</summary>
    bool WasRotated(string refreshToken);

    /// <summary>Wipes the file. The device pairs again from scratch.</summary>
    void Clear(string reason);
}

/// <summary>
/// tokens.dat, DPAPI-encrypted with CurrentUser scope. Refresh rotation with reuse detection:
/// every rotation records the fingerprint of the token it replaced, and presenting one of those
/// again means the file was stolen rather than expired.
/// </summary>
public sealed class TokenStore : ITokenStore
{
    /// <summary>Enough history to catch a copy, bounded so the file cannot grow without limit.</summary>
    private const int RotatedHistory = 32;

    private readonly AgentPaths _paths;
    private readonly ITokenProtector _protector;
    private readonly IClientLog _log;
    private readonly object _gate = new();
    private TokenFile _file;

    public TokenStore(AgentPaths paths, ITokenProtector? protector = null, IClientLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _protector = protector ?? DpapiTokenProtector.Instance;
        _log = log ?? NullClientLog.Instance;
        _file = Load();
    }

    /// <summary>Raised when a rotated refresh token is presented. The host surfaces it on /devices.</summary>
    public event EventHandler<EventArgs>? ReuseDetected;

    public AgentTokens? Current
    {
        get
        {
            lock (_gate)
            {
                return _file.Tokens;
            }
        }
    }

    public string? DeviceToken
    {
        get
        {
            lock (_gate)
            {
                return _file.DeviceToken;
            }
        }
    }

    public Guid? DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _file.DeviceId;
            }
        }
    }

    public void SaveDeviceRegistration(Guid deviceId, string deviceToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        lock (_gate)
        {
            _file = _file with { DeviceId = deviceId, DeviceToken = deviceToken };
            Persist();
        }

        _log.Info("Device registration stored.");
    }

    public void SaveIssued(AgentTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        lock (_gate)
        {
            _file = _file with { Tokens = tokens };
            Persist();
        }

        _log.Info("Agent credentials stored.");
    }

    public string BeginRotation()
    {
        lock (_gate)
        {
            var tokens = _file.Tokens
                ?? throw new InvalidOperationException("No refresh token: this device is unpaired.");

            if (IsRotated(tokens.RefreshToken))
            {
                RaiseReuse();
            }

            return tokens.RefreshToken;
        }
    }

    public void CompleteRotation(string presented, AgentTokens rotated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presented);
        ArgumentNullException.ThrowIfNull(rotated);

        lock (_gate)
        {
            if (IsRotated(presented))
            {
                RaiseReuse();
            }

            var history = new List<string>(_file.RotatedFingerprints) { Fingerprint(presented) };
            if (history.Count > RotatedHistory)
            {
                history.RemoveRange(0, history.Count - RotatedHistory);
            }

            _file = _file with { Tokens = rotated, RotatedFingerprints = history };
            Persist();
        }

        _log.Info("Refresh token rotated.");
    }

    public bool WasRotated(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        lock (_gate)
        {
            return IsRotated(refreshToken);
        }
    }

    public void Clear(string reason)
    {
        lock (_gate)
        {
            _file = TokenFile.Empty;
            if (File.Exists(_paths.TokensFile))
            {
                File.Delete(_paths.TokensFile);
            }
        }

        _log.Warn($"Token store cleared: {reason}");
    }

    /// <summary>SHA-256, so the file holds no second copy of a spent credential in the clear.</summary>
    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private bool IsRotated(string token) =>
        _file.RotatedFingerprints.Contains(Fingerprint(token), StringComparer.Ordinal);

    private void RaiseReuse()
    {
        _file = TokenFile.Empty;
        if (File.Exists(_paths.TokensFile))
        {
            File.Delete(_paths.TokensFile);
        }

        _log.Error("A rotated refresh token was presented. The chain is revoked and this device must pair again.");
        ReuseDetected?.Invoke(this, EventArgs.Empty);
        throw new TokenReuseDetectedException();
    }

    private TokenFile Load()
    {
        if (!File.Exists(_paths.TokensFile))
        {
            return TokenFile.Empty;
        }

        try
        {
            var plaintext = _protector.Unprotect(File.ReadAllBytes(_paths.TokensFile));
            return JsonSerializer.Deserialize<TokenFile>(plaintext, AgentJson.Options) ?? TokenFile.Empty;
        }
        catch (CryptographicException)
        {
            // Another Windows account, or a restored profile. Unreadable is unpaired.
            _log.Warn("tokens.dat could not be decrypted under this user. Treating the device as unpaired.");
            return TokenFile.Empty;
        }
        catch (JsonException)
        {
            _log.Warn("tokens.dat was unreadable. Treating the device as unpaired.");
            return TokenFile.Empty;
        }
        catch (IOException ex)
        {
            _log.Warn($"tokens.dat could not be read: {ex.Message}");
            return TokenFile.Empty;
        }
    }

    private void Persist()
    {
        _paths.EnsureCreated();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_file, AgentJson.Options);
        var ciphertext = _protector.Protect(plaintext);
        Array.Clear(plaintext);

        var temporary = _paths.TokensFile + ".tmp";
        File.WriteAllBytes(temporary, ciphertext);
        File.Move(temporary, _paths.TokensFile, overwrite: true);
    }

    /// <summary>The on-disk shape. Redacted on print for the same reason as <see cref="AgentTokens"/>.</summary>
    private sealed record TokenFile
    {
        public static TokenFile Empty { get; } = new();

        public Guid? DeviceId { get; init; }

        public string? DeviceToken { get; init; }

        public AgentTokens? Tokens { get; init; }

        /// <summary>SHA-256 of every refresh token this device has rotated away from.</summary>
        public IReadOnlyList<string> RotatedFingerprints { get; init; } = [];

        public override string ToString() => "TokenFile { <redacted> }";
    }
}
