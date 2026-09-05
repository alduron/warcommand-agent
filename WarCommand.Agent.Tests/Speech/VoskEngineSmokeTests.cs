using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Tests.Core;
using WarCommand.Agent.Speech;
using WarCommand.Agent.Speech.Recognition;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// The speech path end to end, against the real acoustic model when one is installed.
/// </summary>
/// <remarks>
/// Every other speech test uses a fake engine, which is why the whole recognizer could be written,
/// tested and never constructed: nothing exercised the model load, the native handle or the
/// grammar handed to it. This does, and it skips rather than fails where no model is installed,
/// because a CI runner has no 40 MB model and should not download one.
/// </remarks>
public class VoskEngineSmokeTests
{
    private static bool ModelIsInstalled => Directory.Exists(VoskModelLoader.DefaultModelDirectory);

    [Fact]
    public async Task The_installed_model_loads_and_recognizes_against_a_compiled_grammar()
    {
        // Returns rather than skips: no skippable-fact package here, and adding one for a single
        // machine-dependent test is a worse trade than a guard with a reason written next to it.
        if (!ModelIsInstalled)
        {
            return;
        }

        using var model = await new VoskModelLoader()
            .LoadAsync(VoskModelLoader.DefaultModelDirectory, CancellationToken.None);

        Assert.True(model.IsLoaded);

        var engine = new VoskSpeechEngine(model);
        var grammar = Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

        // Silence. The point is the whole path: model, native recognizer, compiled vocabulary and
        // a buffer handed across. What comes back is an empty utterance, which is correct, and the
        // parser rejects it rather than inventing a request.
        using var buffer = new AudioBuffer();
        buffer.Append(new short[16000]);

        var utterance = await engine.RecognizeAsync(buffer, grammar, CancellationToken.None);

        Assert.NotNull(utterance);
        Assert.IsType<ParsedRejection>(new IntentParser(grammar).Parse(utterance));
    }

    /// <summary>
    /// The streaming path, which is the one the agent actually runs now.
    /// </summary>
    /// <remarks>
    /// Fed in 20 ms chunks, exactly as WASAPI delivers them. Nothing else covers the session: the
    /// whole-buffer decode above is kept for the tests and is not what a hold uses any more.
    /// </remarks>
    [Fact]
    public async Task A_streamed_hold_feeds_chunks_and_flushes_what_is_left_at_key_up()
    {
        if (!ModelIsInstalled)
        {
            return;
        }

        using var model = await new VoskModelLoader()
            .LoadAsync(VoskModelLoader.DefaultModelDirectory, CancellationToken.None);

        var engine = new VoskSpeechEngine(model);
        var grammar = Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);

        using (var session = engine.BeginSession(grammar))
        {
            // One second of silence in 20 ms chunks. Silence is the honest case for a test with no
            // recorded speech: the point is that the chunks cross the seam and the session flushes.
            var chunk = new short[AudioBuffer.SampleRateHz / 50];
            for (var i = 0; i < 50; i++)
            {
                var spoken = session.Feed(chunk);
                if (spoken is not null)
                {
                    Assert.IsType<ParsedRejection>(new IntentParser(grammar).Parse(spoken));
                }
            }

            // Silence produces no tokens, so the flush is null rather than an empty utterance:
            // nothing may be handed to the parser that the parser would have to invent an intent
            // from.
            Assert.Null(session.Final());
        }

        // The gate the session held is released on dispose, so the engine is usable again. A
        // session that leaked it would deadlock the next hold with no error anywhere.
        using var after = new AudioBuffer();
        after.Append(new short[16000]);
        Assert.NotNull(await engine.RecognizeAsync(after, grammar, CancellationToken.None));
    }
}
