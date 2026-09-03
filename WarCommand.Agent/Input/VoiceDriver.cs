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
/// One push-to-talk hold, from the microphone to a parsed intent.
/// </summary>
/// <remarks>
/// The model, the engine, the grammar compiler, the capture and the intent parser were all written
/// and tested, and nothing constructed any of them: holding the push-to-talk key opened the same
/// keyboard menu the other hold key opens and listened to nothing at all. This assembles them.
/// <para>
/// The buffer is capped and zeroed by <see cref="AudioBuffer"/> itself, and nothing here writes
/// audio anywhere: only the parsed intent leaves this class, per binding rule 8.
/// </para>
/// </remarks>
public sealed class VoiceDriver : IDisposable, ISuspendable
{
    private readonly IAudioCapture _capture;
    private readonly Func<Catalog> _catalog;
    private readonly Func<BoardState?> _board;
    private readonly Func<IReadOnlyCollection<string>> _enabledRoleIds;
    private readonly Action<ParseResult> _onParsed;
    private readonly FileClientLog _log;

    private VoskModel? _model;
    private ISpeechEngine? _engine;
    private AudioBuffer? _holding;
    private bool _disposed;

    /// <summary>Builds the driver. The model is loaded on first use, not at startup.</summary>
    public VoiceDriver(
        IAudioCapture capture,
        Func<Catalog> catalog,
        Func<BoardState?> board,
        Func<IReadOnlyCollection<string>> enabledRoleIds,
        Action<ParseResult> onParsed,
        FileClientLog log)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(enabledRoleIds);
        ArgumentNullException.ThrowIfNull(onParsed);
        ArgumentNullException.ThrowIfNull(log);

        _capture = capture;
        _catalog = catalog;
        _board = board;
        _enabledRoleIds = enabledRoleIds;
        _onParsed = onParsed;
        _log = log;
    }

    /// <summary>True once the acoustic model is resident. False until the first hold.</summary>
    public bool IsReady => _engine is not null;

    /// <summary>Why voice is unavailable, or null while it is fine.</summary>
    public string? Fault { get; private set; }

    /// <summary>Push-to-talk down: open the microphone and start filling a buffer.</summary>
    public void BeginHold(string? deviceId)
    {
        if (_disposed || _capture.IsHolding)
        {
            return;
        }

        try
        {
            _capture.Open(deviceId);
            _holding = new AudioBuffer();
            _capture.BeginHold(_holding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Fault = "NO MICROPHONE";
            _log.Warn($"Capture failed to start: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Push-to-talk up: stop capturing, recognize what was said, and parse it.
    /// </summary>
    /// <remarks>
    /// The grammar is compiled from the board as it is at release, so 'accept four' is only in the
    /// vocabulary while slot four exists. That is what keeps a small model accurate.
    /// </remarks>
    public async Task EndHoldAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_capture.IsHolding || _holding is not { } buffer)
        {
            return;
        }

        _capture.EndHold();
        _holding = null;

        try
        {
            var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
            if (engine is null)
            {
                return;
            }

            var catalog = _catalog();
            var context = _board() is { } board
                ? GrammarContext.FromBoard(board, _enabledRoleIds())
                : GrammarContext.Everything;

            var grammar = Grammar.Compile(catalog, context);
            var utterance = await engine.RecognizeAsync(buffer, grammar, cancellationToken)
                .ConfigureAwait(false);

            _onParsed(new IntentParser(grammar).Parse(utterance));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Fault = "SPEECH FAILED";
            _log.Warn($"Recognition failed: {ex.GetType().Name}");
        }
        finally
        {
            buffer.Dispose();
        }
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

    /// <summary>
    /// Panic: drop the open hold, close the device, zero the buffer. Binding rule 7.
    /// </summary>
    /// <remarks>
    /// Registered as the AudioCapture subsystem, which was a no-op placeholder long after this
    /// class was built. Nothing is recognized from what was captured before the press: panicking
    /// mid-sentence means that sentence is discarded, not transcribed.
    /// </remarks>
    public void Suspend()
    {
        if (_capture.IsHolding)
        {
            _capture.EndHold();
        }

        _holding?.Dispose();
        _holding = null;
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
        _holding?.Dispose();
        _holding = null;
        _capture.Close();
        _model?.Dispose();
    }
}
