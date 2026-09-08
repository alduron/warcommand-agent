using System.Globalization;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The parse spec is warcommand-api/tests/unit/fixtures/utterances.yaml, read here row by row. It is
/// never transcribed into this file: two copies diverge on the first edit and the Python suite does
/// not notice. Everything below the theory is a case the fixture cannot express, because it needs
/// per-token confidences or a hand-built alternatives list.
/// </summary>
public class IntentParserTests
{
    private static readonly Grammar Loaded =
        Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

    public static TheoryData<string> FixtureIds => UtteranceFixture.Ids();

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Every_row_of_the_shared_utterance_fixture_parses_as_specified(string id)
    {
        var row = UtteranceFixture.Case(id);
        var expect = row.Expect;
        var parsed = Parse(row.Said);

        switch (expect.Mode)
        {
            case "request":
                AssertRequest(expect, Assert.IsType<ParsedRequest>(parsed));
                break;

            case "command":
                AssertCommand(expect, Assert.IsType<ParsedCommand>(parsed));
                break;

            case "menu":
                var menu = Assert.IsType<ParsedDisambiguation>(parsed);
                Assert.Equal(
                    expect.Options.Select(o => o.Type).ToList(),
                    menu.Options.Select(o => o.TypeId).ToList());
                break;

            case "reject":
                Assert.Equal(expect.Reason, Assert.IsType<ParsedRejection>(parsed).Reason);
                break;

            default:
                throw new InvalidOperationException($"{id}: unknown expect.mode '{expect.Mode}'");
        }
    }

