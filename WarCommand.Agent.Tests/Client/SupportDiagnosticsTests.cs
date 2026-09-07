using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Composition;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Diagnostics;
using WarCommand.Agent.Input;
using WarCommand.Agent.Speech;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// The export is the only instrument we have on somebody else's machine. These are the facts it
/// has to carry, and the ones it must not.
/// </summary>
public class SupportDiagnosticsTests
{
    /// <summary>
    /// A tally answers the question a log of failures cannot: whether it has ever worked here.
    /// </summary>
    [Fact]
    public void The_counters_name_how_many_tries_went_which_way()
    {
        var counters = new SupportCounters();

        counters.ScreenReadAccepted();
        counters.ScreenReadRefused("NO COORDS");
        counters.ScreenReadRefused("NO COORDS");
        counters.ScreenReadRefused("UNSTABLE READ");

        var reads = counters.Reads();
        Assert.NotNull(reads);
        Assert.Contains("4 attempted, 1 read, 3 refused", reads, StringComparison.Ordinal);
        Assert.Contains("NO COORDS x2", reads, StringComparison.Ordinal);
        Assert.Contains("UNSTABLE READ x1", reads, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counters_name_how_the_holds_went()
    {
        var counters = new SupportCounters();

        counters.VoiceHold(silent: true, utterances: 0);
        counters.VoiceHold(silent: false, utterances: 1);
        counters.Utterance(matched: false);

        var holds = counters.Holds();
        Assert.NotNull(holds);
        Assert.Contains("2 held, 1 silent, 1 heard nothing", holds, StringComparison.Ordinal);
        Assert.Contains("1 utterances, 1 unmatched", holds, StringComparison.Ordinal);
    }

    /// <summary>Nothing tried means nothing to say. An empty tally would read as a fault.</summary>
    [Fact]
    public void A_session_that_tried_nothing_renders_no_tally()
    {
        var counters = new SupportCounters();

        Assert.Null(counters.Reads());
        Assert.Null(counters.Holds());
    }

    /// <summary>
    /// Binding rule 9. The words are what says whether the grammar matched; the digits are somebody
    /// standing somewhere, and a grid must not be reconstructable from an exported log.
    /// </summary>
    [Fact]
    public void A_logged_utterance_carries_the_words_and_none_of_the_grid()
    {
        var utterance = new Utterance
        {
            Tokens =
            [
                new RecognizedToken("mortar", 0.91),
                new RecognizedToken("urgent", 0.88),
                new RecognizedToken("grid", 0.95),
                new RecognizedToken("nine", 0.71),
                new RecognizedToken("six", 0.64),
            ],
            Confidence = 0.82,
        };

        var masked = VoiceDriver.Masked(utterance);

        Assert.Equal("mortar urgent grid # #", masked);
        Assert.DoesNotContain("nine", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("six", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seams were optional and nobody passed one, so every speech and input event went to a
    /// null sink and an export from a broken machine looked like one from a working machine. A
    /// fault has to reach the DEFAULT log, not just a verbose one.
    /// </summary>
    [Fact]
    public void A_speech_fault_reaches_the_default_log_and_the_healthy_events_do_not()
    {
        var log = new Recording();
        var bridge = new SpeechLogBridge(log);

        bridge.Note(SpeechEvent.NoInputDevice);
        bridge.Note(SpeechEvent.ModelUnavailable, "C:/models/vosk");
        bridge.Note(SpeechEvent.ModelLoaded, "C:/models/vosk");

        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains(log.Warnings, w => w.Contains("NoInputDevice", StringComparison.Ordinal));
        Assert.Contains(log.Warnings, w => w.Contains("C:/models/vosk", StringComparison.Ordinal));
        Assert.Single(log.Infos);
    }

    [Fact]
    public void An_input_fault_reaches_the_default_log()
    {
        var log = new Recording();
        var bridge = new InputLogBridge(log);

        bridge.Note(InputEvent.ExclusiveFullscreenDetected);
        bridge.Note(InputEvent.KeyboardHookInstalled);

        Assert.Single(log.Warnings);
        Assert.Single(log.Infos);
    }

    private sealed class Recording : IClientLog
    {
        public List<string> Infos { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message, Exception? error = null) => Warnings.Add(message);
    }
}
