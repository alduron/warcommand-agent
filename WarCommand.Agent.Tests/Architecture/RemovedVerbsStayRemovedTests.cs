using System.Text.RegularExpressions;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// Fails the build if a removed request verb comes back into this repo.
/// </summary>
/// <remarks>
/// A request ends completed, cancelled or expired. The mid-mission progress verb and the
/// spotter-correction verb were both deleted from contracts, api, web and agent. An AetherGraph
/// response=block stops one being TYPED into a source file; nothing stops one arriving through a
/// contract regeneration into Contracts/Bundled, a generated RequestTypes.g.cs, or a merge. This is
/// what catches that, and it runs in dotnet test, which the pre-push hook runs.
/// See Decision_WarCommandARequestOnlyCompletesCancelsOrExpires.
/// The patterns are ASSEMBLED rather than spelled out, so this file does not trip its own scan.
/// </remarks>
public class RemovedVerbsStayRemovedTests
{
    // The banned words are ASSEMBLED where they would otherwise trip the sibling guard in
    // warcommand-api, which scans this repo too and would read the list itself as a violation.
    private static readonly (string Pattern, string Why)[] Forbidden =
    [
        (@"(?i)rounds[ _\-]?away", "the deleted mid-mission progress verb"),
        (@"(?i)adjust[ _\-]?direction", "the deleted spotter-correction position class"),
        (@"\bAdjustRequest\b|\bRequestAdjust\b", "the deleted spotter-correction command"),
        (@"""id""\s*:\s*""adjust""", "the deleted adjust command verb"),
        (@"(?i)quantity[ _\-]?delivered", "the deleted partial-completion count"),
        (@"(?i)no_longer" + "_required", "a deleted completion outcome"),
        (@"(?i)stood" + "_down", "a deleted terminal route, now abandoned"),
        (@"\bRequestState\.(Cancelled|Expired)\b", "a retired request state, now Abandoned"),
        (@"\bRequestEventKind\.(Cancelled|Expired|StoodDown)\b",
            "a retired event kind, now Abandoned"),
        (@"\bOutcome\.(Serviced|Unable|NoLongerRequired)\b",
            "the deleted completion outcome"),
        (@"request\.(cancelled|expired)\b", "a retired realtime frame, now request.abandoned"),
    ];

    private static readonly string[] ScannedExtensions = [".cs", ".json", ".md"];

    private static readonly string[] SkippedSegments = ["obj", "bin", "publish", "TestResults", ".git"];

    [Fact]
    public void No_file_in_this_repo_carries_a_removed_request_verb()
    {
        var root = SolutionRoot();
        var self = Path.GetFileName(GetType().Name) + ".cs";
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (IsSkipped(root, file)) continue;
            if (string.Equals(Path.GetFileName(file), self, StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            foreach (var (pattern, why) in Forbidden)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success)
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}: {why} ('{match.Value}')");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A removed request verb is back in the tree. A request ends completed, cancelled or "
            + "expired, and there is no non-terminal progress verb.\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void The_scan_actually_walks_the_repo()
    {
        // A scan that silently walked nothing passes. This is what makes the count load-bearing.
        var root = SolutionRoot();
        var scanned = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Count(f => !IsSkipped(root, f));

        Assert.True(scanned > 50, $"only {scanned} files scanned");
    }

    private static bool IsSkipped(string root, string file) =>
        Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkippedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WarCommand.Agent.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
}
