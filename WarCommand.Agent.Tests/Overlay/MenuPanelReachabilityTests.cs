using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;
using Xunit;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The home list is drawn in three pieces: requests above the board, the rows themselves, and MORE
/// below. The pieces have to add up to the list, or the highlight lands on something nobody can
/// see and moving reads as a dropped key.
/// </summary>
public sealed class MenuPanelReachabilityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void The_three_drawn_pieces_add_up_to_the_whole_home_list()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1, 2, 3] });

        var view = MenuViewModel.From(menu);
        var rows = menu.Options.Count(o => o.Path.StartsWith("board.", StringComparison.Ordinal));

        var detail = string.Join(", ", menu.Options.Select(o => o.Path));
        Assert.True(
            menu.Options.Count == view.Options.Count + rows + view.Trailing.Count,
            $"options {menu.Options.Count} leading {view.Options.Count} rows {rows} trailing {view.Trailing.Count} :: {detail}");
    }

    [Fact]
    public void Down_from_rest_highlights_a_row_and_leaves_the_menu_above_it_on_screen()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [1, 2] });

        // The highlight is on the row, which is what DOWN means, and the menu above stays drawn.
        // It used to vanish the moment the highlight moved onto a row, so walking down made the
        // block above you disappear and walking back up made it reappear with the highlight
        // already inside it. There was no way to see what was above before going there.
        Assert.Equal(1, menu.HighlightedSlot);

        var view = MenuViewModel.From(menu);
        Assert.NotEmpty(view.Options);
        Assert.DoesNotContain(view.Options, o => o.IsHighlighted);
    }

    [Fact]
    public void Down_from_rest_on_an_empty_board_lands_on_a_request_not_on_tools()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext());

        // With no rows there is nothing below to go down to. It used to fall back to TOOLS, which
        // is at the far end of the list, so one press of DOWN landed on the settings page and the
        // next press of UP appeared to teleport into the request tree.
        Assert.True(menu.HighlightIsARequest);
        Assert.NotEqual("home.more", menu.Options[menu.Highlight].Path);
    }

    [Fact]
    public void Up_from_rest_shows_the_request_list()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1, 2] });

        Assert.True(menu.HighlightIsARequest);
        Assert.NotEmpty(MenuViewModel.From(menu).Options);
    }

    [Fact]
    public void Tools_is_drawn_above_the_rows_with_everything_else_that_is_not_a_row()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1] });

        var view = MenuViewModel.From(menu);

        // Nothing trails the board. TOOLS and ARTILLERY used to draw below it while the request
        // categories drew above, so walking UP from the bottom crossed the whole board to reach
        // the requests and read as a teleport.
        Assert.Contains(view.Options, o => o.Label == "TOOLS");
        Assert.Contains(view.Options, o => o.Label == "ARTILLERY");
        Assert.Empty(view.Trailing);
    }

    [Fact]
    public void An_empty_board_still_offers_tools()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext());

        Assert.Contains(MenuViewModel.From(menu).Options, o => o.Label == "TOOLS");
    }

    [Fact]
    public void More_reaches_the_panels_behind_it()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1] });

        while (menu.Options[menu.Highlight].Path != "home.more")
        {
            menu.Scroll(1, T0);
        }

        menu.Select(T0);

        // Roles, the join code, the match, people, help, gun position, restart and account linking
        // all live behind this one entry.
        Assert.Equal(MenuLevel.More, menu.Level);
        Assert.NotEmpty(MenuViewModel.From(menu).Options);
    }

    [Fact]
    public void A_level_below_home_draws_its_whole_list_in_one_piece()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1] });
        menu.Select(T0);

        var view = MenuViewModel.From(menu);

        // Only the home list is split, because only it has board rows running through the middle.
        Assert.Equal(menu.Options.Count, view.Options.Count);
        Assert.Empty(view.Trailing);
    }

    private static MenuStateMachine Machine() =>
        new(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);
}
