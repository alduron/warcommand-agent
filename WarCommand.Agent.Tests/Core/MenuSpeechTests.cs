using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Voice selects the option a key would select, so the vocabulary is the drawn surface and nothing
/// else. These assert that identity rather than a hand-written word list, which is the failure this
/// replaced: a catalog alias set and a menu that had drifted apart with nothing checking.
/// </summary>
public class MenuSpeechTests
{
    private static MenuEntry Line(int digit, string label, bool info = false) => new()
    {
        Digit = digit,
        Path = label.ToLowerInvariant().Replace(' ', '.'),
        Label = label,
        IsInfo = info,
    };

    private static readonly IReadOnlyList<MenuEntry> Supply =
    [
        Line(1, "BUILDING"),
        Line(2, "AMMO"),
        Line(3, "FUEL"),
        Line(4, "MECH"),
        Line(5, "HAMMERS"),
        Line(6, "FOB KIT"),
    ];

    [Fact]
    public void Vocabulary_is_the_drawn_labels_and_the_numbers_beside_them()
    {
        // Both, because the overlay draws both. The numerals themselves are absent: '1' is outside
        // the Vosk small model's lexicon and only the word form ever decodes.
        Assert.Equal(
            ["ammo", "building", "five", "fob kit", "four", "fuel", "hammers", "mech", "one", "six", "three", "two"],
            MenuSpeech.Vocabulary(Supply));
    }

    [Theory]
    [InlineData("one", "BUILDING")]
    [InlineData("two", "AMMO")]
    [InlineData("six", "FOB KIT")]
    [InlineData("6", "FOB KIT")]
    public void A_number_presses_the_line_it_is_drawn_beside(string heard, string label)
    {
        var match = MenuSpeech.Match(heard, Supply);

        Assert.NotNull(match);
        Assert.Equal(label, match.Label);
    }

    [Fact]
    public void A_number_no_line_carries_presses_nothing()
    {
        Assert.Null(MenuSpeech.Match("nine", Supply));
    }

    [Fact]
    public void A_number_is_never_reached_through_a_near_miss()
    {
        // 'four' and 'five' are one edit apart and both name a line here. Resolving a number by
        // distance would make every mishearing press the neighbouring row.
        Assert.Equal("MECH", MenuSpeech.Match("four", Supply)?.Label);
        Assert.Equal("HAMMERS", MenuSpeech.Match("five", Supply)?.Label);
    }

    [Fact]
    public void An_info_line_is_not_speakable_because_no_key_presses_it()
    {
        IReadOnlyList<MenuEntry> page = [Line(1, "HELP"), Line(-1, "HOLD CAPSLOCK WHILE YOU WORK", info: true)];

        // 'one' is HELP's own number and belongs there. The info line contributes nothing at all.
        Assert.Equal(["help", "one"], MenuSpeech.Vocabulary(page));
        Assert.Null(MenuSpeech.Match("hold capslock while you work", page));
    }

    [Theory]
    [InlineData("ammo", 2)]
    [InlineData("AMMO", 2)]
    [InlineData("fob kit", 6)]
    [InlineData("fob  kit", 6)]
    public void A_label_resolves_to_its_own_digit(string heard, int digit)
    {
        var match = MenuSpeech.Match(heard, Supply);

        Assert.NotNull(match);
        Assert.Equal(digit, match.Digit);
    }

    [Fact]
    public void A_near_miss_resolves_to_the_line_on_screen()
    {
        // The whole point. 'armor' is forbidden as a catalog alias because it collides with
        // 'mortar' across the whole vocabulary. Against six drawn lines it is a misheard AMMO.
        var match = MenuSpeech.Match("armor", Supply);

        Assert.NotNull(match);
        Assert.Equal("AMMO", match.Label);
    }

    [Fact]
    public void A_word_on_no_line_resolves_to_nothing()
    {
        Assert.Null(MenuSpeech.Match("helicopter", Supply));
        Assert.Null(MenuSpeech.Match(string.Empty, Supply));
        Assert.Null(MenuSpeech.Match(null, Supply));
    }

    [Fact]
    public void Two_lines_equally_close_resolve_to_nothing_rather_than_the_first()
    {
        IReadOnlyList<MenuEntry> page = [Line(1, "MARK"), Line(2, "PARK")];

        Assert.Null(MenuSpeech.Match("bark", page));
    }

    [Fact]
    public void A_key_that_is_live_on_a_surface_is_speakable_beside_its_rows()
    {
        // BACK is a key, not a row, so a vocabulary built from rows alone could never carry it and
        // saying it did nothing on every level. It must not swallow a row either.
        IReadOnlyList<MenuEntry> page = [.. Supply, Line(-1, "BACK")];

        Assert.Contains("back", MenuSpeech.Vocabulary(page));
        Assert.Equal("BACK", MenuSpeech.Match("back", page)?.Label);
        Assert.Equal("AMMO", MenuSpeech.Match("ammo", page)?.Label);
        Assert.Equal("FUEL", MenuSpeech.Match("three", page)?.Label);
    }

