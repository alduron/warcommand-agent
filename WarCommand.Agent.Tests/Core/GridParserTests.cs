using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The named risk is a digit confidently misheard in a spoken grid, which is the default path at
/// M1 and produces a well formed wrong grid that every downstream check accepts.
/// </summary>
public class GridParserTests
{
    private static IReadOnlyList<RecognizedToken> Words(string text, double confidence = 0.9) =>
        Utterance.FromWords(text, confidence).Tokens;

    [Fact]
    public void Digit_by_digit_is_tried_first()
    {
        var grid = GridParser.TryParse(Words("eight five point five three six nine point four two"), 0)!;

        Assert.Equal(85.53m, grid.X);
        Assert.Equal(69.42m, grid.Y);
        Assert.Equal(10, grid.TokensConsumed);
    }

    [Fact]
    public void The_cardinal_reading_is_accepted_too()
    {
        var grid = GridParser.TryParse(Words("eighty five point five three sixty nine point four two"), 0)!;

        Assert.Equal(85.53m, grid.X);
        Assert.Equal(69.42m, grid.Y);
    }

    [Fact]
    public void A_bare_tens_word_reads_as_itself()
    {
        var axis = GridParser.TryParseAxis(Words("fifty point zero zero"), 0)!;

        Assert.Equal(50.00m, axis.Value);
    }

    [Fact]
    public void A_written_numeral_axis_is_accepted()
    {
        var axis = GridParser.TryParseAxis(Words("85 point five three"), 0)!;

        Assert.Equal(85.53m, axis.Value);
    }

    [Fact]
    public void Half_a_grid_is_not_a_grid()
    {
        Assert.Null(GridParser.TryParse(Words("eight five point five three"), 0));
        Assert.Null(GridParser.TryParse(Words("mortar urgent"), 0));
    }

    [Fact]
    public void An_axis_needs_exactly_two_decimals()
    {
        Assert.Null(GridParser.TryParseAxis(Words("eight five point five"), 0));
    }

    [Fact]
    public void The_grid_confidence_is_the_minimum_over_the_digits_not_the_sentence()
    {
        // 'eight five point five three' heard as 'three five ...' is the failure that happens: one
        // weak digit must be able to fail the point on its own.
        var tokens = new List<RecognizedToken>
        {
            new("eight", 0.31),
            new("five", 0.98),
            new("point", 0.99),
            new("five", 0.97),
            new("three", 0.96),
            new("six", 0.95),
            new("nine", 0.94),
            new("point", 0.99),
            new("four", 0.93),
            new("two", 0.92),
        };

        var grid = GridParser.TryParse(tokens, 0)!;

        Assert.Equal(0.31m, grid.MinDigitConfidence);
    }

    [Fact]
    public void The_utterance_exposes_the_same_minimum_over_its_digits()
    {
        var utterance = new Utterance
        {
            Confidence = 0.95,
            Tokens =
            [
                new RecognizedToken("mortar", 0.99),
                new RecognizedToken("at", 0.99),
                new RecognizedToken("eight", 0.44),
                new RecognizedToken("five", 0.98),
            ],
        };

        Assert.Equal(0.44m, utterance.MinDigitConfidence);
    }

    [Fact]
    public void Join_takes_exactly_six_digits_and_both_readings_of_zero()
    {
        var tokens = Words("nine two oh five eight zero");

        var code = GridParser.TryParseDigits(tokens, 0, 6, out var confidence);

        Assert.Equal("920580", code);
        Assert.Equal(0.9m, confidence);
    }

    [Fact]
    public void Fewer_digits_is_never_a_code()
    {
        Assert.Null(GridParser.TryParseDigits(Words("nine two one five eight"), 0, 6, out _));
    }

    [Fact]
    public void A_non_digit_inside_the_run_refuses_the_whole_code()
    {
        Assert.Null(GridParser.TryParseDigits(Words("nine two mortar five eight five"), 0, 6, out _));
    }
}
