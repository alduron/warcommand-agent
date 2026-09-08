using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Input;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Speech;
using WarCommand.Agent.Speech.Capture;
using WarCommand.Agent.Speech.Recognition;

namespace WarCommand.Agent.Composition;

/// <summary>
/// One push-to-talk hold, from the microphone to a parsed intent, decoded while the key is down.
/// </summary>
/// <remarks>
/// The model, the engine, the grammar compiler, the capture and the intent parser were all written
/// and tested, and nothing constructed any of them: holding the push-to-talk key opened the same
/// keyboard menu the other hold key opens and listened to nothing at all. This assembles them.
/// <para>
/// Recognition is STREAMING, not decode-on-release. An utterance acts the moment the speaker stops,
/// so a panel spoken under the hold opens under the hold and closing it is letting go, exactly as
/// pressing its digit would be. Decoding only on release made every panel unreachable by voice,
/// because the surface a panel draws on is gone by the time the key is up.
/// </para>
/// <para>
/// The buffer is capped and zeroed by <see cref="AudioBuffer"/> itself, chunks are pooled and
/// cleared as they are consumed, and nothing here writes audio anywhere: only the parsed intent
/// leaves this class, per binding rule 8.
/// </para>
/// </remarks>
public sealed class VoiceDriver : IDisposable, ISuspendable
{
    /// <summary>
    /// Chunks held between the capture thread and the decoder. Bounded, and the oldest is dropped
    /// rather than blocking: capture must never wait on a decode.
    /// </summary>
    /// <remarks>
    /// Deep enough to cover the model load on the first hold, which is about 400 ms, at the 10 to
    /// 50 ms chunks WASAPI delivers.
    /// </remarks>
    private const int MaxQueuedChunks = 128;

    private readonly IAudioCapture _capture;
    private readonly Func<Catalog> _catalog;
    private readonly Func<BoardState?> _board;
    private readonly Func<IReadOnlyCollection<string>> _enabledRoleIds;
    private readonly Action<ParseResult> _onParsed;

    // The surface being drawn right now, re-read on every chunk, and what saying one of its lines
    // does. Voice selects the option the key would select, so these two are the whole of the menu
    // route: the vocabulary IS this list and a match IS a key press.
    private readonly Func<IReadOnlyList<MenuEntry>> _drawnOptions;
    private readonly Action<MenuEntry> _onMenuSpoken;
    private readonly SilentHoldMonitor _silence;
    private readonly RollingFileLog _log;
    private readonly ISpeechLog _speech;
    private readonly SupportCounters _counters;

    private VoskModel? _model;
    private ISpeechEngine? _engine;
    private AudioBuffer? _holding;

    /// <summary>
    /// Audio since the last utterance boundary, for the near-miss re-decode and nothing else.
    /// </summary>
    /// <remarks>
    /// Capped and zeroed like the hold buffer, and reset the moment an utterance is handled, so it
    /// never holds more than one thing somebody said. Binding rule 9 is unchanged: it is memory,
    /// it is cleared, and no member on it writes anywhere.
    /// </remarks>
    private AudioBuffer? _utterance;
    private Channel<Chunk>? _chunks;
    private Task? _pump;
    private bool _disposed;

    // This hold only. Reset on every key down, read on key up. A hold that heard nothing is the
    // single most reported voice fault and the one the log said nothing about at all.
    private int _chunksThisHold;
    private int _utterancesThisHold;

    // Key-down to key-up, which is NOT what the chunk count measures. Capture takes ~130 ms to
    // deliver its first buffer, so a hold that heard nothing needs both numbers to say whether the
    // device was slow or the key was simply not held while anybody was talking.
    private long _holdStartedTicks;

    // The size of the vocabulary this hold was decoded against, and how many roles pruned it.
    // The grammar is pruned by the board and by the roles this player enabled, so a membership that
    // never loaded leaves enabledRoleIds empty and the recognizer is handed a word list with no
    // request types in it. Good audio then decodes to nothing, which is indistinguishable in a log
    // from a dead microphone unless these two numbers are in it.
    private int _vocabularyThisHold;
    private int _rolesThisHold;

