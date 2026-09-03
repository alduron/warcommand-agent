using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// One vocabulary per position class. A flat vocabulary would put 'left' back in contact with
/// 'lift' and the collision test would not see it.
/// </summary>
public class GrammarTests
{
    private static Grammar Everything() => Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

    private static IReadOnlyList<RecognizedToken> Words(string text) => Utterance.FromWords(text, 0.9).Tokens;

    [Fact]
    public void The_adjust_directions_are_never_initial_class_tokens()
    {
        var grammar = Everything();

        foreach (var direction in new[] { "left", "right", "over", "short" })
        {
            Assert.False(grammar.Contains(PositionClass.Initial, direction), $"'{direction}' reached the initial class");
            Assert.True(grammar.Contains(PositionClass.AdjustDirection, direction));
        }

        // The incumbents keep the whole first position.
        Assert.True(grammar.Contains(PositionClass.Initial, "lift"));
        Assert.True(grammar.Contains(PositionClass.Initial, "ride"));
    }

    [Fact]
    public void Wall_and_all_are_perfect_homophones_in_different_classes()
    {
        var grammar = Everything();

        Assert.True(grammar.Contains(PositionClass.Initial, "wall"));
        Assert.True(grammar.Contains(PositionClass.Slot, "all"));
        Assert.False(grammar.Contains(PositionClass.Initial, "all"));
    }

    [Fact]
    public void Zero_is_legal_in_the_digit_class_and_nowhere_else()
    {
        var grammar = Everything();

        Assert.True(grammar.Contains(PositionClass.Digit, "zero"));
        Assert.True(grammar.Contains(PositionClass.Digit, "oh"));
        Assert.False(grammar.Contains(PositionClass.Slot, "zero"));
        Assert.False(grammar.Contains(PositionClass.Slot, "oh"));
    }

    [Fact]
    public void No_forbidden_alias_ever_resolves_to_a_request_type()
    {
        var grammar = Everything();

        foreach (var forbidden in ContractFixtures.Catalog.ForbiddenAliases.Keys)
        {
            var match = grammar.LongestMatch(PositionClass.Initial, Words(forbidden), 0);

            // forbidden_aliases is scoped: 'fuel' is banned as a TYPE alias and is still the supply
            // kind shortcut, and 'left' is banned in the initial class and lives in adjust_direction.
            Assert.False(
                match?.Token.Kind == GrammarTokenKind.RequestType,
                $"forbidden alias '{forbidden}' resolves to request type '{match?.Token.Id}'");
        }
    }

    [Fact]
    public void A_forbidden_alias_scoped_to_the_initial_class_is_absent_from_it()
    {
        var grammar = Everything();

        foreach (var forbidden in new[] { "armor", "air support", "air transport", "a t", "support", "rover", "left", "right" })
        {
            Assert.False(
                grammar.Contains(PositionClass.Initial, forbidden),
                $"forbidden alias '{forbidden}' reached the initial class");
        }
    }

    [Fact]
    public void Longest_match_wins_deterministically()
    {
        var grammar = Everything();

        Assert.Equal("clear", grammar.LongestMatch(PositionClass.Initial, Words("clear"), 0)!.Token.Id);
        Assert.Equal("clear_building", grammar.LongestMatch(PositionClass.Initial, Words("clear building"), 0)!.Token.Id);
        Assert.Equal("uav_recon", grammar.LongestMatch(PositionClass.Initial, Words("drone"), 0)!.Token.Id);
        Assert.Equal("uav_strike", grammar.LongestMatch(PositionClass.Initial, Words("drone strike"), 0)!.Token.Id);
    }

    [Fact]
    public void A_type_alias_outranks_a_kind_shortcut_of_the_same_phrase()
    {
        var grammar = Everything();

        var spawn = grammar.LongestMatch(PositionClass.Initial, Words("spawn"), 0)!.Token;
        var spawnPoint = grammar.LongestMatch(PositionClass.Initial, Words("spawn point"), 0)!.Token;

        Assert.Equal(GrammarTokenKind.KindShortcut, spawn.Kind);
        Assert.Equal("fortify", spawn.Id);
        Assert.Equal("spawn_point", spawnPoint.Id);
    }

    [Fact]
    public void Types_whose_target_roles_are_not_enabled_are_absent()
    {
        var grammar = Grammar.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar"] });

        // A group with no anti_air cannot mishear anything as 'anti air'.
        Assert.True(grammar.Contains(PositionClass.Initial, "mortar"));
        Assert.False(grammar.Contains(PositionClass.Initial, "anti air"));
        Assert.False(grammar.Contains(PositionClass.Initial, "medic"));
    }

    [Fact]
    public void An_empty_board_loads_no_verb_that_names_a_row()
    {
        var grammar = Grammar.Compile(ContractFixtures.Catalog, new GrammarContext());

        Assert.False(grammar.Contains(PositionClass.Initial, "accept"));
        Assert.False(grammar.Contains(PositionClass.Initial, "done"));
        Assert.False(grammar.Contains(PositionClass.Initial, "splash"));
    }

    [Fact]
    public void Join_and_help_survive_an_empty_board_because_that_is_the_cold_start()
    {
        var grammar = Grammar.Compile(ContractFixtures.Catalog, new GrammarContext());

        Assert.True(grammar.Contains(PositionClass.Initial, "join"));
        Assert.True(grammar.Contains(PositionClass.Initial, "help"));
        Assert.True(grammar.Contains(PositionClass.Initial, "role"));
    }