    [Fact]
    public void A_digit_entry_level_names_its_numbers_and_its_read_key()
    {
        // The coordinate level draws no rows at all, so before this it had an empty vocabulary and
        // nothing said on it could reach the recognizer: HERE did nothing and no grid could be
        // entered by voice.
        IReadOnlyList<MenuEntry> page =
        [
            Line(-1, "BACK"),
            Line(-1, "HERE"),
            .. Enumerable.Range(0, 10).Select(d => Line(d, DigitWord(d))),
        ];

        Assert.Equal("HERE", MenuSpeech.Match("here", page)?.Label);
        Assert.Equal("BACK", MenuSpeech.Match("back", page)?.Label);
        Assert.Equal(7, MenuSpeech.Match("seven", page)?.Digit);
        Assert.Equal(0, MenuSpeech.Match("zero", page)?.Digit);

        static string DigitWord(int d) =>
            new[] { "ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE" }[d];
    }

    [Fact]
    public void A_grid_said_in_one_breath_types_every_digit()
    {
        // Nobody says a grid one digit at a time with a pause between each, and the recognizer does
        // not hear it that way either: eight numbers at speed come back as ONE utterance of eight
        // words. Matched whole that reached nothing, so the coordinate page ignored everything.
        IReadOnlyList<MenuEntry> page =
        [
            .. Enumerable.Range(0, 10).Select(d => Line(d, Word(d))),
            Line(-1, "BACK"),
        ];

        var run = MenuSpeech.MatchRun("one two three four", page);

        Assert.Equal([1, 2, 3, 4], run.Select(r => r.Digit));
    }

    [Fact]
    public void A_run_lands_whole_or_not_at_all()
    {
        IReadOnlyList<MenuEntry> page =
        [
            .. Enumerable.Range(0, 10).Select(d => Line(d, Word(d))),
            Line(-1, "BACK"),
        ];

        // Half a grid is worse than none: it types into a field the speaker cannot see the end of.
        Assert.Empty(MenuSpeech.MatchRun("one two helicopter four", page));

        // A single word is not a run, so it goes down the ordinary path.
        Assert.Empty(MenuSpeech.MatchRun("one", page));
    }

    [Fact]
    public void A_two_word_label_still_beats_two_lines_named_its_parts()
    {
        // MatchRun is tried AFTER the whole-utterance match, so FOB KIT is one line and not two.
        Assert.Equal("FOB KIT", MenuSpeech.Match("fob kit", Supply)?.Label);
    }

    [Theory]
    [InlineData("one zero seven point four three", "10743")]
    [InlineData("one hundred seven point forty three", "10743")]
    [InlineData("one hundred and seven forty three", "10743")]
    [InlineData("one oh seven four three", "10743")]
    [InlineData("ten seven forty three", "10743")]
    [InlineData("one two three four", "1234")]
    [InlineData("twenty one", "21")]
    [InlineData("forty three", "43")]
    [InlineData("nineteen", "19")]
    [InlineData("helicopter", "")]
    [InlineData("one two helicopter", "")]
    public void People_read_numbers_every_way_there_is(string heard, string digits)
    {
        // The same grid, read six ways. All of them are the same digits, so all of them type it.
        Assert.Equal(digits, MenuSpeech.DigitsIn(heard));
    }

    [Fact]
    public void A_grid_read_naturally_types_the_same_digits_as_one_read_digit_by_digit()
    {
        IReadOnlyList<MenuEntry> page =
        [
            .. Enumerable.Range(0, 10).Select(d => Line(d, Word(d))),
            Line(-1, "BACK"),
        ];

        Assert.Equal(
            MenuSpeech.MatchRun("one zero seven four three", page).Select(r => r.Digit),
            MenuSpeech.MatchRun("one hundred seven point forty three", page).Select(r => r.Digit));
    }

    private static string Word(int d) =>
        new[] { "ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE" }[d];

    [Fact]
    public void Every_line_the_real_root_draws_is_speakable()
    {
        var catalog = BundledContracts.Catalog().Current;
        var machine = new MenuStateMachine(MenuTree.Compile(catalog), catalog);
        machine.Open(DateTimeOffset.UtcNow);

        Assert.NotEmpty(machine.Options);
        foreach (var option in machine.Options.Where(o => !o.IsInfo))
        {
            var match = MenuSpeech.Match(option.Label, machine.Options);

            Assert.NotNull(match);
            Assert.Equal(option.Path, match.Path);
        }
    }
}