    // The peak of what the DECODER was fed, which is not the same measurement as the buffer's.
    // The buffer is filled by the capture thread; the recognizer is fed from a pooled channel on
    // the decode task. A hold with a healthy buffer peak and a dead fed peak means the chunks are
    // being lost or cleared between the two, and nothing in the log could tell those apart.
    private int _fedPeakThisHold;

    /// <summary>Builds the driver. The model is loaded on first use, not at startup.</summary>
    public VoiceDriver(
        IAudioCapture capture,
        Func<Catalog> catalog,
        Func<BoardState?> board,
        Func<IReadOnlyCollection<string>> enabledRoleIds,
        Action<ParseResult> onParsed,
        SilentHoldMonitor silence,
        RollingFileLog log,
        ISpeechLog speechLog,
        SupportCounters counters,
        Func<IReadOnlyList<MenuEntry>>? drawnOptions = null,
        Action<MenuEntry>? onMenuSpoken = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(enabledRoleIds);
        ArgumentNullException.ThrowIfNull(onParsed);
        ArgumentNullException.ThrowIfNull(silence);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(speechLog);
        ArgumentNullException.ThrowIfNull(counters);

        _capture = capture;
        _catalog = catalog;
        _board = board;
        _enabledRoleIds = enabledRoleIds;
        _onParsed = onParsed;
        _silence = silence;
        _log = log;
        _speech = speechLog;
        _counters = counters;

        // Optional so a test that only cares about request parsing constructs nothing extra. An
        // agent that passes neither has no menu on screen by definition, and the catalog route is
        // what a hold at rest has always used.
        _drawnOptions = drawnOptions ?? (() => []);
        _onMenuSpoken = onMenuSpoken ?? (_ => { });
    }

    /// <summary>True once the acoustic model is resident. False until the first hold.</summary>
    public bool IsReady => _engine is not null;

    /// <summary>Why voice is unavailable, or null while it is fine.</summary>
    public string? Fault { get; private set; }

    /// <summary>NO AUDIO FROM the device once enough holds in a row heard nothing, else null.</summary>
    public string? Warning => _silence.Warning;

    /// <summary>Peak of what the recognizer was actually fed this hold, in dBFS.</summary>
    private double FedPeakDbfs => _fedPeakThisHold == 0
        ? double.NegativeInfinity
        : 20.0 * Math.Log10(_fedPeakThisHold / (double)short.MaxValue);

