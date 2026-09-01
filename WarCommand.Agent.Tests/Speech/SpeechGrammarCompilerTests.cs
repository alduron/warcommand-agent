using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Speech;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// The vocabulary is not flat. One list per position class, and a token is only ever a candidate in
/// the position it belongs to.
/// </summary>
/// <remarks>
/// Compiling one flat vocabulary puts <c>left</c> back in contact with <c>lift</c> and the collision
/// test does not see it, because a floor computed over the whole alias set measures the wrong thing.
/// These tests are what stop that being done by accident.
/// </remarks>
public class SpeechGrammarCompilerTests
{
    private static readonly string[] AdjustEntryAliases = ["adjust", "correction"];

    private static CompiledSpeechGrammar Everything =>
        SpeechGrammarCompiler.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

    [Fact]
    public void One_vocabulary_is_emitted_per_position_class()
    {
        var compiled = Everything;

        foreach (var positionClass in PositionClasses.All)
        {
            var vocabulary = compiled.For(positionClass);
            Assert.Equal(positionClass, vocabulary.Class);
            Assert.NotEmpty(vocabulary.Phrases);
        }
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("over")]
    [InlineData("short")]
    public void An_adjust_direction_is_never_a_candidate_in_the_initial_position(string direction)
    {
        var compiled = Everything;

        Assert.False(
            compiled.For(PositionClass.Initial).Contains(direction),
            $"'{direction}' reached the initial class, where the phonetic floor refuses it");
        Assert.True(compiled.For(PositionClass.AdjustDirection).Contains(direction));
    }

    [Fact]
    public void The_incumbents_the_directions_would_have_collided_with_keep_the_initial_position()
    {
        var initial = Everything.For(PositionClass.Initial);

        // 'left' is one feature from 'lift' and 'right' one from 'ride'. Both incumbents keep the
        // whole first position because their rivals are in another class entirely.
        Assert.True(initial.Contains("lift"));
        Assert.True(initial.Contains("ride"));
    }

    [Fact]
    public void A_homophone_pair_in_two_classes_is_not_a_collision()
    {
        var compiled = Everything;

        // wall and all are perfect homophones and never compete: all is legal only after a verb.
        Assert.True(compiled.For(PositionClass.Initial).Contains("wall"));
        Assert.False(compiled.For(PositionClass.Initial).Contains("all"));
        Assert.True(compiled.For(PositionClass.Slot).Contains("all"));
    }

    [Fact]
    public void Types_whose_target_roles_are_not_enabled_are_absent()
    {
        var withAntiAir = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar", "anti_air"] });

        var withoutAntiAir = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar"] });

        Assert.True(withAntiAir.For(PositionClass.Initial).Contains("anti air"));
        Assert.False(
            withoutAntiAir.For(PositionClass.Initial).Contains("anti air"),
            "a group with no anti_air cannot mishear anything as 'anti air': the words are not loaded");

        Assert.True(withoutAntiAir.For(PositionClass.Initial).Contains("mortar"));
        Assert.True(withoutAntiAir.RecognizerPhrases.Count < withAntiAir.RecognizerPhrases.Count);
    }

    [Fact]
    public void Pruning_an_unenabled_role_shrinks_what_the_recognizer_loads()
    {
        var everything = Everything;
        var fourRoles = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar", "logistics", "medic", "infantry"] });

        Assert.True(
            fourRoles.AllWords.Count < everything.AllWords.Count,
            "accuracy is a function of the vocabulary actually loaded, not the one on paper");
    }

    [Fact]
    public void Verbs_are_pruned_by_board_state()
    {
        var empty = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            new GrammarContext { EnabledRoleIds = ["mortar"] });

        var claimed = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            new GrammarContext { EnabledRoleIds = ["mortar"], HasAnyRows = true, HasClaimedRows = true });

        Assert.DoesNotContain(empty.Verbs, v => string.Equals(v.Id, "done", StringComparison.Ordinal));
        Assert.Contains(claimed.Verbs, v => string.Equals(v.Id, "done", StringComparison.Ordinal));

        // join survives an empty board: it is the cold-start path for somebody with nothing.
        Assert.Contains(empty.Verbs, v => string.Equals(v.Id, "join", StringComparison.Ordinal));
    }

    [Fact]
    public void A_kind_is_reachable_only_through_a_type_that_takes_one()
    {
        var withLogistics = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["logistics"] });

        var withoutLogistics = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar"] });

        // 'ammo' is a kind, and the shortcut that lets it be said alone belongs to resupply. With
        // no logistics role there is no owner, so the shortcut is not in the initial class at all.
        Assert.True(withLogistics.For(PositionClass.Initial).Contains("ammo"));
        Assert.False(withoutLogistics.For(PositionClass.Initial).Contains("ammo"));

        // The kind class itself is the position the word is legal at, in both compilations.
        Assert.True(withLogistics.For(PositionClass.Kind).Contains("ammo"));
        Assert.True(withoutLogistics.For(PositionClass.Kind).Contains("ammo"));
    }

    [Fact]
    public void The_five_verb_fields_the_compiler_reads_are_carried_through()
    {
        var adjust = Assert.Single(
            Everything.Verbs.Where(v => string.Equals(v.Id, "adjust", StringComparison.Ordinal)));

        Assert.Equal(PositionClass.AdjustDirection, adjust.AliasClass);
        Assert.Equal(AdjustEntryAliases, adjust.EntryAliases);
        Assert.Contains("left", adjust.Aliases);
        Assert.True(adjust.TakesMetres);
        Assert.False(adjust.Terminal);

        var done = Assert.Single(
            Everything.Verbs.Where(v => string.Equals(v.Id, "done", StringComparison.Ordinal)));

        Assert.True(done.Terminal);
        Assert.True(done.TakesQuantity);
        Assert.Equal(PositionClass.Initial, done.AliasClass);
        Assert.Empty(done.EntryAliases);
    }

    [Fact]
    public void A_verb_that_takes_metres_loads_the_number_words_the_recognizer_needs()
    {
        // 'adjust 3 over fifty' cannot be heard if 'fifty' was never handed to the decoder.
        Assert.Contains("fifty", Everything.RecognizerPhrases);
    }

    [Fact]
    public void The_recognizer_grammar_is_the_union_and_carries_the_unknown_sink()
    {
        var compiled = Everything;
        var json = compiled.ToRecognizerGrammarJson();

        Assert.Contains(CompiledSpeechGrammar.UnknownPhrase, json, StringComparison.Ordinal);
        Assert.DoesNotContain(CompiledSpeechGrammar.UnknownPhrase, compiled.RecognizerPhrases);

        foreach (var positionClass in PositionClasses.All)
        {
            foreach (var phrase in compiled.For(positionClass).Phrases)
            {
                Assert.Contains(phrase, compiled.RecognizerPhrases);
            }
        }
    }

    [Fact]
    public void The_fingerprint_moves_only_when_the_loaded_vocabulary_does()
    {
        var first = Everything.Fingerprint;
        var again = Everything.Fingerprint;
        var pruned = SpeechGrammarCompiler.Compile(
            ContractFixtures.Catalog,
            GrammarContext.Everything with { EnabledRoleIds = ["mortar"] }).Fingerprint;

        Assert.Equal(first, again);
        Assert.NotEqual(first, pruned);
    }
}
