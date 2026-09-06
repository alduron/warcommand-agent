using System.Globalization;
using System.IO.Compression;
using System.Text;
using WarCommand.Agent.Client.Storage;

namespace WarCommand.Agent.Client.Diagnostics;

/// <summary>
/// What the settings window's export writes beside the logs: the handful of facts that decide which
/// of several explanations a log line has.
/// </summary>
/// <remarks>
/// A RECORD, not a string the caller assembles. Everything in a bundle is something the user is
/// about to hand to other people, so what may appear is decided here, once, by what this type has
/// fields for. There is deliberately no field for a token, a key binding, a callsign or a
/// coordinate: 12-security.md rule 3 forbids a key code reaching any log at any level, and the rest
/// is somebody's identity or position rather than a fault.
/// </remarks>
public sealed record LogBundleSummary
{
    public required string AgentVersion { get; init; }

    public required string WindowsVersion { get; init; }

    /// <summary>Production or Local. Which API the agent is talking to.</summary>
    public required string Backend { get; init; }

    public required string OverlayMode { get; init; }

    /// <summary>Screen capture is opt-in, and half the readout reports depend on knowing.</summary>
    public required bool ScreenCaptureEnabled { get; init; }

    public required bool GameRunning { get; init; }

    /// <summary>The tray's own connection word. Not an account, not a name.</summary>
    public required string Connection { get; init; }

    /// <summary>Monitor sizes, no device names. Overlay placement is the second-most reported fault.</summary>
    public IReadOnlyList<string> Displays { get; init; } = [];

    /// <summary>Why the last screen read produced nothing, or null. Never a coordinate.</summary>
    public string? LastReadRefusal { get; init; }

    /// <summary>
    /// What the screen resolved to and how far the last decode got. Never a coordinate.
    /// </summary>
    /// <remarks>
    /// Every pixel in the readout profile was measured on one machine, so the first question
    /// about a bad read on another one is what those numbers came out as there. This is that
    /// answer, in the file the customer sends, without asking them to run anything.
    /// </remarks>
    public string? ReadoutGeometry { get; init; }

    internal string Render(DateTimeOffset at) =>
        string.Join(Environment.NewLine, Lines(at)) + Environment.NewLine;

    private IEnumerable<string> Lines(DateTimeOffset at)
    {
        yield return "WarCommand agent log bundle";
        yield return FormattableString.Invariant(
            $"exported     {at.ToString("yyyy-MM-dd HH:mm:ss K", CultureInfo.InvariantCulture)}");
        yield return FormattableString.Invariant($"agent        {AgentVersion}");
        yield return FormattableString.Invariant($"windows      {WindowsVersion}");
        yield return FormattableString.Invariant($"backend      {Backend}");
        yield return FormattableString.Invariant($"connection   {Connection}");
        yield return FormattableString.Invariant($"overlay      {OverlayMode}");
        yield return FormattableString.Invariant(
            $"capture      {(ScreenCaptureEnabled ? "on" : "off")}");
        yield return FormattableString.Invariant($"wardogs      {(GameRunning ? "running" : "not running")}");
        yield return FormattableString.Invariant($"displays     {string.Join(", ", Displays)}");

        if (LastReadRefusal is { Length: > 0 } refusal)
        {
            yield return FormattableString.Invariant($"last refusal {refusal}");
        }

        if (ReadoutGeometry is { Length: > 0 } geometry)
        {
            yield return FormattableString.Invariant($"readout      {geometry}");
        }

        yield return string.Empty;
        yield return "No token, key binding, callsign, coordinate, audio or screen frame is in this file";
        yield return "or in any log beside it. The logs name windows, displays and faults.";
    }
}

/// <summary>
/// Zips the log directory plus a <see cref="LogBundleSummary"/> into one file the user can attach.
/// </summary>
/// <remarks>
/// The product is going to be debugged by a crowd rather than by whoever wrote it, so the path from
/// "it did something wrong" to a file somebody else can read has to be one button. Collecting by
/// hand means asking people to find %LOCALAPPDATA%, which in practice means getting nothing.
/// </remarks>
public static class LogBundle
{
    /// <summary>
    /// Writes <c>warcommand-logs-<em>stamp</em>.zip</c> into <paramref name="destinationDirectory"/>
    /// and returns its full path. Overwrites nothing: the stamp carries seconds.
    /// </summary>
    public static string Write(
        AgentPaths paths,
        string destinationDirectory,
        LogBundleSummary summary,
        DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var stamp = (at ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(
            destinationDirectory,
            FormattableString.Invariant($"warcommand-logs-{stamp}.zip"));

        Directory.CreateDirectory(destinationDirectory);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var summaryEntry = zip.CreateEntry("summary.txt");
        using (var writer = new StreamWriter(summaryEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write(summary.Render(at ?? DateTimeOffset.Now));
        }

        if (Directory.Exists(paths.LogDirectory))
        {
            foreach (var file in Directory.GetFiles(paths.LogDirectory, RollingFileLog.Prefix + "*.log"))
            {
                // Copied through a share-reading stream rather than CreateEntryFromFile: the live
                // file is open for append in this same process, and the convenience method opens it
                // exclusively and throws.
                var entry = zip.CreateEntry(Path.GetFileName(file));
                using var source = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }

        return zipPath;
    }
}