    /// <summary>
    /// Push-to-talk down: open the microphone and start decoding.
    /// </summary>
    /// <remarks>
    /// The grammar is compiled from the board as it is at key DOWN, the same moment the coordinate
    /// is snapshotted. It used to be compiled at release, which read a board that had moved under
    /// the speaker while they were still talking about it.
    /// </remarks>
    public void BeginHold(string? deviceId)
    {
        if (_disposed || _capture.IsHolding)
        {
            return;
        }

        var chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(MaxQueuedChunks)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        try
        {
            _chunksThisHold = 0;
            _utterancesThisHold = 0;
            _holdStartedTicks = Stopwatch.GetTimestamp();
            _fedPeakThisHold = 0;
            _capture.Open(deviceId);
            _holding = new AudioBuffer();
            _utterance = new AudioBuffer();
            _chunks = chunks;

            // The capture thread's whole job here: copy and return. Anything slower starves capture.
            _capture.OnChunk = samples =>
            {
                var pooled = ArrayPool<short>.Shared.Rent(samples.Length);
                samples.CopyTo(pooled);
                Interlocked.Increment(ref _chunksThisHold);
                if (!chunks.Writer.TryWrite(new Chunk(pooled, samples.Length)))
                {
                    ArrayPool<short>.Shared.Return(pooled, clearArray: true);
                }
            };

            _capture.BeginHold(_holding);
            _pump = Task.Run(() => PumpAsync(chunks.Reader, GrammarNow()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Fault = "NO MICROPHONE";
            _log.Warn($"Capture failed to start: {ex.GetType().Name}");

            // The tap is cleared here too. A hold that never opened leaves no key-up path through
            // EndHoldAsync, so a handler left attached would outlive the hold that installed it.
            _capture.OnChunk = null;
            chunks.Writer.TryComplete();
            Clear();
        }
    }

    /// <summary>Push-to-talk up: stop capturing and let the decoder finish what is in flight.</summary>
    public async Task EndHoldAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_capture.IsHolding)
        {
            return;
        }

        _capture.EndHold();
        _capture.OnChunk = null;
        _chunks?.Writer.TryComplete();

        if (_pump is { } pump)
        {
            await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The peak the whole hold reached. A device that exists and delivers silence is otherwise
        // indistinguishable from somebody who held the key and said nothing.
        var silent = false;
        var peak = double.NegativeInfinity;
        if (_holding is { } buffer)
        {
            peak = buffer.PeakDbfs;
            silent = _silence.Hold(buffer, _capture.Device?.FriendlyName ?? string.Empty)
                is not SilentHoldResult.HadAudio;
        }

        // The whole hold, in one line, in the file the customer exports. "I said mortar and nothing
        // happened" used to produce an empty log: nothing recorded whether the microphone delivered
        // anything, whether the recognizer completed an utterance, or whether the parser refused
        // one. These four numbers separate a dead device from a dead recognizer from a word that is
        // not in the grammar, and none of them is a coordinate or anything anybody said.
        var keyDownMs = Stopwatch.GetElapsedTime(_holdStartedTicks).TotalMilliseconds;
        var shape = FormattableString.Invariant(
            $"key down {keyDownMs:0} ms, {_chunksThisHold} chunks, buffer peak {peak:0.0} dBFS, decoder peak {FedPeakDbfs:0.0} dBFS, {_utterancesThisHold} utterances, vocabulary {_vocabularyThisHold} words from {_rolesThisHold} roles");

        if (_utterancesThisHold == 0)
        {
            // WARN: this is the reported fault, so it has to survive the default log rather than
            // be dropped with the rest of INFO.
            _log.Warn($"Hold heard nothing. {shape}");
        }
        else
        {
            _log.Info($"Hold ended. {shape}");
        }

        _counters.VoiceHold(silent, _utterancesThisHold);

        Clear();
    }

    /// <summary>Ends the hold and drops what was captured without recognizing any of it.</summary>
    public void DiscardHold()
    {
        if (_capture.IsHolding)
        {
            _capture.EndHold();
        }

        _capture.OnChunk = null;
        _chunks?.Writer.TryComplete();
        Clear();
    }

    /// <summary>
    /// Panic: drop the open hold, close the device, zero the buffer. Binding rule 7.
    /// </summary>
    /// <remarks>
    /// Nothing is recognized from what was captured before the press: panicking mid-sentence means
    /// that sentence is discarded, not transcribed.
    /// </remarks>
    public void Suspend()
    {
        DiscardHold();
        _capture.Close();
    }

    /// <summary>Nothing to re-open: the next hold opens the device again.</summary>
    public void Resume()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DiscardHold();
        _capture.Close();
        _model?.Dispose();
    }

    /// <summary>
    /// The vocabulary this hold is decoded against, read at key down.
    /// </summary>
    /// <remarks>
    /// Pruned by the board and by the roles THIS player enabled: accuracy is a function of the
    /// vocabulary actually loaded, so a slot reference is only in it while that slot exists.
    /// </remarks>
    private Grammar GrammarNow()
    {
        var roles = _enabledRoleIds();
        var context = _board() is { } board
            ? GrammarContext.FromBoard(board, roles)
            : GrammarContext.Everything;

        _rolesThisHold = roles.Count;
        return Grammar.Compile(_catalog(), context);
    }

