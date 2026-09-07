using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A gun crew that accepts a shell mission has already been told where to shoot.
/// </summary>
/// <remarks>
/// The grid is on the row they pressed ACCEPT on. Making them walk into the tool and type it again
/// is asking for the number twice and getting it wrong once.
/// </remarks>
public class FireTargetFollowsTheMissionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);

    private static MapPoint At(decimal x, decimal y) => new(x, y, "map_readout", null, null);

    [Fact]
    public void The_target_is_taken_from_the_mission()
    {
        var menu = Machine();

        Assert.True(menu.AdoptFireTarget(At(97.56m, 108.62m)));
        Assert.Equal(97.56m, menu.ToolTarget!.X);
        Assert.Equal(108.62m, menu.ToolTarget!.Y);
    }

    /// <summary>The crew's own origin is theirs. Only the target follows the board.</summary>
    [Fact]
    public void The_origin_is_never_touched()
    {
        var menu = Machine();
        menu.AdoptFireTarget(At(97.56m, 108.62m));

        Assert.Null(menu.ToolGun);
    }

    /// <summary>A correction moves the grid, and the tool follows it without being reopened.</summary>
    [Fact]
    public void A_moved_grid_moves_the_target()
    {
        var menu = Machine();
        menu.AdoptFireTarget(At(97.56m, 108.62m));

        Assert.True(menu.AdoptFireTarget(At(98.10m, 108.90m)));
        Assert.Equal(98.10m, menu.ToolTarget!.X);
    }

    /// <summary>An idempotent frame must not repaint the overlay.</summary>
    [Fact]
    public void The_same_grid_twice_is_not_a_change()
    {
        var menu = Machine();
        menu.AdoptFireTarget(At(97.56m, 108.62m));

        Assert.False(menu.AdoptFireTarget(At(97.56m, 108.62m)));
    }

    /// <summary>Only a type the catalog says computes one. A delivery never moves a gun.</summary>
    [Fact]
    public void Only_a_fire_mission_computes_a_solution()
    {
        Assert.NotNull(ContractFixtures.Catalog.RequestType("attack_position")!.ComputesSolution);
        Assert.Null(ContractFixtures.Catalog.RequestType("resupply")!.ComputesSolution);
        Assert.Null(ContractFixtures.Catalog.RequestType("weapon_rifle")!.ComputesSolution);
    }
}
