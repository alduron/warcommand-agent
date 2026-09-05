using System.Buffers;
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
    private readonly FileClientLog _log;

    private VoskModel? _model;
    private ISpeechEngine? _engine;
    private AudioBuffer? _holding;
    private Channel<Chunk>? _chunks;
    private Task? _pump;
    private bool _disposed;

    /// <summary>Builds the driver. The model is loaded on first use, not at startup.</summary>
    public VoiceDriver(
        IAudioCapture capture,
        Func<Catalog> catalog,
        Func<BoardState?> board,
        Func<IReadOnlyCollection<string>> enabledRoleIds,
        Action<ParseResult> onParsed,
        SilentHoldMonitor silence,
        FileClientLog log)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(enabledRoleIds);
        ArgumentNullException.ThrowIfNull(onParsed);
        ArgumentNullException.ThrowIfNull(silence);
        ArgumentNullException.ThrowIfNull(log);

        _capture = capture;
        _catalog = catalog;
        _board = board;
        _enabledRoleIds = enabledRoleIds;
        _onParsed = onParsed;
        _silence = silence;
        _log = log;
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
            _capture.Open(deviceId);
            _holding = new AudioBuffer();
            _chunks = chunks;

            // The capture thread's whole job here: copy and return. Anything slower starves capture.
            _capture.OnChunk = samples =>
            {
                var pooled = ArrayPool<short>.Shared.Rent(samples.Length);
                samples.CopyTo(pooled);
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
        if (_holding is { } buffer)
        {
            _silence.Hold(buffer, _capture.Device?.FriendlyName ?? string.Empty);
        }

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
        var context = _board() is { } board
            ? GrammarContext.FromBoard(board, _enabledRoleIds())
            : GrammarContext.Everything;

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

            session = engine.BeginSession(grammar);
            var parser = new IntentParser(grammar, BundledContracts.NearFloorPairs());

            await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (session.Feed(chunk.Samples.AsSpan(0, chunk.Length)) is { } utterance)
                    {
                        _onParsed(parser.Parse(utterance));
                    }
                }
                finally
                {
                    ArrayPool<short>.Shared.Return(chunk.Samples, clearArray: true);
                }
            }

            if (session.Final() is { } last)
            {
                _onParsed(parser.Parse(last));
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
            _model = await new VoskModelLoader()
                .LoadAsync(VoskModelLoader.DefaultModelDirectory, cancellationToken)
                .ConfigureAwait(false);
            _engine = new VoskSpeechEngine(_model);
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
