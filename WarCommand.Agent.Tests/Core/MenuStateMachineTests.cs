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

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 0, 0, 6 })]
    public void Releasing_the_key_closes_the_menu_from_every_level(int[] digits)
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        foreach (var digit in digits)
        {
            menu.Digit(digit, T0);
        }

        menu.KeyUp(T0.AddMilliseconds(80));

        // No level may outlive the key. While the menu is open the digit row, Escape and Backspace
        // are swallowed, so one that stays open with nothing held eats them across every window.
        Assert.False(menu.IsOpen);
        Assert.False(MenuStateMachine.IsLatched);
    }

    [Fact]
    public void Several_modifiers_are_all_carried_and_all_marked()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);
        Assert.Equal(MenuLevel.Confirm, menu.Level);

        var offered = menu.Options.Select(o => o.Path[(o.Path.LastIndexOf('.') + 1)..]).ToList();
        var first = offered.IndexOf("danger_close") + 1;
        var second = offered.IndexOf("he") + 1;
        Assert.True(first > 0 && second > 0, "the fixture type offers danger_close and he");

        menu.Digit(first, T0);
        menu.Digit(second, T0);

        // Both, and both marked. Choosing two and seeing one is worse than seeing neither: the
        // line reads as the whole request and is not.
        Assert.Equal(2, menu.Modifiers.Count);
        Assert.Contains("danger_close", menu.Modifiers);
        Assert.Contains("he", menu.Modifiers);
        Assert.Equal(2, menu.Options.Count(o => o.IsChosen));

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));
        Assert.Equal(2, ready.Modifiers.Count);
    }

    [Fact]
    public void Roles_match_and_people_are_offered_once_they_have_something_to_draw()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext
        {
            OccupiedSlots = [1],
            EnabledRoleIds = ["mortar", "medic"],
            SubscribedRoleIds = ["mortar"],
            DeploymentLabel = "ALPHA",
            InviteCode = "123456",
            MemberCount = 6,
            Roster = ["BEAR", "WOLF"],
        });
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        var offered = menu.Options.Select(o => o.VerbId).ToList();
        Assert.Contains("roles", offered);
        Assert.Contains("match", offered);
        Assert.Contains("people", offered);

        // ROLES marks what you already receive, and a digit asks the server to change it.
        menu.Digit(MenuStateMachine.MoreDigits["roles"], T0);
        Assert.Equal(MenuLevel.Roles, menu.Level);
        Assert.Equal(2, menu.Options.Count);
        Assert.Single(menu.Options.Where(o => o.IsChosen));

        var toggled = Assert.IsType<MenuRoleToggled>(menu.Digit(2, T0));
        Assert.Equal("medic", toggled.RoleId);
    }

    [Fact]
    public void Link_is_offered_from_more_and_restart_is_not()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { CanRestart = true, LinkPromptPending = true });
        menu.Digit(0, T0);

        var offered = menu.Options.Select(o => o.VerbId).ToList();
        Assert.Contains("link", offered);

        // RESTART left MORE. It clears the board and drops every visitor, and it had two doors:
        // one here and one on the match page. A destructive action gets exactly one, on the page
        // you open to act on the match.
        Assert.DoesNotContain("restart", offered);

        // Still an action, not a page: it closes the menu and the agent performs it.
        var panel = Assert.IsType<MenuPanelRequested>(menu.Digit(MenuStateMachine.MoreDigits["link"], T0));
        Assert.Equal("link", panel.PanelId);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void A_page_with_nothing_to_draw_is_not_offered()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { OccupiedSlots = [1] });
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        var offered = menu.Options.Select(o => o.VerbId).ToList();
        Assert.DoesNotContain("roles", offered);
        Assert.DoesNotContain("match", offered);
        Assert.DoesNotContain("people", offered);
    }

    [Fact]
    public void The_home_list_is_tools_then_requests_then_rows_in_that_order()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { OccupiedSlots = [2, 5] });

        // BOARD used to be an entry here, nesting the board menu inside the request menu: taking a
        // row meant REQUEST, then BOARD, then a slot, then a verb. Then it became a separate level
        // joined by a crossover edge, with MORE hanging off the end of it. One list, no modes.
        var paths = menu.Options.Select(o => o.Path).ToList();
        var firstRow = paths.FindIndex(p => p.StartsWith("board.", StringComparison.Ordinal));
        var tools = paths.IndexOf("home.more");
        var artillery = paths.IndexOf("home.fire");

        // Everything that is not a row sits ABOVE the rows, so walking UP off a row reaches the
        // request categories and then the tools, and walking DOWN reaches the rows. Neither
        // direction crosses the board to get to the other group, which is what made it confusing.
        Assert.True(artillery >= 0 && tools >= 0);
        Assert.True(tools < firstRow, "tools sit above the rows");
        Assert.True(artillery < firstRow, "artillery sits above the rows");
        Assert.Equal(paths.Count - 1, paths.FindLastIndex(p => p.StartsWith("board.", StringComparison.Ordinal)));
        Assert.Equal(2, paths.Count(p => p.StartsWith("board.", StringComparison.Ordinal)));
        Assert.DoesNotContain(menu.Options, o => o.Label == "BOARD");
    }

    [Fact]
    public void Down_from_rest_lands_on_the_first_board_row()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [2, 5] });

        // The board is not a level any more, it is a stretch of the home list, so opening onto it
        // is a highlight position rather than a mode.
        Assert.Equal(MenuLevel.Root, menu.Level);
        Assert.Equal(2, menu.HighlightedSlot);

        // Select takes that row straight to its verbs. No slot digit, no BOARD detour, and the
        // row stays highlighted while you choose what to do to it.
        menu.Select(T0);
        Assert.Equal(MenuLevel.BoardAction, menu.Level);
        Assert.Equal(2, menu.HighlightedSlot);
    }

    [Fact]
    public void Back_at_the_root_returns_to_rest_and_never_closes()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        // Back means one thing everywhere: leave the level you are on. At the top that is a return
        // to rest, with the key still held and the board still on screen. It used to CLOSE, so the
        // key that means "I did not mean that" also ended the interaction and the only way back in
        // was to let go and start again.
        Assert.IsType<MenuNavigated>(menu.Back(T0));
        Assert.Equal(MenuLevel.Root, menu.Level);
        Assert.Equal(MenuStateMachine.NoHighlight, menu.Highlight);
    }

    [Fact]
    public void Back_below_the_root_climbs_one_level_and_stays_open()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [4] });
        menu.Select(T0);
        Assert.Equal(MenuLevel.BoardAction, menu.Level);

        menu.Back(T0);

        Assert.Equal(MenuLevel.Root, menu.Level);
        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void A_type_on_two_branches_names_each_branch_separately()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        // fortify sits at build.2 and build.3, so BUILD used to appear twice under BUILD with
        // nothing saying which held walls and which held defenses.
        var build = menu.Options.Single(o => o.Label == "BUILD");
        var labels = build.Children.Select(c => c.Label).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("WALLS", labels);
        Assert.Contains("DEFENSES", labels);
    }

    [Fact]
    public void No_branch_anywhere_draws_the_same_label_twice()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        // A duplicate label is a coin flip for the user, and the compiler is what should catch it.
        foreach (var category in menu.Options.Where(o => o.Children.Count > 0))
        {
            var labels = category.Children.Select(c => c.Label).ToList();
            Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void A_failed_read_leaves_the_coordinate_level_exactly_where_it_was()
    {
        var menu = Machine();
        menu.Open(T0, snapshot: null);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        Assert.Equal(MenuLevel.Coordinate, menu.Level);

        // Select on this level asks the app to read the map. A refusal is the app declining to call
        // AcceptReadCoordinate, and nothing about the menu may move: no level change, no point.
        Assert.IsType<MenuCoordinateReadRequested>(menu.Select(T0));
        Assert.Equal(MenuLevel.Coordinate, menu.Level);

        Assert.IsType<MenuCoordinateReadRequested>(menu.Select(T0.AddSeconds(1)));
        Assert.Equal(MenuLevel.Coordinate, menu.Level);
    }

    [Fact]
    public void A_read_that_lands_fills_the_point_and_moves_to_confirm()
    {
        var menu = Machine();
        menu.Open(T0, snapshot: null);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        var read = new MapPoint(97.56m, 108.62m, "map_readout", "x97.56 y108.62", 0.07m);
        menu.AcceptReadCoordinate(read, T0);

        Assert.Equal(MenuLevel.Confirm, menu.Level);

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));
        Assert.Equal(97.56m, ready.Point.X);
        Assert.Equal(108.62m, ready.Point.Y);
    }

    [Fact]
    public void Releasing_with_no_coordinate_sends_nothing_and_says_why()
    {
        var menu = Machine();
        menu.Open(T0, snapshot: null);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        // Released on the coordinate level having never got a reading. Nothing is sent, and the
        // reason is named so the surface can say so: a menu that just closes looks like it worked.
        var outcome = menu.KeyUp(T0.AddSeconds(2));

        var discarded = Assert.IsType<MenuDiscarded>(outcome);
        Assert.Equal("released_before_confirm", discarded.Reason);
    }

    [Fact]
    public void A_confirm_reached_without_a_point_refuses_to_send()
    {
        var menu = Machine();
        menu.Open(T0, snapshot: null);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);
        menu.AcceptReadCoordinate(new MapPoint(1m, 2m, "map_readout", null, null), T0);
        Assert.Equal(MenuLevel.Confirm, menu.Level);

        // Backspace clears the point and drops back, so confirm can never hold a stale reading.
        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Coordinate, menu.Level);
    }

    [Fact]
    public void The_artillery_tool_keeps_both_ends_and_never_closes_after_a_read()
    {
        var menu = Machine();
        menu.Open(T0, snapshot: null);

        // Walk to ARTILLERY on the home list and open it.
        while (menu.Options[menu.Highlight].Path != "home.fire")
        {
            menu.Scroll(1, T0);
        }

        menu.Select(T0);
        Assert.Equal(MenuLevel.FireTool, menu.Level);

        // Set the gun. GUN HERE used to be a one-shot buried in MORE that closed the menu, so
        // re-ranging meant walking the whole tree again.
        menu.Select(T0);
        Assert.Equal(MenuLevel.GunPosition, menu.Level);
        menu.AcceptReadCoordinate(new MapPoint(10m, 10m, "map_readout", null, null), T0);
        Assert.Equal(MenuLevel.FireTool, menu.Level);
        Assert.Equal(10m, menu.ToolGun?.X);

        // Then the target, from the same page, without leaving it.
        menu.Scroll(1, T0);
        menu.Select(T0);
        Assert.Equal(MenuLevel.FireTarget, menu.Level);
        menu.AcceptReadCoordinate(new MapPoint(20m, 20m, "map_readout", null, null), T0);
        Assert.Equal(MenuLevel.FireTool, menu.Level);
        Assert.Equal(20m, menu.ToolTarget?.X);

        // And the target can be re-read over and over, which is the whole point of the tool.
        menu.Select(T0);
        menu.AcceptReadCoordinate(new MapPoint(30m, 30m, "map_readout", null, null), T0);
        Assert.Equal(30m, menu.ToolTarget?.X);
        Assert.Equal(10m, menu.ToolGun?.X);
        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void The_highlight_wraps_at_both_ends_of_a_list()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        var count = menu.Options.Count;

        // Off the bottom comes back to the top.
        menu.Scroll(count - 1, T0);
        Assert.Equal(count - 1, menu.Highlight);
        menu.Scroll(1, T0);
        Assert.Equal(0, menu.Highlight);

        // And off the top goes to the bottom.
        menu.Scroll(-1, T0);
        Assert.Equal(count - 1, menu.Highlight);
    }

    [Fact]
    public void Up_off_the_top_of_the_board_reaches_the_requests_above_it()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [3] });
        Assert.Equal(3, menu.HighlightedSlot);

        // No crossover, no level change: the request above this row is simply the previous entry.
        menu.Scroll(-1, T0);

        Assert.Equal(MenuLevel.Root, menu.Level);
        Assert.Null(menu.HighlightedSlot);
    }

    [Fact]
    public void Moving_to_a_row_and_selecting_it_takes_a_verb()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext { OccupiedSlots = [4] });
        Assert.Equal(4, menu.HighlightedSlot);

        menu.Select(T0.AddSeconds(2));
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
    public void A_held_menu_never_times_out()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        Assert.IsType<MenuNothing>(menu.Tick(T0.AddSeconds(5), holdKeyDown: true));
        Assert.IsType<MenuNothing>(menu.Tick(T0.AddMinutes(10), holdKeyDown: true));
        Assert.Equal(MenuLevel.Root, menu.Level);
    }

    [Fact]
    public void A_menu_opened_by_a_nav_key_is_still_a_held_menu()
    {
        // The regression: opening moved off the hold key onto the nav key, the driver's held flag
        // was never set, and the orphan guard closed the menu 1.5s later under the user's finger.
        // The machine's contract is that holdKeyDown is what decides, never how the menu opened.
        var menu = Machine();
        menu.Open(T0, Snapshot);

        for (var second = 1; second <= 60; second++)
        {
            Assert.IsType<MenuNothing>(menu.Tick(T0.AddSeconds(second), holdKeyDown: true));
        }

        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void A_menu_left_open_with_the_key_up_is_closed_by_the_orphan_guard()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        Assert.IsType<MenuNothing>(menu.Tick(T0.AddSeconds(1), holdKeyDown: false));
        Assert.IsType<MenuDiscarded>(menu.Tick(T0.AddSeconds(2), holdKeyDown: false));
    }

    [Fact]
    public void Releasing_after_a_long_hold_still_leaves_the_guard_a_full_window()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        // Held for a minute, so the guard's clock restarts at the moment of release rather than
        // firing instantly on the first tick after it.
        Assert.IsType<MenuNothing>(menu.Tick(T0.AddMinutes(1), holdKeyDown: true));
        Assert.IsType<MenuNothing>(menu.Tick(T0.AddMinutes(1).AddMilliseconds(500), holdKeyDown: false));
        Assert.IsType<MenuDiscarded>(menu.Tick(T0.AddMinutes(1).AddSeconds(2), holdKeyDown: false));
    }

    [Fact]
    public void Losing_focus_closes_the_menu()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);

        Assert.IsType<MenuDiscarded>(menu.FocusLost(T0.AddSeconds(1)));
    }

    [Fact]
    public void A_row_then_a_verb_runs_it_immediately()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, Slots(1, 4));

        // Walk to slot 4 in the home list and take it. Two presses to a verb list.
        while (menu.HighlightedSlot != 4)
        {
            menu.Scroll(1, T0);
        }

        menu.Select(T0);
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
    public void More_offers_only_what_the_agent_can_actually_do()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { OccupiedSlots = [1] });
        menu.Digit(0, T0);
        menu.Digit(0, T0);

        // An entry the agent cannot honour is absent, never shown and dead. Roles, match, people,
        // restart and link all returned a panel id nothing handled: they closed the menu and did
        // nothing, which is the one click this product refuses to offer anywhere.
        var offered = menu.Options.Select(o => o.VerbId).ToList();
        Assert.Contains("help", offered);

        // GUN HERE is gone from this page. It set the same gun position the ARTILLERY tool sets,
        // by a second route with its own way back out, so backing out of a gun read landed you
        // somewhere different depending on which entry you came in through.
        Assert.DoesNotContain("gun", offered);
        Assert.Contains("join", offered);
        Assert.DoesNotContain("roles", offered);
        Assert.DoesNotContain("match", offered);
        Assert.DoesNotContain("people", offered);
        Assert.DoesNotContain("restart", offered);
        Assert.DoesNotContain("link", offered);
    }

    [Fact]
    public void There_is_one_gun_position_and_one_route_to_it()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot, new MenuContext { OccupiedSlots = [1] });

        // Not on the TOOLS page. There were two entry points to the same value, and backing out of
        // a gun read went to whichever one the code assumed rather than the one you used.
        menu.Digit(0, T0);
        Assert.DoesNotContain(menu.Options, o => o.VerbId == "gun");

        // The one route is ARTILLERY on the home list, and backing out of it returns HOME.
        menu.Back(T0);
        while (menu.Options[menu.Highlight].Path != "home.fire")
        {
            menu.Scroll(-1, T0);
        }

        menu.Select(T0);
        Assert.Equal(MenuLevel.FireTool, menu.Level);

        menu.Back(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
    }

    [Fact]
    public void Reading_a_gun_feeds_the_row_brackets_as_well_as_the_tool()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext());
        while (menu.Options[menu.Highlight].Path != "home.fire")
        {
            menu.Scroll(-1, T0);
        }

        menu.Select(T0);
        menu.Select(T0);
        Assert.Equal(MenuLevel.GunPosition, menu.Level);

        // One read, both consumers: the ARTILLERY section ranges from it and every mortar row
        // draws its bracket from it.
        var set = Assert.IsType<MenuGunPositionSet>(menu.AcceptReadCoordinate(Snapshot, T0));
        Assert.Equal(Snapshot, set.Point);
        Assert.Equal(Snapshot, menu.ToolGun);

        // And clearing says so, so the rows stop ranging from a gun the tool has forgotten.
        while (menu.Options[menu.Highlight].Path != "fire.clear")
        {
            menu.Scroll(1, T0);
        }

        Assert.IsType<MenuGunPositionCleared>(menu.Select(T0));
        Assert.Null(menu.ToolGun);
    }

    [Fact]
    public void Copy_is_a_row_verb_now_that_the_chord_is_gone()
    {
        var menu = Machine();
        menu.OpenOnBoard(T0, new MenuContext
        {
            OccupiedSlots = [3],
            Slots = new Dictionary<int, SlotState> { [3] = new(RequestState.Open, false) },
        });
        menu.Select(T0);

        // Its digit is a position in what this row offers, not a fixed identity, so the test asks
        // the menu where COPY landed rather than assuming a number that a hand may not reach.
        var copy = menu.Options.Single(o => o.VerbId == "copy");
        var action = Assert.IsType<MenuBoardAction>(menu.Digit(copy.Digit, T0));

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

        // Held, like every level. Nothing detaches from the key, because a menu that outlives the
        // key swallows the digit row and Backspace over whatever window the user is typing in.
        Assert.False(MenuStateMachine.IsLatched);

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

        // 0 is MORE on the home list, matching where it is drawn. It used to open the board, which
        // is no longer a level: the rows are part of the home list.
        menu.Digit(0, T0);
        Assert.Equal(MenuLevel.More, menu.Level);

        // HELP is a level, not a panel handed to the app: it draws the digits while the key is
        // held, like every other level, instead of closing the menu and doing nothing.
        menu.Digit(1, T0);
        Assert.Equal(MenuLevel.Help, menu.Level);
        Assert.NotEmpty(menu.Options);

        menu.Backspace(T0);
        Assert.Equal(MenuLevel.More, menu.Level);

        menu.Backspace(T0);
        Assert.Equal(MenuLevel.Root, menu.Level);
    }

    [Fact]
    public void A_two_point_type_asks_for_its_second_point_before_it_will_send()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["move"], T0);
        menu.Digit(1, T0);

        Assert.Equal(2, ContractFixtures.Catalog.RequestType("transport_move")!.Arity);

        // The snapshot is PICKUP. The draft is not finished, so it waits on the point level for
        // dropoff rather than jumping to confirm.
        Assert.Equal(MenuLevel.Coordinate, menu.Level);
        Assert.Equal(1, menu.PointsWanted);
        Assert.Equal("DROPOFF", menu.CurrentPointLabel);

        // Releasing here sends nothing. It used to send ONE point, which the server rejects with
        // point_count_mismatch, so no transport, lift or escort request could ever be made.
        Assert.IsType<MenuDiscarded>(menu.KeyUp(T0));
    }

    [Fact]
    public void A_two_point_type_sends_both_points_in_ordinal_order()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["move"], T0);
        menu.Digit(1, T0);

        var dropoff = new MapPoint(42.5m, 43.5m, "map_readout", null, null);
        menu.AcceptReadCoordinate(dropoff, T0);

        Assert.Equal(MenuLevel.Confirm, menu.Level);
        Assert.Equal(0, menu.PointsWanted);

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));

        Assert.Equal("transport_move", ready.TypeId);
        Assert.Equal(2, ready.Points.Count);
        Assert.Equal(Snapshot, ready.Points[0]);
        Assert.Equal(dropoff, ready.Points[1]);

        // Point stays the first one, for every caller that only ever wants the target.
        Assert.Equal(Snapshot, ready.Point);
    }

    [Fact]
    public void A_one_point_type_still_sends_exactly_one()
    {
        var menu = Machine();
        menu.Open(T0, Snapshot);
        menu.Digit(ContractFixtures.Catalog.MenuCategories["fire"], T0);
        menu.Digit(1, T0);

        var ready = Assert.IsType<MenuRequestReady>(menu.KeyUp(T0));

        Assert.Single(ready.Points);
        Assert.Equal(Snapshot, ready.Points[0]);
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
