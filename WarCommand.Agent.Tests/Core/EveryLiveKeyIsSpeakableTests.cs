using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Voice selects the option a key would select, so a key that does something and cannot be said is
/// a dead feature by definition.
/// </summary>
/// <remarks>
/// This exists because the gaps were never in the recognizer. They were levels whose live keys were
/// drawn by no row: the coordinate page draws nothing at all, BACK is drawn nowhere, and 0 opened
/// TOOLS from eight levels while appearing on none of them. Each was found by hand, one report at a
/// time. This walks every level instead, and it presses the keys rather than reading the code, so a
/// new level or a new key arrives already covered.
/// </remarks>
public class EveryLiveKeyIsSpeakableTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static readonly string[] Numbers =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>A board and a group with enough in them that every level has something to draw.</summary>
    private static MenuContext Context => new()
    {
        OccupiedSlots = [1, 2],
        Slots = new Dictionary<int, SlotState>
        {
            [1] = new(RequestState.Open, false),
            [2] = new(RequestState.InProgress, true),
        },
        EnabledRoleIds = ["mortar", "medic"],
        SubscribedRoleIds = ["mortar"],
        DeploymentLabel = "ALPHA",
        InviteCode = "123456",
        MemberCount = 6,
        Roster = ["BEAR", "WOLF"],
        CanRestart = true,
        LinkPromptPending = true,
    };

    /// <summary>Nine rows drawn and more behind them, so the page turn is live.</summary>
    private static MenuContext FullPage => new()
    {
        OccupiedSlots = [1, 2, 3, 4, 5, 6, 7, 8, 9],
        Slots = Enumerable.Range(1, 9).ToDictionary(i => i, _ => new SlotState(RequestState.Open, false)),
        BoardPages = 2,
    };

    /// <summary>Every level a hold can reach, named. A new one added here is covered by all five.</summary>
    private static readonly string[] AllLevels =
    [
        "Root", "Board", "BoardPaged", "Branch", "Coordinate", "Confirm", "BoardAction",
        "More", "Help", "Roles", "Match", "People", "Join", "RangeTool", "RangeEnd",
    ];

    public static IEnumerable<object[]> Levels() => AllLevels.Select(level => new object[] { level });

    [Theory]
    [MemberData(nameof(Levels))]
    public void Every_digit_that_does_something_can_be_said(string level)
    {
        var surface = MenuSpeech.SurfaceOf(Walk(level));

        for (var digit = 0; digit <= 9; digit++)
        {
            var probe = Walk(level);
            var before = Signature(probe);
            var outcome = probe.Digit(digit, T0);
            if (outcome is MenuNothing && Signature(probe) == before)
            {
                continue;
            }

            var said = MenuSpeech.Match(Numbers[digit], surface);
            Assert.True(
                said is not null && said.Digit == digit,
                $"{level}: pressing {digit} does something, but saying '{Numbers[digit]}' reaches "
                + (said is null ? "nothing" : $"'{said.Label}'"));
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void Moving_can_be_said_wherever_it_does_something(string level)
    {
        foreach (var (notches, word, path) in new[]
        {
            (-1, "up", MenuSpeech.Keys.Up),
            (1, "down", MenuSpeech.Keys.Down),
        })
        {
            var menu = Walk(level);
            var before = Signature(menu);
            var outcome = menu.Scroll(notches, T0);
            if (outcome is MenuNothing && Signature(menu) == before)
            {
                continue;
            }

            var surface = MenuSpeech.SurfaceOf(Walk(level));
            Assert.True(
                MenuSpeech.Match(word, surface)?.Path == path,
                $"{level}: scrolling {word} does something ({outcome.GetType().Name}) and saying "
                + $"'{word}' does not reach it");
        }
    }

    [Fact]
    public void A_board_past_one_page_can_be_turned_by_voice()
    {
        // The board is a window over a longer list and DOWN off the last row is the ONLY thing that
        // turns it, key or voice. Without a word for it every row past the first nine was reachable
        // by nothing at all: page two draws its own lines 1 upward and the surface only ever holds
        // the page in front of you.
        var menu = Walk("BoardPaged");
        var surface = MenuSpeech.SurfaceOf(menu);

        Assert.Equal(MenuSpeech.Keys.Down, MenuSpeech.Match("down", surface)?.Path);

        // Walk the highlight to the last row, then one more.
        MenuOutcome outcome = MenuOutcome.None;
        for (var i = 0; i < menu.Options.Count; i++)
        {
            outcome = menu.Scroll(1, T0);
        }

        Assert.IsType<MenuBoardPaged>(outcome);
        Assert.Equal(1, ((MenuBoardPaged)outcome).Delta);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void Back_can_be_said_wherever_it_does_something(string level)
    {
        var menu = Walk(level);
        var before = Signature(menu);
        menu.Back(T0);
        if (Signature(menu) == before)
        {
            return;
        }

        var surface = MenuSpeech.SurfaceOf(Walk(level));
        Assert.Equal(MenuSpeech.Keys.Back, MenuSpeech.Match("back", surface)?.Path);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void A_level_that_wants_a_coordinate_takes_the_grid_or_here(string level)
    {
        var menu = Walk(level);
        if (!menu.WantsCoordinate)
        {
            return;
        }

        var surface = MenuSpeech.SurfaceOf(menu);

        // One screen, two answers, everywhere. Never a page that takes only one of them.
        Assert.Equal(MenuSpeech.Keys.Here, MenuSpeech.Match("here", surface)?.Path);
        for (var digit = 0; digit <= 9; digit++)
        {
            Assert.Equal(
                MenuSpeech.Keys.DigitPrefix + digit,
                MenuSpeech.Match(Numbers[digit], surface)?.Path);
        }
    }

    /// <summary>Levels that draw a gap on purpose, and why. Everything else numbers 1 upward.</summary>
    private static readonly Dictionary<string, string> GapsOnPurpose = new(StringComparer.Ordinal)
    {
        ["More"] =
            "7 stays empty where RESTART was. A digit learned once stays learned, so LINK ACCOUNT "
            + "keeps the 8 it has always had rather than sliding up into a freed slot.",
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void The_digits_a_level_draws_run_1_upward_with_no_holes(string level)
    {
        // A hole is a key nobody can guess and a number the eye has to hunt for. It also breaks the
        // promise that the number beside a line is the thing you say to press it. This caught a
        // page numbered 1, 2, 3, 5, 7 from an entries.Count read inside a lazy projection.
        var drawn = Walk(level).Options.Where(o => !o.IsInfo && o.Digit > 0).Select(o => o.Digit).ToList();
        if (drawn.Count == 0)
        {
            return;
        }

        Assert.Equal(drawn, drawn.Distinct());
        Assert.Equal(drawn, drawn.Order());

        if (GapsOnPurpose.TryGetValue(level, out var why))
        {
            Assert.True(why.Length > 40, why);
            return;
        }

        Assert.Equal(Enumerable.Range(1, drawn.Count), drawn);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void No_open_level_is_mute(string level)
    {
        var menu = Walk(level);
        Assert.True(menu.IsOpen, $"{level}: the fixture did not reach an open level");

        // Even a page of nothing but text can be left, so every level has at least one word.
        Assert.NotEmpty(MenuSpeech.Vocabulary(MenuSpeech.SurfaceOf(menu)));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void The_walk_actually_arrives_where_it_says_it_does(string level)
    {
        // Without this the whole file is vacuous: a fixture that quietly stops at Root would test
        // Root fourteen times and pass, which is exactly how a coverage claim goes bad.
        var expected = level switch
        {
            "Coordinate" or "RangeEnd" => MenuLevel.Coordinate,
            "BoardPaged" => MenuLevel.Board,
            _ => Enum.Parse<MenuLevel>(level),
        };

        Assert.Equal(expected, Walk(level).Level);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void Every_speakable_line_has_something_to_route_it(string level)
    {
        // Matchable is not the same as actionable. App routes a line by its digit, or by one of the
        // key paths when it is a key rather than a row. A speakable line that is neither would be
        // heard, logged as a hit, and do nothing, which is the worst of the three outcomes.
        string[] keyPaths =
        [
            MenuSpeech.Keys.Back,
            MenuSpeech.Keys.Here,
            MenuSpeech.Keys.Tools,
            MenuSpeech.Keys.Up,
            MenuSpeech.Keys.Down,
            MenuSpeech.Keys.Select,
        ];

        foreach (var line in MenuSpeech.SurfaceOf(Walk(level)).Where(l => !l.IsInfo))
        {
            var routable = line.Digit >= 0
                || keyPaths.Contains(line.Path, StringComparer.Ordinal)
                || line.Path.StartsWith(MenuSpeech.Keys.DigitPrefix, StringComparison.Ordinal);

            Assert.True(routable, $"{level}: '{line.Label}' can be said and nothing would happen");
        }
    }

    [Fact]
    public void Every_level_the_menu_has_is_walked()
    {
        // A new MenuLevel is a new place voice can be, so it has to be named here or this file
        // silently stops covering the app.
        var walked = AllLevels
            .Select(l => l switch
            {
                "Coordinate" or "RangeEnd" => MenuLevel.Coordinate,
                "BoardPaged" => MenuLevel.Board,
                _ => Enum.Parse<MenuLevel>(l),
            })
            .ToHashSet();

        var missing = Enum.GetValues<MenuLevel>()
            .Where(l => l != MenuLevel.Closed && !walked.Contains(l))
            .ToList();

        Assert.True(missing.Count == 0, "not walked: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void Every_row_the_level_draws_answers_to_its_own_label(string level)
    {
        var menu = Walk(level);
        var surface = MenuSpeech.SurfaceOf(menu);

        foreach (var row in menu.Options.Where(o => !o.IsInfo))
        {
            var said = MenuSpeech.Match(row.Label, surface);
            Assert.True(
                said is not null && said.Path == row.Path,
                $"{level}: the row '{row.Label}' is drawn but saying it reaches "
                + (said is null ? "nothing" : $"'{said.Label}'"));
        }
    }

    /// <summary>Drives the menu to one level with the keys, never by reaching into its state.</summary>
    private static MenuStateMachine Walk(string level)
    {
        var menu = new MenuStateMachine(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);
        var context = Context;

        switch (level)
        {
            case "Root":
                menu.Open(T0, null, context);
                break;

            case "Board":
                menu.OpenOnBoard(T0, context);
                break;

            case "BoardPaged":
                menu.OpenOnBoard(T0, FullPage);
                break;

            case "Branch":
                menu.Open(T0, null, context);
                menu.Digit(1, T0);
                break;

            case "Coordinate":
                menu.Open(T0, null, context);
                menu.Digit(ContractFixtures.Catalog.MenuCategories["attack"], T0);
                menu.Digit(1, T0);
                break;

            case "Confirm":
                menu.Open(T0, null, context);
                menu.Digit(ContractFixtures.Catalog.MenuCategories["attack"], T0);
                menu.Digit(1, T0);
                Fill(menu);
                break;

            case "BoardAction":
                menu.OpenOnBoard(T0, context);
                menu.Select(T0);
                break;

            case "More":
                menu.OpenTools(T0, context);
                break;

            case "RangeTool":
                menu.OpenTools(T0, context);
                menu.OpenPanel("range", T0, context);
                break;

            case "RangeEnd":
                menu.OpenTools(T0, context);
                menu.OpenPanel("range", T0, context);
                menu.Digit(menu.Options.First(o => o.Path == "range.origin").Digit, T0);
                break;

            default:
                menu.OpenTools(T0, context);
                menu.OpenPanel(level.ToLowerInvariant(), T0, context);
                break;
        }

        return menu;
    }

    /// <summary>Types whatever this level is asking for, so the walk lands past it.</summary>
    private static void Fill(MenuStateMachine menu)
    {
        var wanted = menu.DigitsWanted;
        for (var i = 0; i < wanted; i++)
        {
            menu.Digit(1, T0);
        }
    }

    /// <summary>Everything a key press could visibly change. Compared, never asserted on.</summary>
    private static string Signature(MenuStateMachine menu) => string.Join(
        "|",
        menu.Level,
        menu.Highlight,
        menu.DigitsTyped,
        string.Join(",", menu.Options.Select(o => o.Label)),
        menu.ToolGun,
        menu.ToolTarget,
        string.Join(",", menu.Modifiers));
}
