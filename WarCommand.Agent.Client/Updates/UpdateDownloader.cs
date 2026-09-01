using System.Diagnostics;
using System.Security.Cryptography;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Core.Updates;

namespace WarCommand.Agent.Client.Updates;

/// <summary>The installer was fetched but its bytes are not the ones the manifest named.</summary>
public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message) : base(message)
    {
    }

    public UpdateVerificationException()
        : base("The downloaded installer did not match its published digest.")
    {
    }

    public UpdateVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Fetches a published installer, verifies it against the manifest digest, and hands back a path.
/// Running it is a separate call, so nothing is ever executed by the act of downloading it.
/// </summary>
/// <remarks>
/// The digest check is the whole point of this class. The download goes to a temp name and is only
/// renamed into place after it verifies, so a truncated or substituted file can never be left
/// somewhere that looks installable. A file that fails is deleted, not kept for diagnosis: it is
/// an executable of unknown provenance.
/// </remarks>
public sealed class UpdateDownloader
{
    private readonly HttpClient _http;
    private readonly AgentPaths _paths;
    private readonly IClientLog _log;

    public UpdateDownloader(HttpClient http, AgentPaths paths, IClientLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        _http = http;
        _paths = paths;
        _log = log ?? NullClientLog.Instance;
    }

    /// <summary>Where verified installers land. Beside the tokens, never in the install directory.</summary>
    public string Directory => Path.Combine(_paths.Root, "updates");

    /// <summary>
    /// Downloads and verifies. Returns the path to the verified installer.
    /// Throws <see cref="UpdateVerificationException"/> when the digest does not match.
    /// </summary>
    public async Task<string> FetchAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        // Re-checked here as well as in UpdateDecision: this method is what opens the socket, and
        // it must not depend on a caller having validated first.
        if (!UpdateDecision.IsInstallableUrl(offer.Url) || !UpdateDecision.IsSha256(offer.Sha256))
        {
            throw new UpdateVerificationException("The offer is not installable.");
        }

        System.IO.Directory.CreateDirectory(Directory);
        var final = Path.Combine(Directory, $"WarCommand-Setup-{offer.Version}.exe");
        var staging = final + ".partial";

        if (File.Exists(final) && await MatchesAsync(final, offer.Sha256, cancellationToken).ConfigureAwait(false))
        {
            _log.Info($"Update {offer.Version} is already downloaded and verified.");
            return final;
        }

        _log.Info($"Downloading update {offer.Version}.");
        try
        {
            using (var response = await _http
                .GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                _ = response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = File.Create(staging);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (!await MatchesAsync(staging, offer.Sha256, cancellationToken).ConfigureAwait(false))
            {
                throw new UpdateVerificationException(
                    $"Update {offer.Version} did not match its published digest and was discarded.");
            }

            File.Move(staging, final, overwrite: true);
            _log.Info($"Update {offer.Version} verified.");
            return final;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Runs a verified installer and returns true when it started. The caller shuts the agent down
    /// straight after: the installer replaces the running exe, so it cannot finish while we hold it.
    /// </summary>
    public bool Launch(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        try
        {
            // /UPDATE is ours, not Inno's: it is what the installer's LaunchAfterSilentUpdate
            // check looks for, because a silent install skips the normal post-install launch and
            // would otherwise replace the agent and leave nothing running. Nothing under
            // %LOCALAPPDATA%\WarCommand is touched, so install.id and the pairing survive.
            var start = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART /UPDATE",
            };
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log.Warn($"Could not start the installer: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Removes every installer except the one named. Called after a successful update.</summary>
    public void Prune(string? keepPath = null)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            if (keepPath is not null && string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDelete(file);
        }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover file in the updates directory is harmless; it is verified before use.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
