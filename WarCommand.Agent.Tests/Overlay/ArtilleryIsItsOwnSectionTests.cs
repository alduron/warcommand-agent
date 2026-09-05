using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;
using Xunit;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The artillery readout is a section of the board, and it survives the key coming up.
/// </summary>
/// <remarks>
/// It was one string squeezed into the menu's typed-text field. That clipped it, so the elevation
/// ran off the panel, and it existed only while the menu was open at the artillery level, which is
/// not when a gun crew reads numbers: they read them while dialling, with the key released.
/// </remarks>
public sealed class ArtilleryIsItsOwnSectionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);

    /// <summary>Walks the tool the way a person does: gun, then target.</summary>
    private static MenuStateMachine WithGunAndTarget(bool target)
    {
        var menu = Machine();
        menu.OpenTools(T0, new MenuContext());

        while (menu.Options[menu.Highlight].Path != "board.more.range")
        {
            menu.Scroll(-1, T0);
        }

        menu.Select(T0);
        Assert.Equal(MenuLevel.RangeTool, menu.Level);

        menu.Select(T0);
        menu.AcceptReadCoordinate(new MapPoint(84.10m, 70.88m, "map_readout", null, null), T0);

        if (!target)
        {
            return menu;
        }

        while (menu.Options[menu.Highlight].Path != "range.target")
        {
            menu.Scroll(1, T0);
        }

        menu.Select(T0);
        menu.AcceptReadCoordinate(new MapPoint(85.53m, 69.42m, "map_readout", null, null), T0);
        return menu;
    }

    [Fact]
    public void With_no_gun_there_is_no_section_at_all()
    {
        Assert.Null(MenuViewModel.ArtilleryFor(Machine()));
    }

    [Fact]
    public void A_gun_alone_shows_where_it_is_and_says_what_is_missing()
    {
        var readout = MenuViewModel.ArtilleryFor(WithGunAndTarget(target: false));

        Assert.NotNull(readout);
        Assert.Contains("84.10", readout.Gun, StringComparison.Ordinal);
        Assert.Contains("NOT SET", readout.Target, StringComparison.Ordinal);
        Assert.Equal(string.Empty, readout.Bracket);
        Assert.NotEqual(string.Empty, readout.Note);
    }

    [Fact]
    public void Both_ends_set_puts_the_numbers_on_their_own_line()
    {
        var readout = MenuViewModel.ArtilleryFor(WithGunAndTarget(target: true));

        Assert.NotNull(readout);
        Assert.Contains("85.53", readout.Target, StringComparison.Ordinal);

        // The numbers the crew dials, alone. Running them together with the caveats is what pushed
        // the elevation off the edge of the panel.
        Assert.Contains("AZ", readout.Bracket, StringComparison.Ordinal);
        Assert.DoesNotContain("ADJUST", readout.Bracket, StringComparison.Ordinal);

        // And the caveats, which never leave: the tables are player-measured and there is no
        // altitude anywhere in the system, so every readout carries the spotter hint.
        Assert.Contains("ADJUST", readout.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_never_called_a_firing_solution()
    {
        var readout = MenuViewModel.ArtilleryFor(WithGunAndTarget(target: true));

        Assert.NotNull(readout);
        var whole = $"{readout.Gun} {readout.Target} {readout.Bracket} {readout.Note}";
        Assert.DoesNotContain("FIRING SOLUTION", whole, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_section_outlives_the_menu()
    {
        var menu = WithGunAndTarget(target: true);

        // The key comes up. Everything the menu was holding is discarded.
        menu.KeyUp(T0.AddSeconds(1));
        Assert.False(menu.IsOpen);

        // The gun and the target are not the menu's draft; they are where the crew is standing and
        // what they are shooting at, and they stay until somebody moves them.
        var readout = MenuViewModel.ArtilleryFor(menu);
        Assert.NotNull(readout);
        Assert.Contains("AZ", readout.Bracket, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_takes_the_whole_section_off_the_board()
    {
        var menu = WithGunAndTarget(target: true);
        Assert.NotNull(MenuViewModel.ArtilleryFor(menu));

        // Back out to the artillery page, where CLEAR appears only once there is something to
        // clear. There was no way to unset a gun at all: reading one put ARTILLERY on the board
        // permanently, and every bracket it drew was there for the rest of the session.
        while (menu.Level != MenuLevel.RangeTool)
        {
            menu.Back(T0);
        }

        var clear = menu.Options.Single(o => o.Path == "range.clear");
        menu.Digit(clear.Digit, T0);

        // Both ends together: a gun position with no target goes stale where it stands, and a
        // bracket computed from where a gun used to be is worse than no bracket at all.
        Assert.Null(MenuViewModel.ArtilleryFor(menu));
        Assert.Null(menu.ToolGun);
        Assert.Null(menu.ToolTarget);

        // And the entry goes with it, rather than sitting there clearing nothing.
        Assert.DoesNotContain(menu.Options, o => o.Path == "range.clear");
    }

    [Fact]
    public void Every_digit_the_artillery_page_draws_actually_does_something()
    {
        var menu = WithGunAndTarget(target: false);
        while (menu.Level != MenuLevel.RangeTool)
        {
            menu.Back(T0);
        }

        // The page drew 1, 2 and 3 and dispatched none of them: RangeTool was missing from the
        // digit switch entirely, so the only way to work the tool was to navigate onto each line.
        foreach (var entry in menu.Options)
        {
            var fresh = WithGunAndTarget(target: false);
            while (fresh.Level != MenuLevel.RangeTool)
            {
                fresh.Back(T0);
            }

            Assert.IsNotType<MenuNothing>(fresh.Digit(entry.Digit, T0));
        }
    }

    [Fact]
    public void The_presenter_carries_it_to_a_surface_that_joins_late()
    {
        var presenter = new BoardPresenter();
        presenter.SetArtillery(MenuViewModel.ArtilleryFor(WithGunAndTarget(target: true)));

        Assert.NotNull(presenter.Artillery);
        Assert.Contains("AZ", presenter.Artillery.Bracket, StringComparison.Ordinal);

        presenter.SetArtillery(null);
        Assert.Null(presenter.Artillery);
    }
}
