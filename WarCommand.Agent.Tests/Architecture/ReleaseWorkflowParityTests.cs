using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WarCommand.Agent.Tests.Core;
using YamlDotNet.Serialization;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// The local gate runs inside the umbrella clone, where every sibling file is simply there.
/// release.yml checks this repo out ALONE, so each of those files has to be fetched, placed and
/// confirmed by hand. That difference is why v0.4.4 failed on three tests after a fully green
/// local push: contracts/row-fields.json was read by the suite and named nowhere in the workflow.
///
/// This runs in the ordinary suite, so the pre-push hook executes it: a dependency added to
/// UmbrellaDependencies without wiring release.yml is refused here, before the tag is cut.
/// </summary>
public class ReleaseWorkflowParityTests
{
    private const string WorkflowPath = ".github/workflows/release.yml";

    private static readonly IList Steps = LoadSteps();

    /// <summary>Workspace paths, because xunit theory data has to be a simple serialisable value.</summary>
    public static TheoryData<string> RequiredDependencies
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var dependency in UmbrellaDependencies.All.Where(d => d.Required))
            {
                data.Add(dependency.WorkspacePath);
            }

            return data;
        }
    }

    [Fact]
    public void Something_outside_this_repo_is_actually_declared()
    {
        // A theory over an empty list passes. This is what keeps the three below load-bearing.
        Assert.NotEmpty(RequiredDependencies);
    }

    [Theory]
    [MemberData(nameof(RequiredDependencies))]
    public void Every_required_dependency_is_fetched_by_a_sparse_checkout(string workspacePath)
    {
        var dependency = UmbrellaDependencies.ByWorkspacePath(workspacePath);
        var entries = SparseEntries(CheckoutOf(dependency));

        Assert.True(
            entries.Any(entry => Covers(entry, dependency.RepositoryPath)),
            $"{WorkflowPath} checks out {dependency.Repository} with sparse-checkout "
            + $"[{string.Join(", ", entries)}], which does not cover {dependency.RepositoryPath}. "
            + "The suite reads it, so the release cannot build without it.");
    }

    [Theory]
    [MemberData(nameof(RequiredDependencies))]
    public void Every_required_dependency_is_placed_at_the_workspace_root(string workspacePath)
    {
        var dependency = UmbrellaDependencies.ByWorkspacePath(workspacePath);
        var checkout = CheckoutOf(dependency);
        var checkoutPath = Text(With(checkout, "path"))
            ?? throw new InvalidOperationException($"the {dependency.Repository} checkout names no path");

        // Checked out straight into place, so nothing has to copy it.
        if (string.Equals(Join(checkoutPath, dependency.RepositoryPath), dependency.WorkspacePath, StringComparison.Ordinal))
        {
            return;
        }

        // Otherwise a run step has to copy it out of the checkout directory and onto the workspace
        // root, where ContractFixtures walks up to find it. Only the sparse entries count as copy
        // sources: allowing any ancestor would let "cp -r _umbrella/contracts" satisfy a file that
        // was never fetched.
        var sources = SparseEntries(checkout)
            .Where(entry => Covers(entry, dependency.RepositoryPath))
            .Append(dependency.RepositoryPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var placed = sources.Any(source =>
            Scripts().Any(script =>
                script.Contains(Join(checkoutPath, source), StringComparison.Ordinal)
                && script.Contains(WorkspacePrefix(dependency, source), StringComparison.Ordinal)));

        Assert.True(
            placed,
            $"nothing in {WorkflowPath} copies {dependency.RepositoryPath} out of {checkoutPath}/ and "
            + $"onto {dependency.WorkspacePath}. ContractFixtures walks UP from the test binary, so a "
            + "file left under the checkout directory is invisible to it.");
    }

    [Theory]
    [MemberData(nameof(RequiredDependencies))]
    public void Every_required_dependency_is_confirmed_before_the_build(string workspacePath)
    {
        var dependency = UmbrellaDependencies.ByWorkspacePath(workspacePath);

        // Paths in the confirm step are relative to the job's working-directory, warcommand-agent.
        var probe = $"! -f ../{dependency.WorkspacePath} ]";

        var index = IndexOfStep(script =>
            Collapse(script).Contains(probe, StringComparison.Ordinal)
            && script.Contains("exit 1", StringComparison.Ordinal));

        Assert.True(
            index >= 0,
            $"no step in {WorkflowPath} fails the run when ../{dependency.WorkspacePath} is missing. "
            + "Without that check a missing deploy key surfaces as three unexplained test failures.");

        var test = IndexOfStep(script => script.Contains("dotnet test", StringComparison.Ordinal));
        Assert.True(test >= 0, $"{WorkflowPath} runs no tests");
        Assert.True(index < test, $"the check for {dependency.WorkspacePath} runs after the tests it exists to explain");
    }

    [Fact]
    public void The_agent_is_checked_out_beside_its_siblings_and_not_at_the_workspace_root()
    {
        // Everything above depends on this layout: ContractFixtures finds a sibling by walking up
        // out of warcommand-agent/. Checked out at the root, none of the paths resolve.
        var self = Checkouts().Single(step => Text(With(step, "repository")) is null);
        Assert.Equal("warcommand-agent", Text(With(self, "path")));
    }

    [Fact]
    public void The_workflow_signs_the_executable_the_build_actually_produces()
    {
        // Same class of defect, on the other side of the build: a name the workflow knows and the
        // build does not. sign.ps1 returns before its Test-Path with no certificate configured, so
        // today a wrong name here is silent and goes red on the first signed release.
        var assembly = Group(
            File.ReadAllText(RepoFile("WarCommand.Agent/WarCommand.Agent.csproj")),
            @"<AssemblyName>([^<]+)</AssemblyName>",
            "WarCommand.Agent.csproj names no AssemblyName");

        var signed = Scripts()
            .Select(script => Regex.Match(script, @"sign\.ps1\s+-Path\s+publish/(\S+)"))
            .FirstOrDefault(match => match.Success)
            ?? throw new InvalidOperationException($"{WorkflowPath} signs nothing out of publish/");

        Assert.Equal($"{assembly}.exe", signed.Groups[1].Value);

        // And the installer packages that same name, or the shortcut it writes points at nothing.
        var appExe = Group(
            File.ReadAllText(RepoFile("installer/warcommand.iss")),
            @"#define\s+AppExe\s+""([^""]+)""",
            "warcommand.iss defines no AppExe");

        Assert.Equal($"{assembly}.exe", appExe);
    }

    private static string Group(string content, string pattern, string absent)
    {
        var match = Regex.Match(content, pattern);
        Assert.True(match.Success, absent);
        return match.Groups[1].Value.Trim();
    }

    /// <summary>The checkout step that fetches this dependency's repository.</summary>
    private static IDictionary CheckoutOf(UmbrellaDependency dependency)
    {
        var step = Checkouts().FirstOrDefault(s =>
            string.Equals(Text(With(s, "repository")), dependency.Repository, StringComparison.Ordinal));

        Assert.True(
            step is not null,
            $"{WorkflowPath} never checks out {dependency.Repository}, which holds "
            + $"{dependency.RepositoryPath}. The suite reads it, so the release would fail on it.");

        return step!;
    }

    private static IEnumerable<IDictionary> Checkouts() =>
        Steps.Cast<object>()
            .Select(Map)
            .Where(step => step is not null
                && Text(step!["uses"])?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true)
            .Select(step => step!);

    private static List<string> SparseEntries(IDictionary checkout) =>
        (Text(With(checkout, "sparse-checkout")) ?? string.Empty)
        .Split('\n')
        .Select(line => line.Trim().TrimEnd('/'))
        .Where(line => line.Length > 0)
        .ToList();

    /// <summary>True when the sparse entry is the path itself or a directory holding it.</summary>
    private static bool Covers(string entry, string path) =>
        string.Equals(entry, path, StringComparison.Ordinal)
        || path.StartsWith(entry + "/", StringComparison.Ordinal);

    /// <summary>The workspace path with as many trailing segments dropped as <paramref name="source"/> drops.</summary>
    private static string WorkspacePrefix(UmbrellaDependency dependency, string source)
    {
        var dropped = dependency.RepositoryPath.Count(c => c == '/') - source.Count(c => c == '/');
        var prefix = dependency.WorkspacePath;
        for (var i = 0; i < dropped; i++)
        {
            prefix = prefix[..prefix.LastIndexOf('/')];
        }

        return prefix;
    }

    private static string Join(string left, string right) =>
        left is "." or "" ? right : $"{left.TrimEnd('/')}/{right}";

    private static IEnumerable<string> Scripts() =>
        Steps.Cast<object>().Select(step => Text(Map(step)?["run"])).Where(run => run is not null).Select(run => run!);

    private static int IndexOfStep(Func<string, bool> matches)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Text(Map(Steps[i])?["run"]) is { } run && matches(run))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Runs of whitespace collapsed, so a reflowed shell condition still matches.</summary>
    private static string Collapse(string script) => Regex.Replace(script, @"\s+", " ");

    private static object? With(IDictionary step, string key) => Map(step["with"])?[key];

    private static IDictionary? Map(object? node) => node as IDictionary;

    private static string? Text(object? node) => node as string;

    private static IList LoadSteps()
    {
        var yaml = new DeserializerBuilder().Build()
            .Deserialize<object>(File.ReadAllText(RepoFile(WorkflowPath)));

        var jobs = Map(Map(yaml)?["jobs"]);
        var release = Map(jobs?["release"]);
        return Map(release)?["steps"] as IList
               ?? throw new InvalidOperationException($"{WorkflowPath} has no jobs.release.steps");
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WarCommand.Agent.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
