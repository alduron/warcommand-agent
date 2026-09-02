using System.Linq;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A menu that swallowed W for a second and a half would get somebody killed, and the tree is
/// compiled from the catalog rather than hand drawn.
/// </summary>
public class MenuStateMachineTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;
    private static readonly MapPoint Snapshot = new(85.53m, 69.42m, "map_readout", "x85.53 y69.42", 0.94m);

    private static MenuTree Tree => MenuTree.Compile(ContractFixtures.Catalog);

    private static MenuStateMachine Machine() => new(Tree, ContractFixtures.Catalog);

    private static MenuContext Slots(params int[] slots) => new() { OccupiedSlots = slots };

    [Theory]
    [InlineData(MenuKeyClass.Digit, true)]
    [InlineData(MenuKeyClass.Escape, true)]
    [InlineData(MenuKeyClass.Backspace, true)]
    [InlineData(MenuKeyClass.PushToTalk, true)]
    [InlineData(MenuKeyClass.Other, false)]
    public void Exactly_four_key_classes_are_swallowed(MenuKeyClass key, bool swallowed)
    {
        Assert.Equal(swallowed, MenuStateMachine.Swallows(key));
    }

    [Fact]
    public void Movement_and_comms_keep_working_while_a_menu_is_open()
    {
        var swallowed = Enum.GetValues<MenuKeyClass>().Where(MenuStateMachine.Swallows).ToList();

        Assert.Equal(4, swallowed.Count);
        Assert.DoesNotContain(MenuKeyClass.Other, swallowed);
    }

    [Fact]
    public void The_tree_is_compiled_from_menu_paths()
    {
        var tree = Tree;

        var fire = tree.Root.Single(e => e.Digit == ContractFixtures.Catalog.MenuCategories["fire"]);
        Assert.Equal("FIRE", fire.Label);
        Assert.Equal("mortar_fire", fire.Children.Single(c => c.Digit == 1).TypeId);
        Assert.Equal("artillery_fire", fire.Children.Single(c => c.Digit == 2).TypeId);
    }

    [Fact]
    public void Every_catalog_menu_path_reaches_a_leaf()
    {
        var tree = Tree;

        foreach (var type in ContractFixtures.Catalog.RequestTypes)
        {
            foreach (var path in type.MenuPaths)
            {
                var entry = tree.Find(path);
                Assert.True(entry is not null, $"{type.Id} menu path '{path}' is not in the compiled tree");
                Assert.Equal(type.Id, entry!.TypeId);
            }
        }
    }

    [Fact]
    public void A_kind_leaf_carries_its_parents_type()
    {
        var tree = Tree;

        var hesco = tree.Find("build.2.5")!;
        Assert.Equal("fortify", hesco.TypeId);
        Assert.Equal("hesco", hesco.StructureKindId);
        Assert.Equal("WALL", hesco.Label);

        var ammo = tree.Find("supply.1")!;
        Assert.Equal("resupply", ammo.TypeId);
        Assert.Equal("ammo", ammo.SupplyKindId);
    }

    [Fact]
    public void A_branch_that_also_names_a_type_keeps_the_bare_type_reachable()
    {
        // 'fortify' with no structure is legal and must not become voice-only.
        var bare = Tree.Find("build.2.0")!;

        Assert.Equal("fortify", bare.TypeId);
        Assert.Null(bare.StructureKindId);
    }

    [Fact]
    public void A_type_whose_roles_are_not_enabled_is_absent_from_the_menu()
    {
        var tree = MenuTree.Compile(ContractFixtures.Catalog, ["mortar"]);

        Assert.NotNull(tree.Find("fire.1"));
        Assert.Null(tree.Find("medical.1"));
    }

    [Fact]
    public void A_tap_latches_the_menu_so_it_can_be_driven_with_the_key_released()
    {
        var menu = Machine();
        menu.Open(T0);

        // Down and straight back up, having chosen nothing. That is a tap.
        var outcome = menu.KeyUp(T0.AddMilliseconds(80));

        Assert.IsType<MenuNavigated>(outcome);
        Assert.True(menu.IsOpen);
        Assert.True(menu.IsLatched);
        Assert.Equal(MenuLevel.Root, menu.Level);

        // And the digits still work with nothing held down.
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0.AddSeconds(1));
        Assert.Equal(MenuLevel.Branch, menu.Level);
    }

    [Fact]
    public void A_hold_that_reached_a_level_still_discards_on_release()
    {
        var menu = Machine();
        menu.Open(T0);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0.AddMilliseconds(50));

        var outcome = menu.KeyUp(T0.AddMilliseconds(120));

        Assert.IsType<MenuDiscarded>(outcome);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void A_latched_menu_takes_a_whole_request_from_the_keyboard_alone()
    {
        var menu = Machine();
        menu.Open(T0);
        _ = menu.KeyUp(T0.AddMilliseconds(80));

        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0.AddSeconds(1));
        menu.Digit(1, T0.AddSeconds(2));
        Assert.Equal(MenuLevel.Coordinate, menu.Level);

        foreach (var digit in new[] { 8, 5, 5, 3, 6, 9, 4, 2 })
        {
            menu.Digit(digit, T0.AddSeconds(3));
        }

        Assert.Equal(MenuLevel.Confirm, menu.Level);
        Assert.Equal("mortar_fire", menu.SelectedTypeId);
    }

    [Fact]
    public void Zero_reaches_the_board_and_a_slot_takes_a_verb()
    {
        var menu = Machine();
        menu.Open(T0, context: new MenuContext { OccupiedSlots = [4] });
        _ = menu.KeyUp(T0.AddMilliseconds(80));

        menu.Digit(0, T0.AddSeconds(1));
        Assert.Equal(MenuLevel.Board, menu.Level);

        menu.Digit(4, T0.AddSeconds(2));
        Assert.Equal(MenuLevel.BoardAction, menu.Level);

        var outcome = menu.Digit(1, T0.AddSeconds(3));

        var action = Assert.IsType<MenuBoardAction>(outcome);
        Assert.Equal("accept", action.VerbId);
        Assert.Equal(4, action.Slot);
    }

    [Fact]
    public void Navigating_the_menu_does_not_resample_the_coordinate()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0.AddMilliseconds(100));
        menu.Digit(1, T0.AddMilliseconds(200));
        var outcome = menu.KeyUp(T0.AddMilliseconds(300));

        var ready = Assert.IsType<MenuRequestReady>(outcome);
        Assert.Equal(Snapshot, ready.Point);
        Assert.Equal("mortar_fire", ready.TypeId);
    }

    [Fact]
    public void With_no_capture_the_coordinate_level_takes_eight_digits()
    {
        var menu = Machine();
        menu.Open(T0);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        Assert.Equal(MenuLevel.Coordinate, menu.Level);
        foreach (var digit in new[] { 8, 5, 5, 3, 6, 9, 4, 2 })
        {
            menu.Digit(digit, T0);
        }

        Assert.Equal(MenuLevel.Confirm, menu.Level);
        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));
        Assert.Equal(85.53m, ready.Point.X);
        Assert.Equal(69.42m, ready.Point.Y);
        Assert.Equal("typed_grid", ready.Point.Source);
        Assert.Null(ready.Point.Confidence);
    }

    [Fact]
    public void A_digit_with_no_entry_at_that_position_is_ignored_silently()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        var outcome = menu.Digit(ContractFixtures.Catalog.MenuCategories["medical"], T0);
        Assert.IsType<MenuNavigated>(outcome);

        // MEDICAL has two entries; 9 is not one of them.
        Assert.IsType<MenuNothing>(menu.Digit(9, T0));
        Assert.Equal(MenuLevel.Branch, menu.Level);
    }

    [Fact]
    public void Releasing_before_confirm_discards()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);

        var outcome = menu.KeyUp(T0.AddMilliseconds(50));

        Assert.IsType<MenuDiscarded>(outcome);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Modifiers_live_on_the_confirm_level_and_toggle()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        var urgent = menu.Options.Single(o => string.Equals(o.Label, "URGENT", StringComparison.Ordinal));
        menu.Digit(urgent.Digit, T0);
        Assert.Contains("urgent", menu.Modifiers);

        menu.Digit(urgent.Digit, T0);
        Assert.Empty(menu.Modifiers);
    }

    [Fact]
    public void Backspace_goes_up_one_level()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["build"], T0);
        menu.Digit(2, T0);
        Assert.Equal(MenuLevel.Branch, menu.Level);

        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Branch, menu.Level);

        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
    }

    [Fact]
    public void Escape_cancels_everything()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);

        Assert.IsType<MenuDiscarded>(menu.Escape(T0));
        Assert.Equal(MenuLevel.Closed, menu.Level);
    }

    [Fact]
    public void The_menu_closes_on_its_own_after_six_seconds()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        Assert.IsType<MenuNothing>(menu.Tick(T0.AddSeconds(5)));
        Assert.IsType<MenuDiscarded>(menu.Tick(T0.AddSeconds(7)));
    }

    [Fact]
    public void Losing_focus_closes_the_menu()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        Assert.IsType<MenuDiscarded>(menu.FocusLost(T0.AddSeconds(1)));
    }

    [Fact]
    public void Zero_board_then_a_slot_then_a_verb_runs_it_immediately()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, Slots(1, 4));

        menu.Digit(0, T0);
        Assert.Equal(MenuLevel.Board, menu.Level);

        menu.Digit(4, T0);
        Assert.Equal(MenuLevel.BoardAction, menu.Level);

        var action = Assert.IsType<MenuBoardAction>(menu.Digit(1, T0));
        Assert.Equal("accept", action.VerbId);
        Assert.Equal(4, action.Slot);
    }

    [Fact]
    public void Zero_reaches_more_even_on_a_saturated_board()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, Slots(1, 2, 3, 4, 5, 6, 7, 8, 9));

        menu.Digit(0, T0);
        menu.Digit(0, T0);

        Assert.Equal(MenuLevel.More, menu.Level);
    }

    [Fact]
    public void More_offers_restart_and_link_only_when_the_context_allows_them()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { OccupiedSlots = [1] });
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        var offered = menu.Options.Select(o => o.VerbId).ToList();
        Assert.Contains("help", offered);
        Assert.Contains("gun", offered);
        Assert.DoesNotContain("restart", offered);
        Assert.DoesNotContain("link", offered);
        Assert.Equal(MenuOutcome.None, menu.Digit(7, T0));

        menu.Open(T0, Snapshot, new MenuContext { CanRestart = true, LinkPromptPending = true });
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        Assert.Equal("restart", Assert.IsType<MenuPanelRequested>(menu.Digit(7, T0)).PanelId);
    }

    [Fact]
    public void A_panel_closes_the_menu_and_gun_here_commits_the_key_down_snapshot()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        Assert.Equal("roles", Assert.IsType<MenuPanelRequested>(menu.Digit(2, T0)).PanelId);
        Assert.False(menu.IsOpen);

        menu.Open(T0, Snapshot);
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        Assert.Equal(Snapshot, Assert.IsType<MenuGunPositionSet>(menu.Digit(5, T0)).Point);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Copy_is_a_row_verb_now_that_the_chord_is_gone()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, Slots(3));
        menu.Digit(0, T0);
        menu.Digit(3, T0);

        var action = Assert.IsType<MenuBoardAction>(menu.Digit(7, T0));

        Assert.Equal("copy", action.VerbId);
        Assert.Equal(3, action.Slot);
    }

    [Fact]
    public void Join_takes_six_digits_and_commits_with_no_confirm_step()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(0, T0);
        menu.Digit(0, T0);
        menu.Digit(6, T0);

        // Six digits is not a hold, so the level detaches from the key and a release changes nothing.
        Assert.True(menu.IsLatched);
        Assert.Equal(MenuOutcome.None, menu.KeyUp(T0));

        foreach (var digit in new[] { 9, 2, 1, 5, 8 })
        {
            Assert.IsType<MenuNavigated>(menu.Digit(digit, T0));
        }

        var joined = Assert.IsType<MenuJoinReady>(menu.Digit(5, T0));
        Assert.Equal("921585", joined.InviteCode);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Zero_zero_one_reaches_help_and_backspace_walks_back_out()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, Slots(1));
        menu.Digit(0, T0);
        menu.Digit(0, T0);
        Assert.Equal("help", Assert.IsType<MenuPanelRequested>(menu.Digit(1, T0)).PanelId);

        menu.Open(T0, Snapshot, Slots(1));
        menu.Digit(0, T0);
        menu.Digit(0, T0);
        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Board, menu.Level);

        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
    }

    [Fact]
    public void A_two_point_type_hands_its_second_point_to_the_awaiting_point_flow()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["move"], T0);
        menu.Digit(1, T0);

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));

        Assert.Equal("transport_move", ready.TypeId);
        Assert.Equal(Snapshot, ready.Point);
        Assert.Equal(2, ContractFixtures.Catalog.RequestType(ready.TypeId)!.Arity);
    }

    [Fact]
    public void The_supply_default_kind_is_applied_when_the_leaf_names_none()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["supply"], T0);
        menu.Digit(1, T0);

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));

        Assert.Equal("resupply", ready.TypeId);
        Assert.Equal("ammo", ready.SupplyKindId);
    }
}