    /// <summary>Drains chunks into the recognizer, raising each utterance as it completes.</summary>
    private async Task PumpAsync(ChannelReader<Chunk> reader, Grammar grammar)
    {
        ISpeechSession? session = null;
        try
        {
            var engine = await EngineAsync(CancellationToken.None).ConfigureAwait(false);
            if (engine is null)
            {
                await DrainAsync(reader).ConfigureAwait(false);
                return;
            }

            var parser = new IntentParser(grammar, BundledContracts.NearFloorPairs());
            IReadOnlyList<string>? loaded = null;
            IReadOnlyList<MenuEntry> drawn = [];

            await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    // Re-read every chunk, not once at key down. A panel opens UNDER the hold, and
                    // the moment it does its lines are the only words that mean anything.
                    drawn = _drawnOptions();
                    var wanted = VocabularyFor(drawn, grammar);
                    if (loaded is null || !wanted.SequenceEqual(loaded, StringComparer.Ordinal))
                    {
                        session?.Dispose();
                        session = wanted.Count > 0
                            ? engine.BeginSession(wanted)
                            : engine.BeginSession(grammar);
                        loaded = wanted;
                        _vocabularyThisHold = wanted.Count > 0
                            ? wanted.Count
                            : SpeechGrammarCompiler.Compile(grammar).AllWords.Count;
                    }

                    var fed = chunk.Samples.AsSpan(0, chunk.Length);
                    _utterance?.Append(fed);
                    foreach (var sample in fed)
                    {
                        var magnitude = Math.Abs((int)sample);
                        if (magnitude > _fedPeakThisHold)
                        {
                            _fedPeakThisHold = magnitude;
                        }
                    }

                    // The block above always leaves a session behind: loaded is null on the first
                    // pass, so the rebuild runs before anything is ever fed.
                    if (session!.Feed(fed) is { } utterance)
                    {
                        Heard(utterance, parser, drawn);
                    }
                }
                finally
                {
                    ArrayPool<short>.Shared.Return(chunk.Samples, clearArray: true);
                }
            }

