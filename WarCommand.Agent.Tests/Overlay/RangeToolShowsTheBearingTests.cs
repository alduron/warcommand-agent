using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;
using Xunit;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The range tool answers which way and how far, on the page the crew is looking at.
/// </summary>
/// <remarks>
/// The bearing came off this line when the readout became a section of the board. The board
/// section is not what somebody laying a gun is looking at: they are on the tool page, holding the
/// key, reading the two numbers they are about to dial. A range with no bearing answers half the
/// question the page exists for.
/// </remarks>
public class RangeToolShowsTheBearingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);

    private static MapPoint At(decimal x, decimal y) => new(x, y, "map_readout", null, null);

    /// <summary>Walks the tool the way a person does, then returns the line it draws.</summary>
    private static string Typed(MapPoint gun, MapPoint target)
    {
        var menu = OnTheRangePage();

        menu.Select(T0);
        menu.AcceptReadCoordinate(gun, T0);
        menu.AdoptFireTarget(target);

        return MenuViewModel.From(menu).Typed ?? string.Empty;
    }

    private static MenuStateMachine OnTheRangePage()
    {
        var menu = Machine();
        menu.OpenTools(T0, new MenuContext());

        while (menu.Options[menu.Highlight].Path != "board.more.range")
        {
            menu.Scroll(-1, T0);
        }

        menu.Select(T0);
        Assert.Equal(MenuLevel.RangeTool, menu.Level);
        return menu;
    }

    /// <summary>Due north is 0, and the bearing leads because it is what a gun is laid on.</summary>
    [Fact]
    public void The_bearing_is_on_the_line_with_the_range()
    {
        var line = Typed(At(50m, 50m), At(50m, 60m));

        Assert.StartsWith("AZ 0", line, StringComparison.Ordinal);
        Assert.Contains("M", line, StringComparison.Ordinal);
    }

    /// <summary>Clockwise from north, which is what the gun's own dial reads.</summary>
    [Theory]
    [InlineData(50, 60, "AZ 0")]
    [InlineData(60, 50, "AZ 90")]
    [InlineData(50, 40, "AZ 180")]
    [InlineData(40, 50, "AZ 270")]
    public void The_bearing_is_degrees_clockwise_from_north(int x, int y, string expected)
    {
        Assert.StartsWith(expected, Typed(At(50m, 50m), At(x, y)), StringComparison.Ordinal);
    }

    /// <summary>A shot the weapon cannot reach still says which way it is.</summary>
    [Fact]
    public void An_unreachable_target_keeps_its_bearing()
    {
        var line = Typed(At(1m, 1m), At(399m, 399m));

        Assert.StartsWith("AZ 45", line, StringComparison.Ordinal);
    }

    /// <summary>Neither end set is a prompt, not a bearing off two nulls.</summary>
    [Fact]
    public void An_unset_tool_asks_for_its_ends()
    {
        Assert.Equal("SET THE ORIGIN", MenuViewModel.From(OnTheRangePage()).Typed);
    }
}
