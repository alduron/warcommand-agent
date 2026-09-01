using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Speech;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// Risk covered: a digit is confidently misheard in a spoken grid.
/// </summary>
/// <remarks>
/// "eight five point five three" heard as "three five point five three" produces a well-formed grid
/// that every downstream check accepts, and the spoken path is the default at M1. The theory asserts
/// the digits rather than the intent, and asserts the minimum per-token confidence the agent stores
/// in <c>request_points.confidence</c>, so a run that gets the right answer with a collapsed margin
/// still fails.
/// </remarks>
public class RecognizerDigitAccuracyTests
{
    private static readonly Grammar Loaded =
        Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

    public static TheoryData<string> GridUtterances => SpeechCorpus.Ids();

    [Theory]
    [MemberData(nameof(GridUtterances))]
    public async Task Spoken_grid_digits_are_accurate(string id)
    {
        var testCase = SpeechCorpus.Case(id);

        if (testCase.Unavailable is { } reason)
        {
            // Deliberately not a failure and deliberately not a pass that means anything. The row
            // exists so the theory reports the absence by name instead of quietly having no rows.
            Assert.False(SpeechCorpus.IsMeasuring);
            Assert.NotEmpty(reason);
            return;
        }

        var engine = Assert.IsAssignableFrom<ISpeechEngine>(SpeechCorpus.Engine);
        using var buffer = SpeechCorpus.Load(testCase.Wav);

        var utterance = await engine.RecognizeAsync(buffer, Loaded, CancellationToken.None);
        var parsed = new IntentParser(Loaded).Parse(utterance);

        var request = Assert.IsType<ParsedRequest>(parsed);
        var point = Assert.IsType<WarCommand.Agent.Core.Model.MapPoint>(request.SpokenPoint);

        Assert.Equal(testCase.X, point.X);
        Assert.Equal(testCase.Y, point.Y);

        var floor = SpeechCorpus.FloorFor(testCase);
        var measured = utterance.MinDigitConfidence;
        Assert.NotNull(measured);
        Assert.True(
            measured >= floor,
            $"'{testCase.Said}' parsed to the right grid with min digit confidence {measured}, "
            + $"below the {floor} floor. A collapsed margin is the failure this test exists for.");
    }

    [Fact]
    public void The_harness_asserts_the_digits_and_not_the_intent()
    {
        // Runs with no model and no recordings, and proves the two assertions above are the right
        // ones: a confident type plus one weak digit clears the intent floor and must still be
        // caught by the digit minimum. It says nothing whatever about Vosk.
        var utterance = new Utterance
        {
            Tokens =
            [
                new RecognizedToken("mortar", 0.99),
                new RecognizedToken("at", 0.98),
                new RecognizedToken("eight", 0.99),
                new RecognizedToken("five", 0.31),
                new RecognizedToken("point", 0.97),
                new RecognizedToken("five", 0.96),
                new RecognizedToken("three", 0.95),
                new RecognizedToken("six", 0.94),
                new RecognizedToken("nine", 0.93),
                new RecognizedToken("point", 0.97),
                new RecognizedToken("four", 0.92),
                new RecognizedToken("two", 0.91),
            ],
            Confidence = 0.93,
        };

        var request = Assert.IsType<ParsedRequest>(new IntentParser(Loaded).Parse(utterance));
        var point = Assert.IsType<WarCommand.Agent.Core.Model.MapPoint>(request.SpokenPoint);

        Assert.Equal(85.53m, point.X);
        Assert.Equal(69.42m, point.Y);

        Assert.True(utterance.Confidence >= ContractFixtures.Rules.MinIntentConfidence);
        Assert.Equal(0.31m, utterance.MinDigitConfidence);
        Assert.True(utterance.MinDigitConfidence < ContractFixtures.Profile.PointConfidence.Floor);
    }

    [Fact]
    public void What_a_real_corpus_needs_is_data_rather_than_a_comment()
    {
        // The manifest is the corpus contract: adding a recording is one row plus one wav, never a
        // code change. Its requirements list is what the recordings have to cover before a green
        // run here is evidence about the recognizer.
        Assert.NotEmpty(SpeechCorpus.Requirements);
        Assert.Contains(
            SpeechCorpus.Requirements,
            r => r.Contains("Accented", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            SpeechCorpus.Requirements,
            r => r.Contains("gunfire", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            SpeechCorpus.Requirements,
            r => r.Contains("headset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_declared_row_has_its_buffer_committed_beside_the_manifest()
    {
        // A row whose wav was never committed would silently disappear from the theory.
        foreach (var declared in SpeechCorpus.Declared)
        {
            Assert.True(
                File.Exists(Path.Combine(SpeechCorpus.BuffersDirectory, declared.Wav)),
                $"manifest row '{declared.Id}' names '{declared.Wav}', which is not committed");
        }
    }
}
