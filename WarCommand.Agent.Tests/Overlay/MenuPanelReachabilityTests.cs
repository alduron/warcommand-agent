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
    public void Down_from_rest_shows_the_board_and_not_the_request_list()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [1, 2] });

        // The request categories are drawn only while the highlight is in them. Drawing them always
        // made DOWN look exactly like UP: the row was highlighted underneath, but the panel above
        // was full of request categories and the surface read as the request menu either way.
        Assert.Empty(MenuViewModel.From(menu).Options);
        Assert.Equal(1, menu.HighlightedSlot);
    }

    [Fact]
    public void Down_from_rest_on_an_empty_board_lands_on_more_not_a_request()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext());

        Assert.False(menu.HighlightIsARequest);
        Assert.Empty(MenuViewModel.From(menu).Options);
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
    public void More_is_drawn_below_the_rows_never_above_them()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1] });

        var view = MenuViewModel.From(menu);

        // Above the board are requests only. MORE sits under the rows because that is where it is
        // in the list, and drawing it above made it read as a request category.
        Assert.DoesNotContain(view.Options, o => o.Label == "MORE");
        Assert.Contains(view.Trailing, o => o.Label == "MORE");
    }

    [Fact]
    public void An_empty_board_still_offers_more()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext());

        Assert.Contains(MenuViewModel.From(menu).Trailing, o => o.Label == "MORE");
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