    [Fact]
    public void No_claimed_rows_means_done_and_release_are_not_legal()
    {
        var grammar = Grammar.Compile(ContractFixtures.Catalog, new GrammarContext { HasAnyRows = true });

        Assert.True(grammar.Contains(PositionClass.Initial, "accept"));
        Assert.False(grammar.Contains(PositionClass.Initial, "done"));
        Assert.False(grammar.Contains(PositionClass.Initial, "release"));

        // "working" used to belong to a separate start verb. Claiming a request starts it, so it
        // is an accept alias now and is legal wherever accept is.
        Assert.True(grammar.Contains(PositionClass.Initial, "working"));
    }

    [Fact]
    public void Rounds_away_is_legal_only_on_a_row_the_speaker_started()
    {
        var claimedOnly = Grammar.Compile(
            ContractFixtures.Catalog,
            new GrammarContext { HasAnyRows = true, HasClaimedRows = true });
        var started = Grammar.Compile(
            ContractFixtures.Catalog,
            new GrammarContext { HasAnyRows = true, HasClaimedRows = true, HasStartedRows = true });

        Assert.False(claimedOnly.Contains(PositionClass.Initial, "splash"));
        Assert.True(started.Contains(PositionClass.Initial, "splash"));
    }

    [Fact]
    public void Adjust_needs_a_row_the_speaker_requested_or_spots_for()
    {
        var without = Grammar.Compile(ContractFixtures.Catalog, new GrammarContext { HasAnyRows = true });
        var with = Grammar.Compile(
            ContractFixtures.Catalog,
            new GrammarContext { HasAnyRows = true, HasAdjustableRows = true });

        Assert.False(without.Contains(PositionClass.Initial, "correction"));
        Assert.True(with.Contains(PositionClass.Initial, "correction"));
        Assert.True(with.Contains(PositionClass.AdjustDirection, "over"));
    }

    [Fact]
    public void Board_state_drives_the_pruning_context()
    {
        var board = new BoardState(Rows.Viewer, ContractFixtures.Rules);
        Assert.False(GrammarContext.FromBoard(board, []).HasAnyRows);

        board.Upsert(Rows.A(), Rows.Epoch);
        var open = GrammarContext.FromBoard(board, ["mortar"]);
        Assert.True(open.HasAnyRows);
        Assert.False(open.HasClaimedRows);

        var mine = Rows.A(state: RequestState.InProgress, claimant: Rows.Viewer);
        board.Upsert(mine, Rows.Epoch);
        var held = GrammarContext.FromBoard(board, ["mortar"]);
        Assert.True(held.HasClaimedRows);
        Assert.True(held.HasStartedRows);
    }

    [Fact]
    public void Every_word_the_recognizer_loads_comes_from_the_catalog()
    {
        var grammar = Everything();

        Assert.NotEmpty(grammar.AllWords);
        Assert.Contains("mortar", grammar.AllWords);
        Assert.DoesNotContain("armour", grammar.AllWords);
    }

    [Fact]
    public void Ambiguous_aliases_are_loaded_and_flagged()
    {
        var grammar = Everything();

        var flank = grammar.LongestMatch(PositionClass.Initial, Words("flank"), 0)!.Token;

        Assert.Equal("flank", flank.Id);
        Assert.True(flank.Ambiguous);
    }

    [Fact]
    public void The_near_floor_pair_list_is_optional_and_degrades_to_empty()
    {
        Assert.True(NearFloorPairs.FromJson(null).IsEmpty);
        Assert.True(NearFloorPairs.FromJson("not json").IsEmpty);
        Assert.True(NearFloorPairs.FromJson("{}").IsEmpty);
        Assert.True(NearFloorPairs.FromJson("""{"pairs":[]}""").IsEmpty);

        // The old two-string and bare-array shapes were guesses at a file that did not exist yet.
        // They are not the generated shape and must not silently half-load.
        Assert.True(NearFloorPairs.FromJson("""[["escort","extract"]]""").IsEmpty);
    }

    [Fact]
    public void The_near_floor_pair_list_reads_the_generated_shape()
    {
        var pairs = NearFloorPairs.FromJson(
            """
            {"pairs":[{"position_class":"initial","reason":"forced_menu","score":0.8,
              "cleared_by":"nothing","segment_distance":0.4,"differing_features":["onset"],
              "a":{"alias":"flank","owner":"type:flank","ambiguous":true},
              "b":{"alias":"tank","owner":"type:armor_support","ambiguous":false},
              "phonemes":{"a":"F L AE1 NG K","b":"T AE1 NG K"}}]}
            """);

        Assert.True(pairs.IsPair("flank", "tank"));
        Assert.True(pairs.IsPair("tank", "flank"));
        Assert.Equal(["tank"], pairs.PartnersOf("flank"));

        var pair = pairs.PairFor("flank", "tank")!;
        Assert.Equal("initial", pair.PositionClass);
        Assert.Equal("forced_menu", pair.Reason);
        Assert.True(pair.A.Ambiguous);
        Assert.Equal("type:armor_support", pair.B.Owner);
    }

    [Fact]
    public void Partners_are_ordered_closest_first()
    {
        var pairs = NearFloorPairs.FromJson(
            """
            {"pairs":[
              {"score":1.25,"segment_distance":0.625,"a":{"alias":"escort"},"b":{"alias":"extract"}},
              {"score":1.1111,"segment_distance":0.5556,"a":{"alias":"escort"},"b":{"alias":"transport"}}]}
            """);

        Assert.Equal(["transport", "extract"], pairs.PartnersOf("escort"));
    }
}
