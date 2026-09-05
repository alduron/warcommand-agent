using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;
using Xunit;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// Three surfaces, one key each: REQUEST up, BOARD down, TOOLS on its own key. Each draws its whole
/// list, or the highlight lands on something nobody can see and moving reads as a dropped key.
/// </summary>
public sealed class MenuPanelReachabilityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void The_request_surface_draws_every_entry_it_holds()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1, 2, 3] });

        var view = MenuViewModel.From(menu);

        var detail = string.Join(", ", menu.Options.Select(o => o.Path));
        Assert.True(
            menu.Options.Count == view.Options.Count + view.Trailing.Count,
            $"options {menu.Options.Count} leading {view.Options.Count} trailing {view.Trailing.Count} :: {detail}");
        Assert.DoesNotContain(menu.Options, o => o.Path.StartsWith("board.", StringComparison.Ordinal));
    }

    [Fact]
    public void Down_from_rest_highlights_a_row_and_draws_no_request_list()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [1, 2] });

        // The highlight is on the row, which is what DOWN means, and the panel draws nothing else:
        // the real rows carry the highlight, and the request list is a surface you are not on.
        Assert.Equal(1, menu.HighlightedSlot);

        var view = MenuViewModel.From(menu);
        Assert.Equal("BOARD", view.Title);
        Assert.Empty(view.Options);
        Assert.Empty(view.Trailing);
    }

    [Fact]
    public void Down_from_rest_on_an_empty_board_is_an_empty_board_and_nothing_else()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext());

        // An empty board is empty. It used to fall back into the request list, so DOWN on a quiet
        // board opened the thing UP opens and the two directions stopped meaning anything.
        Assert.Equal(MenuLevel.Board, menu.Level);
        Assert.Null(menu.HighlightedSlot);
        Assert.Empty(menu.Options);
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
    public void The_request_surface_carries_no_tools_and_no_rows()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [1] });

        var view = MenuViewModel.From(menu);

        // TOOLS and ARTILLERY are not requests. On the request list they read as things you could
        // ask a squadmate for, and they sat between the categories and the rows.
        Assert.DoesNotContain(view.Options, o => o.Label is "TOOLS" or "ARTILLERY");
        Assert.NotEmpty(view.Options);
        Assert.Empty(view.Trailing);
    }

    [Fact]
    public void The_tools_key_reaches_the_panels_behind_it_from_rest()
    {
        var menu = Machine();
        menu.OpenTools(T0, new MenuContext { OccupiedSlots = [1] });

        // Roles, the join code, the match, people, help, artillery and account linking all live
        // behind this one key, and it opens from rest without passing through a request list.
        Assert.Equal(MenuLevel.More, menu.Level);
        Assert.NotEmpty(MenuViewModel.From(menu).Options);
    }

    [Fact]
    public void The_tools_key_reaches_tools_from_either_other_surface()
    {
        foreach (var open in new Action<MenuStateMachine>[]
        {
            m => m.Open(T0, context: new MenuContext { OccupiedSlots = [1] }),
            m => m.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [1] }),
        })
        {
            var menu = Machine();
            open(menu);
            menu.OpenTools(T0, new MenuContext { OccupiedSlots = [1] });

            Assert.Equal(MenuLevel.More, menu.Level);
        }
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
