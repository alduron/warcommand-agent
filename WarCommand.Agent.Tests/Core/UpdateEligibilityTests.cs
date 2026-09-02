using System.Reflection;
using WarCommand.Agent;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Who may be updated. A build running from a source tree must never be replaced by a published
/// release: it reports the Directory.Build.props default of 0.0.0, which is below every release on
/// purpose, so the tray offered the last one and a single click swapped a newer working build for
/// an older published one and relaunched into it.
/// </summary>
public class UpdateEligibilityTests
{
    private static bool IsInstalledBuild() => (bool)typeof(App)
        .GetMethod("IsInstalledBuild", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, null)!;

    /// <summary>
    /// The suite runs out of bin/, which is exactly the case that must be refused. If this ever
    /// starts returning true here, the check has stopped distinguishing the two.
    /// </summary>
    [Fact]
    public void A_build_running_from_the_source_tree_is_not_an_installed_build()
    {
        Assert.False(IsInstalledBuild());
    }

    [Fact]
    public void The_check_reads_the_running_location_not_the_version()
    {
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "WarCommand");

        Assert.DoesNotContain(installRoot, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
