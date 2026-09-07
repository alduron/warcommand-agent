using System.IO;
using System.Text.RegularExpressions;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// The agent must READ the served contracts, not just be able to.
/// </summary>
/// <remarks>
/// The client could fetch and the store could adopt, and nothing in the shipping app called
/// either, so the agent ran on its bundle and every catalog edit cost a signed release. Binding
/// rule 5 says the opposite. This holds the call sites shut.
/// </remarks>
public class ServedContractsAreFetchedTests
{
    private static string App { get; } = File.ReadAllText(SourcePath("WarCommand.Agent/App.xaml.cs"));

    [Theory]
    [InlineData("GetRequestTypesAsync")]
    [InlineData("GetGameProfileAsync")]
    [InlineData("GetBallisticsAsync")]
    public void The_app_fetches_every_served_contract(string call)
    {
        Assert.Contains(call, App, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_adopts_what_it_fetched()
    {
        Assert.Contains("TryAdopt", App, StringComparison.Ordinal);
    }

    [Fact]
    public void An_adopted_catalog_rebuilds_the_menu_tree()
    {
        // The grammar recompiles per hold from the store, so the tree is the only thing an
        // adoption leaves stale. A fetch that does not rebuild it is a fetch that changes nothing.
        Assert.Contains("Retarget(", App, StringComparison.Ordinal);
        Assert.Contains("MenuTree.Compile", App, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fetch_is_conditional_on_a_stored_etag()
    {
        Assert.Matches(new Regex(@"_catalogEtag", RegexOptions.None), App);
    }

    [Fact]
    public void The_refresh_runs_again_on_the_config_poll_and_not_only_at_arm_time()
    {
        // Twice: once from ArmForAccount, once from the poll tick. A catalog edit must land on a
        // running agent without a restart.
        var calls = Regex.Matches(App, @"RefreshServedContractsAsync\(").Count;
        Assert.True(calls >= 3, $"expected a definition and two call sites, found {calls}");
    }

    private static string SourcePath(string relative)
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
