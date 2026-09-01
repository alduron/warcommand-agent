namespace WarCommand.Agent.Client.Http;

/// <summary>
/// Refuses a plaintext endpoint. The agent pins to https and wss and there is no config override
/// that relaxes it.
/// </summary>
public static class TransportSecurity
{
    /// <summary>Returns <paramref name="uri"/> when its scheme is https, and throws otherwise.</summary>
    public static Uri RequireHttps(Uri uri, string paramName)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"WarCommand refuses plaintext: '{uri}' is not https, and no config override changes that.",
                paramName);
        }

        return uri;
    }

    /// <summary>Returns <paramref name="uri"/> when its scheme is wss, and throws otherwise.</summary>
    public static Uri RequireSecureWebSocket(Uri uri, string paramName)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, "wss", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"WarCommand refuses plaintext: '{uri}' is not wss, and no config override changes that.",
                paramName);
        }

        return uri;
    }
}

/// <summary>Everything the HTTP client needs that is not a credential.</summary>
public sealed class ApiClientOptions
{
    private Uri _baseAddress = new("https://api.warcommand.app");

    /// <summary>https only. Setting a plaintext address throws rather than downgrading silently.</summary>
    public Uri BaseAddress
    {
        get => _baseAddress;
        init => _baseAddress = TransportSecurity.RequireHttps(value, nameof(BaseAddress));
    }

    /// <summary>Reported on every request, and to /v1/devices/register.</summary>
    public string AgentVersion { get; init; } = "0.0.0";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Pages the whole-board seed will follow before giving up.</summary>
    public int MaxBoardPages { get; init; } = 50;
}
