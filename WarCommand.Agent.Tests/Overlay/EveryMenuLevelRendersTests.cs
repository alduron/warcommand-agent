using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;
using Xunit;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// Every level of the menu, drawn, and read the way a person reads it.
/// </summary>
/// <remarks>
/// ARTILLERY carries the sentinel that means "no digit", the sentinel is -1, and the overlay
/// printed it: the home list read "-1 ARTILLERY". Nothing caught it because every test of this
/// menu asserted on paths and verb ids, which are correct, rather than on the characters that
/// reach the screen. This walks the drawn output of every level instead.
/// </remarks>
public sealed class EveryMenuLevelRendersTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly Guid Viewer = Guid.NewGuid();

    private static Catalog Catalog => ContractFixtures.Catalog;

    private static MenuStateMachine Machine() => new(MenuTree.Compile(Catalog), Catalog);

    private static MenuContext Context() => new()
    {
        OccupiedSlots = [1, 2],
        Slots = new Dictionary<int, SlotState>
        {
            [1] = new(RequestState.Open, false),
            [2] = new(RequestState.InProgress, true),
        },
        EnabledRoleIds = ["mortar", "medic"],
        SubscribedRoleIds = ["mortar"],
        GroupName = "61ST",
        DeploymentLabel = "ALPHA",
        InviteCode = "192831",
        MemberCount = 4,
        Roster = ["GHOST", "BEAR"],
        CanRestart = true,
    };

    /// <summary>Every level the hold key can reach, opened and drawn.</summary>
    private static IEnumerable<(string Where, MenuViewModel View)> EveryLevel()
    {
        // The home list.
        var home = Machine();
        home.OpenOnBoard(T0, Context());
        yield return ("home", MenuViewModel.From(home));

        // Each trailing tool and each page under MORE.
        foreach (var path in (string[])["home.fire", "home.more"])
        {
            var machine = Machine();
            machine.OpenOnBoard(T0, Context());
            SelectPath(machine, path);
            yield return (path, MenuViewModel.From(machine));
        }

        foreach (var page in (string[])["help", "roles", "match", "people"])
        {
            var machine = Machine();
            machine.OpenOnBoard(T0, Context());
            SelectPath(machine, "home.more");
            SelectPath(machine, $"board.more.{page}");
            yield return ($"board.more.{page}", MenuViewModel.From(machine));
        }

        // A board row, and the verbs it offers.
        foreach (var slot in (int[])[1, 2])
        {
            var machine = Machine();
            machine.OpenOnBoard(T0, Context());
            SelectPath(machine, $"board.{slot}");
            yield return ($"board.{slot}", MenuViewModel.From(machine));
        }

        // The request tree, every branch of it.
        foreach (var root in MenuTree.Compile(Catalog).Root)
        {
            var machine = Machine();
            machine.OpenOnBoard(T0, Context());
            SelectPath(machine, root.Path);
            yield return (root.Path, MenuViewModel.From(machine));
        }
    }

    /// <summary>Scrolls the highlight onto a path and presses select, the way a person does.</summary>
    private static void SelectPath(MenuStateMachine machine, string path)
    {
        for (var step = 0; step < machine.Options.Count; step++)
        {
            if (machine.Options[machine.Highlight].Path == path)
            {
                break;
            }

            machine.Scroll(1, T0);
        }

        Assert.Equal(path, machine.Options[machine.Highlight].Path);
        machine.Select(T0);
    }

    [Fact]
    public void No_level_ever_draws_a_negative_digit()
    {
        foreach (var (where, view) in EveryLevel())
        {
            foreach (var option in view.Options.Concat(view.Trailing))
            {
                Assert.False(
                    option.DigitDisplay.StartsWith('-'),
                    $"{where}: '{option.DigitDisplay} {option.Label}' draws a sentinel as a digit");
            }
        }
    }

    [Fact]
    public void Every_drawn_line_says_something()
    {
        foreach (var (where, view) in EveryLevel())
        {
            foreach (var option in view.Options.Concat(view.Trailing))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(option.Label),
                    $"{where}: a line is drawn with no label at all");
            }
        }
    }

    [Fact]
    public void A_digit_that_is_drawn_is_a_digit_that_works()
    {
        foreach (var (where, view) in EveryLevel())
        {
            foreach (var option in view.Options.Concat(view.Trailing))
            {
                if (option.DigitDisplay.Length == 0)
                {
                    continue;
                }

                Assert.True(
                    option.DigitDisplay.Length == 1 && char.IsAsciiDigit(option.DigitDisplay[0]),
                    $"{where}: '{option.DigitDisplay} {option.Label}' shows something that is not a key");
            }
        }
    }

    [Fact]
    public void Two_lines_on_one_level_never_claim_the_same_key()
    {
        foreach (var (where, view) in EveryLevel())
        {
            var digits = view.Options.Concat(view.Trailing)
                .Select(o => o.DigitDisplay)
                .Where(d => d.Length > 0)
                .ToList();

            Assert.Equal(digits.Count, digits.Distinct(StringComparer.Ordinal).Count());
            Assert.True(digits.Count <= 10, $"{where}: more entries than there are keys");
        }
    }

    [Fact]
    public void Never_more_than_one_line_is_highlighted()
    {
        foreach (var (where, view) in EveryLevel())
        {
            var lit = view.Options.Concat(view.Trailing).Count(o => o.IsHighlighted);

            // Home at rest has none on purpose: holding arms the surface, it does not choose
            // anything. Two would be a menu that acts on the one you are not looking at.
            Assert.True(lit <= 1, $"{where}: {lit} lines highlighted at once");
        }
    }

    [Fact]
    public void The_highlight_never_rests_on_a_line_that_cannot_be_pressed()
    {
        foreach (var (where, view) in EveryLevel())
        {
            var lines = view.Options.Concat(view.Trailing).ToList();
            var lit = lines.FirstOrDefault(o => o.IsHighlighted);

            if (lit is null)
            {
                // Two honest cases. Home at rest chooses nothing, because holding arms the surface
                // rather than opening it. And a page of pure reference text has nothing to land on;
                // BACK is still the way out and needs no highlight.
                Assert.True(
                    where == "home" || lines.Count == 0 || lines.TrueForAll(o => o.IsInfo),
                    $"{where}: has pressable lines and highlights none of them");
                continue;
            }

            Assert.False(lit.IsInfo, $"{where}: the highlight sits on read-only text");
        }
    }

    [Fact]
    public void Help_is_reference_text_and_never_a_row_of_dead_keys()
    {
        var machine = Machine();
        machine.OpenOnBoard(T0, Context());
        SelectPath(machine, "home.more");
        SelectPath(machine, "board.more.help");

        var view = MenuViewModel.From(machine);
        var lines = view.Options.Concat(view.Trailing).ToList();

        Assert.NotEmpty(lines);

        // It used to list the board verbs against digits 0 and 1 and 3 through 7. None of them did
        // anything on that page, so the whole thing read as a menu where every key was broken.
        foreach (var line in lines)
        {
            Assert.True(line.IsInfo, $"HELP offers '{line.Label}' as if it could be pressed");
            Assert.Equal(string.Empty, line.DigitDisplay);
        }
    }

    [Fact]
    public void Help_describes_the_controls_that_exist_now()
    {
        var machine = Machine();
        machine.OpenOnBoard(T0, Context());
        SelectPath(machine, "home.more");
        SelectPath(machine, "board.more.help");

        var text = string.Join(" | ", MenuViewModel.From(machine).Options.Select(o => o.Label));

        // The keys the reader actually holds, named from the bindings.
        Assert.Contains("CAPS LOCK", text, StringComparison.Ordinal);
        Assert.Contains("W", text, StringComparison.Ordinal);
        Assert.Contains("MOVE THE HIGHLIGHT", text, StringComparison.Ordinal);

        // And not the flow that navigation replaced: it told the reader to press BOARD, which has
        // not existed since the home list became one list.
        Assert.DoesNotContain("BOARD, THEN A SLOT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ON A SLOT:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Artillery_is_reachable_and_draws_no_digit()
    {
        var machine = Machine();
        machine.OpenOnBoard(T0, Context());
        var view = MenuViewModel.From(machine);

        var artillery = view.Options.Concat(view.Trailing).Single(o => o.Label == "ARTILLERY");

        // There is no spare digit: 1 to 9 are board rows and 0 is MORE. Blank is the honest answer,
        // and the row is reached by scrolling onto it like every other navigable entry.
        Assert.Equal(string.Empty, artillery.DigitDisplay);

        SelectPath(machine, "home.fire");
        Assert.Equal(MenuLevel.FireTool, machine.Level);
    }
}