    [Fact]
    public void The_fixture_is_the_whole_spec_and_every_row_of_it_ran()
    {
        // A theory that silently loaded nothing passes. This is what makes the count load-bearing.
        Assert.True(UtteranceFixture.Cases.Count >= 55, $"only {UtteranceFixture.Cases.Count} rows loaded");
        Assert.Equal(
            UtteranceFixture.Cases.Count,
            UtteranceFixture.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_generated_pair_list_names_both_owners_on_a_near_floor_menu()
    {
        // The whole point of the pair list. Degraded to Empty this shows one option to confirm,
        // which is a different and much weaker feature.
        var pairs = NearFloorPairs.FromJson(ContractFixtures.NearFloorPairsJson);
        Assert.False(pairs.IsEmpty, "contracts/generated/near-floor-pairs.json did not load");
        // Above the floor, so the pair itself is only near it. The MENU is forced by the
        // alias being ambiguous, which is a separate and stronger guarantee.
        Assert.Equal("near_floor", pairs.PairFor("escort", "transport")!.Reason);

        var menu = Assert.IsType<ParsedDisambiguation>(
            new IntentParser(Loaded, pairs).Parse(Utterance.FromWords("transport", 0.95)));

        Assert.Equal("transport", menu.Alias);
        Assert.Equal(["move_transport", "vehicle_defense"], menu.Options.Select(o => o.TypeId));
    }

    [Fact]
    public void Without_the_pair_list_the_menu_carries_one_option_to_confirm()
    {
        var menu = Assert.IsType<ParsedDisambiguation>(
            new IntentParser(Loaded, NearFloorPairs.Empty).Parse(Utterance.FromWords("transport", 0.95)));

        Assert.Equal(["move_transport"], menu.Options.Select(o => o.TypeId));
    }

    [Fact]
    public void A_command_verb_must_be_the_first_token_so_a_digit_is_never_a_verb()
    {
        var parsed = Assert.IsType<ParsedCommand>(Parse("done 4"));

        Assert.Equal(4, parsed.Slot);
    }

    [Fact]
    public void The_spoken_grid_confidence_is_the_worst_digit_not_the_utterance()
    {
        var tokens = new List<RecognizedToken> { new("mortar", 0.99), new("at", 0.99) };
        foreach (var (word, score) in new[]
                 {
                     ("eight", 0.42), ("five", 0.98), ("point", 0.99), ("five", 0.97), ("three", 0.97),
                     ("six", 0.96), ("nine", 0.95), ("point", 0.99), ("four", 0.94), ("two", 0.93),
                 })
        {
            tokens.Add(new RecognizedToken(word, score));
        }

        var parsed = Assert.IsType<ParsedRequest>(Parser().Parse(new Utterance { Tokens = tokens, Confidence = 0.96 }));

        Assert.Equal(0.42m, parsed.MinDigitConfidence);
    }

    [Fact]
    public void An_empty_transcript_makes_no_overlay_noise()
    {
        var rejected = Assert.IsType<ParsedRejection>(
            Parser().Parse(new Utterance { Tokens = [], Confidence = 0.0 }));

        Assert.Equal(ParseReasons.EmptyTranscript, rejected.Reason);
    }

    [Fact]
    public void Below_min_intent_confidence_the_transcript_gets_a_question_mark_and_nothing_is_sent()
    {
        var floor = ContractFixtures.Rules.MinIntentConfidence;

        var unrecognized = Assert.IsType<ParsedUnrecognized>(Parse("mortar", floor - 0.01));

        Assert.Equal("mortar", unrecognized.Transcript);
    }

    [Fact]
    public void A_near_tie_between_known_confusables_is_a_menu_and_not_a_guess()
    {
        var utterance = new Utterance
        {
            Tokens = [new RecognizedToken("escort", 0.71)],
            Confidence = 0.71,
            Alternatives = [Utterance.FromWords("transport", 0.62)],
        };

        var menu = Assert.IsType<ParsedDisambiguation>(Parser(Confusables("escort", "transport")).Parse(utterance));

        Assert.Equal(["vehicle_defense", "move_transport"], menu.Options.Select(o => o.TypeId));
    }

    [Fact]
    public void A_hypothesis_outside_the_margin_is_not_a_tie()
    {
        var utterance = new Utterance
        {
            Tokens = [new RecognizedToken("escort", 0.95)],
            Confidence = 0.95,
            Alternatives = [Utterance.FromWords("transport", 0.40)],
        };

        var parsed = Assert.IsType<ParsedRequest>(Parser(Confusables("escort", "transport")).Parse(utterance));

        Assert.Equal("vehicle_defense", parsed.TypeId);
    }

    [Fact]
    public void A_near_tie_between_words_that_are_not_confusable_is_not_a_menu()
    {
        var utterance = new Utterance
        {
            Tokens = [new RecognizedToken("medic", 0.80)],
            Confidence = 0.80,
            Alternatives = [Utterance.FromWords("mortar", 0.75)],
        };

        Assert.IsType<ParsedRequest>(Parser(NearFloorPairs.Empty).Parse(utterance));
    }

    [Fact]
    public void An_unknown_modifier_is_dropped_and_the_request_survives()
    {
        var parsed = Assert.IsType<ParsedRequest>(Parse("mortar willy pete"));

        Assert.Equal("attack_position", parsed.TypeId);
        Assert.Equal(["willy_pete"], parsed.Modifiers);
    }

    private static IntentParser Parser(NearFloorPairs? pairs = null) =>
        new(Loaded, pairs ?? NearFloorPairs.FromJson(ContractFixtures.NearFloorPairsJson));

    private static ParseResult Parse(string said, double confidence = 0.95) =>
        Parser().Parse(Utterance.FromWords(said, confidence));

    /// <summary>A one-pair list in the generated shape, for a case the real file does not carry.</summary>
    private static NearFloorPairs Confusables(string first, string second) =>
        NearFloorPairs.FromJson(
            "{\"pairs\":[{\"position_class\":\"initial\",\"reason\":\"near_floor\",\"score\":1.2,"
            + "\"cleared_by\":\"features\",\"segment_distance\":0.6,\"differing_features\":[\"onset\"],"
            + "\"a\":{\"alias\":\"" + first + "\",\"ambiguous\":false},"
            + "\"b\":{\"alias\":\"" + second + "\",\"ambiguous\":false}}]}");

    private static void AssertRequest(UtteranceExpectation expect, ParsedRequest parsed)
    {
        Assert.Equal(expect.Type, parsed.TypeId);
        Assert.Equal(ToPriority(expect.Priority), parsed.Priority);
        Assert.Equal(expect.Modifiers, parsed.Modifiers.ToList());
        Assert.Equal(expect.SupplyKind, parsed.SupplyKindId);
        Assert.Equal(expect.StructureKind, parsed.StructureKindId);
        Assert.Equal(expect.Quantity?.Unit, parsed.QuantityUnit);
        Assert.Equal(expect.Quantity?.Value, parsed.Quantity);
        Assert.Equal(expect.AwaitingPoint?.Index, parsed.AwaitingPointIndex);
        Assert.Equal(expect.AwaitingPoint?.Label, parsed.AwaitingPointLabel);

        foreach (var point in expect.Points)
        {
            Assert.True(point.Index < parsed.PointLabels.Count, $"no point ordinal {point.Index}");
            Assert.Equal(point.Label, parsed.PointLabels[point.Index]);
        }

        var first = expect.Points.Find(p => p.Index == 0);
        if (first is null)
        {
            return;
        }

        switch (first.Source)
        {
            case "cursor":
                Assert.Equal(PointSource.Cursor, parsed.PointSource);
                Assert.Null(parsed.SpokenPoint);
                break;

            case "spoken_grid":
                Assert.Equal(PointSource.SpokenGrid, parsed.PointSource);
                Assert.Equal("spoken_grid", parsed.SpokenPoint!.Source);
                Assert.Equal(first.X, parsed.SpokenPoint.X);
                Assert.Equal(first.Y, parsed.SpokenPoint.Y);
                Assert.Equal(parsed.MinDigitConfidence, parsed.SpokenPoint.Confidence);
                break;

            default:
                throw new InvalidOperationException($"unsupported point source '{first.Source}'");
        }
    }

    private static void AssertCommand(UtteranceExpectation expect, ParsedCommand parsed)
    {
        Assert.Equal(expect.Verb, parsed.VerbId);
        Assert.Equal(expect.SlotRef, parsed.SlotRef);
        Assert.Equal(expect.Role, parsed.RoleId);
        Assert.Equal(expect.InviteCode, parsed.InviteCode);
        Assert.Equal(expect.Action, parsed.Action);
        Assert.Equal(expect.Prompt, parsed.Prompt);
    }

    private static Priority ToPriority(string? name) => name switch
    {
        "low" => Priority.Low,
        "normal" => Priority.Normal,
        "urgent" => Priority.Urgent,
        _ => throw new InvalidOperationException($"unknown priority '{name}'"),
    };
}
