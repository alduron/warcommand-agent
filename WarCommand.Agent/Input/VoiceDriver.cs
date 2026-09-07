using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Input;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Grammar;
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
    private readonly SilentHoldMonitor _silence;
    private readonly RollingFileLog _log;
    private readonly ISpeechLog _speech;
    private readonly SupportCounters _counters;

    private VoskModel? _model;
    private ISpeechEngine? _engine;
    private AudioBuffer? _holding;
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
        SupportCounters counters)
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
    }

    /// <summary>True once the acoustic model is resident. False until the first hold.</summary>
    public bool IsReady => _engine is not null;

    /// <summary>Why voice is unavailable, or null while it is fine.</summary>
    public string? Fault { get; private set; }

    /// <summary>NO AUDIO FROM the device once enough holds in a row heard nothing, else null.</summary>
    public string? Warning => _silence.Warning;

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
            _capture.Open(deviceId);
            _holding = new AudioBuffer();
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
            $"key down {keyDownMs:0} ms, {_chunksThisHold} chunks, peak {peak:0.0} dBFS, {_utterancesThisHold} utterances, vocabulary {_vocabularyThisHold} words from {_rolesThisHold} roles");

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

            _vocabularyThisHold = SpeechGrammarCompiler.Compile(grammar).AllWords.Count;
            session = engine.BeginSession(grammar);
            var parser = new IntentParser(grammar, BundledContracts.NearFloorPairs());

            await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (session.Feed(chunk.Samples.AsSpan(0, chunk.Length)) is { } utterance)
                    {
                        Heard(utterance, parser);
                    }
                }
                finally
                {
                    ArrayPool<short>.Shared.Return(chunk.Samples, clearArray: true);
                }
            }

            if (session.Final() is { } last)
            {
                Heard(last, parser);
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
    private void Heard(Utterance utterance, IntentParser parser)
    {
        Interlocked.Increment(ref _utterancesThisHold);

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