            if (session?.Final() is { } last)
            {
                Heard(last, parser, drawn);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Fault = "SPEECH FAILED";
            _log.Warn($"Recognition failed: {ex.GetType().Name}");
            await DrainAsync(reader).ConfigureAwait(false);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// One completed utterance: recorded, then handed on.
    /// </summary>
    /// <remarks>
    /// DIGITS ARE MASKED and everything else is written out. A spoken grid is a coordinate and
    /// binding rule 9 keeps coordinates out of a file people pass around, but the rest of an
    /// utterance is catalog vocabulary the recognizer was constrained to and the request itself
    /// goes on the wire anyway. "heard 'mortar grid # # . # #'" against "heard 'mortar'" is the
    /// whole difference between a grammar gap and an incomplete sentence, and neither was
    /// recoverable from an export before.
    /// </remarks>
    private void Heard(Utterance utterance, IntentParser parser, IReadOnlyList<MenuEntry> drawn)
    {
        Interlocked.Increment(ref _utterancesThisHold);

        // A drawn surface owns the words outright. Voice is not a second interface: saying a line
        // presses that line, and while lines are on screen there is nothing else to have said.
        if (drawn.Count > 0)
        {
            // Two readings of the same utterance, and they are not interchangeable. Matching reads
            // the RAW text because a line's number is a digit token and Masked turns every digit
            // into '#': matching on the masked form made 'two' unpressable while its label worked.
            // The log only ever sees the masked form, because a spoken grid is a coordinate.
            var spoken = Spoken(utterance);
            var said = Masked(utterance);
            var floor = (double)_catalog().GrammarRules.MinIntentConfidence;

            if (MenuSpeech.Match(spoken, drawn) is { } entry)
            {
                // The floor gates a MATCH, never the fallback below it. The grammar carries its
                // out-of-vocabulary sink, so a low score here is a line the decoder is unsure of
                // rather than a word it was forced to guess, and pressing it would be the wrong
                // line rather than merely a wasted press.
                if (utterance.Confidence < floor)
                {
                    _log.Warn(FormattableString.Invariant(
                        $"Heard '{said}' at {utterance.Confidence:0.00}, under the {floor:0.00} floor. Nothing pressed."));
                    _counters.Utterance(matched: false);
                    Rearm();
                    return;
                }

                _log.Info($"Heard '{said}' -> line {entry.Digit} {entry.Label}");
                _counters.Utterance(matched: true);
                _onMenuSpoken(entry);
                Rearm();
                return;
            }

            // A RUN of lines in one breath, which is how a grid is actually said: eight numbers at
            // speed come back as one utterance of eight words. Matched whole it reached nothing, so
            // the coordinate page ignored everything said to it. Tried after the whole-utterance
            // match so a two-word LABEL still wins over two lines that happen to be named its parts.
            if (MenuSpeech.MatchRun(spoken, drawn) is { Count: > 0 } run)
            {
                _log.Info($"Heard '{said}' -> {run.Count} lines: {string.Join(", ", run.Select(r => r.Label))}");
                _counters.Utterance(matched: true);
                foreach (var spokenLine in run)
                {
                    _onMenuSpoken(spokenLine);
                }

                Rearm();
                return;
            }

            // The grammar sent it to [unk], which is every word not on this page AND every near
            // miss of one. Ask what was actually said and measure THAT against the drawn lines:
            // 'armor' transcribes as armor and lands on AMMO, where the constrained decode could
            // only say it was not one of the six.
            if (Nearest(drawn) is { } near)
            {
                _log.Info($"Heard '{said}', re-read as '{near.Said}' -> line {near.Entry.Digit} {near.Entry.Label}");
                _counters.Utterance(matched: true);
                _onMenuSpoken(near.Entry);
                Rearm();
                return;
            }

            if (IsExclusive(drawn))
            {
                // WARN: a word said at a menu that matched no line on it is the report, and the
                // lines it was measured against are the whole diagnosis.
                _log.Warn($"Heard '{said}' at a menu drawing {drawn.Count} lines, matched none.");
                _counters.Utterance(matched: false);
                Rearm();
                return;
            }

            // The armed hint draws three surface names and nothing else, so anything that is not
            // one of them is a request. Falls through to the catalog rather than being refused.
        }

        Rearm();

        var parsed = parser.Parse(utterance);
        var masked = Masked(utterance);

        var line = FormattableString.Invariant(
            $"Heard '{masked}' at {utterance.Confidence:0.00}, {utterance.Alternatives.Count} alternatives -> {Outcome(parsed)}");

        // A refusal is WARN so it reaches the default log: an utterance the parser could not place
        // is the report, and at INFO it would be dropped from exactly the export that needs it.
        if (parsed is ParsedCommand { Prompt: not null })
        {
            _log.Warn(line);
            _counters.Utterance(matched: false);
        }
        else
        {
            _log.Info(line);
            _counters.Utterance(matched: true);
        }

        _onParsed(parsed);
    }

    /// <summary>
    /// True when the drawn surface owns the words outright.
    /// </summary>
    /// <remarks>
    /// A real list draws digits beside its lines. The armed hint draws three surface names and no
    /// digits at all, and it is what an open hold shows before anybody has navigated, so treating
    /// it as exclusive would take spoken requests away from the one moment they are most used.
    /// </remarks>
    private static bool IsExclusive(IReadOnlyList<MenuEntry> drawn) =>
        drawn.Any(o => o.Digit >= 0);

    /// <summary>
    /// What the recognizer is constrained to: the drawn lines, plus the catalog when the surface is
    /// only the armed hint. Empty means nothing is drawn and the catalog grammar is the whole of it.
    /// </summary>
    private static IReadOnlyList<string> VocabularyFor(IReadOnlyList<MenuEntry> drawn, Grammar grammar)
    {
        var labels = MenuSpeech.Vocabulary(drawn);
        if (labels.Count == 0 || IsExclusive(drawn))
        {
            return labels;
        }

        return [.. labels
            .Concat(SpeechGrammarCompiler.Compile(grammar).RecognizerPhrases)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The line an unconstrained re-read of this utterance lands on, or null when it lands on none.
    /// </summary>
    /// <remarks>
    /// Only ever reached after the grammar said [unk]. The constrained decode is the better reader
    /// of a word that IS on the page, so this never overrides it: measured, the grammar hears MECH
    /// where a free read hears 'mac', and FOB KIT where a free read hears 'five give'.
    /// </remarks>
    private (MenuEntry Entry, string Said)? Nearest(IReadOnlyList<MenuEntry> drawn)
    {
        if (_engine is not { } engine || _utterance is not { Length: > 0 } audio)
        {
            return null;
        }

        var free = engine.Transcribe(audio.Samples);
        return MenuSpeech.Match(Spoken(free), drawn) is { } entry ? (entry, Masked(free)) : null;
    }

    /// <summary>Drops the audio behind the utterance just handled, so the next one stands alone.</summary>
    private void Rearm() => _utterance?.Reset();

    /// <summary>
    /// The utterance as said, digits included. For MATCHING only, and never for a log line.
    /// </summary>
    /// <remarks>
    /// A menu line's number is a digit token, so <see cref="Masked"/> is the wrong reading here: it
    /// turns 'two' into '#' and makes every line unpressable by its number while its label still
    /// works. Nothing this returns is written anywhere; it is compared against the drawn labels and
    /// dropped.
    /// </remarks>
    private static string Spoken(Utterance utterance) =>
        string.Join(' ', utterance.Tokens.Select(t => t.Text));

    /// <summary>
    /// The utterance with every digit token replaced by <c>#</c>. A grid cannot be read back out
    /// of one of these, and the words that decide whether the grammar matched are all still there.
    /// </summary>
    internal static string Masked(Utterance utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        return string.Join(' ', utterance.Tokens.Select(t => t.IsDigit ? "#" : t.Text));
    }

    /// <summary>What the parser made of it: an id, never a coordinate and never a callsign.</summary>
    private static string Outcome(ParseResult parsed) => parsed switch
    {
        ParsedRequest request => "request " + request.TypeId,
        ParsedCommand { Prompt: { } prompt } => "refused " + prompt,
        ParsedCommand command => "command " + command.VerbId,
        _ => parsed.GetType().Name,
    };

    /// <summary>Returns every queued chunk to the pool, zeroed. Audio is never left lying in one.</summary>
    private static async Task DrainAsync(ChannelReader<Chunk> reader)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false))
            {
                ArrayPool<short>.Shared.Return(chunk.Samples, clearArray: true);
            }
        }
        catch (ChannelClosedException)
        {
            // Nothing left to return.
        }
    }

    private void Clear()
    {
        _holding?.Dispose();
        _holding = null;
        _utterance?.Dispose();
        _utterance = null;
        _chunks = null;
        _pump = null;
    }

    private async Task<ISpeechEngine?> EngineAsync(CancellationToken cancellationToken)
    {
        if (_engine is { } ready)
        {
            return ready;
        }

        try
        {
            _model = await new VoskModelLoader(_speech)
                .LoadAsync(VoskModelLoader.DefaultModelDirectory, cancellationToken)
                .ConfigureAwait(false);
            _engine = new VoskSpeechEngine(_model, _speech);
            Fault = null;
            _log.Info("Speech model loaded.");
            return _engine;
        }
        catch (SpeechModelUnavailableException ex)
        {
            // Not a fault to retry every hold: the model is either installed or it is not.
            Fault = "NO SPEECH MODEL";
            _log.Warn($"Speech model unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>One pooled chunk and how much of it is audio.</summary>
    private readonly record struct Chunk(short[] Samples, int Length);
}
