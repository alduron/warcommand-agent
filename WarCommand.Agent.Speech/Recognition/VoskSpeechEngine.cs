using WarCommand.Agent.Core.Grammar;

namespace WarCommand.Agent.Speech.Recognition;

/// <summary>
/// <see cref="ISpeechEngine"/> over the Vosk small English model, constrained to a word list
/// compiled from the catalog.
/// </summary>
/// <remarks>
/// <para>
/// The recognizer is built per compiled grammar and rebuilt only when the compiled fingerprint
/// changes, so a board-state change that prunes a verb costs one small FST rebuild rather than a
/// model reload. Vosk recognizers are not thread safe, so one hold at a time goes through the gate.
/// </para>
/// <para>
/// Word timings are on and alternatives are off, deliberately. Vosk drops the per-word <c>conf</c>
/// field in alternatives mode and offers only a lattice score for the whole hypothesis, and
/// <c>request_points.confidence</c> is defined as the minimum per-token confidence over the grid
/// digits. Trading that for an n-best list would silently replace the only number that moves when
/// exactly one digit is wrong, so <see cref="Utterance.Alternatives"/> is always empty from this
/// engine and near-tie disambiguation is driven by the generated near-floor pair list instead.
/// </para>
/// </remarks>
public sealed class VoskSpeechEngine : ISpeechEngine, IDisposable
{
    private readonly VoskModel _model;
    private readonly ISpeechLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Vosk.VoskRecognizer? _recognizer;
    private string? _fingerprint;
    private bool _disposed;

    /// <summary>Binds an engine to an already-loaded model.</summary>
    public VoskSpeechEngine(VoskModel model, ISpeechLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _log = log ?? NullSpeechLog.Instance;
    }

    /// <summary>The model directory in use. Shown read-only in the settings window at M1.</summary>
    public string ModelDirectory => _model.ModelDirectory;

    /// <summary>The fingerprint of the grammar the current recognizer was built for, or null.</summary>
    public string? LoadedGrammarFingerprint => _fingerprint;

    /// <inheritdoc />
    public async Task<Utterance> RecognizeAsync(
        AudioBuffer buffer,
        Grammar grammar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(grammar);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return VoskResultReader.Empty;
        }

        var compiled = SpeechGrammarCompiler.Compile(grammar);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognizer = Recognizer(compiled);
            return await Task.Run(() => Decode(recognizer, buffer), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Takes the same gate a whole-buffer decode takes and holds it until the session is disposed:
    /// a Vosk recognizer is not thread safe and one hold is one session.
    /// </remarks>
    public ISpeechSession BeginSession(Grammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var compiled = SpeechGrammarCompiler.Compile(grammar);
        _gate.Wait();
        try
        {
            var recognizer = Recognizer(compiled);
            recognizer.Reset();
            return new Session(recognizer, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>Releases the recognizer. The model outlives the engine and is disposed by its owner.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer?.Dispose();
        _recognizer = null;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// One hold's streaming decode over the engine's recognizer.
    /// </summary>
    /// <remarks>
    /// The scratch array exists because Vosk takes a short[] and a length, not a span. It is grown
    /// to the largest chunk seen and zeroed on release, so no audio outlives the hold.
    /// </remarks>
    private sealed class Session(Vosk.VoskRecognizer recognizer, SemaphoreSlim gate) : ISpeechSession
    {
        private short[] _scratch = new short[4096];
        private bool _disposed;

        public Utterance? Feed(ReadOnlySpan<short> samples)
        {
            if (_disposed || samples.IsEmpty)
            {
                return null;
            }

            if (_scratch.Length < samples.Length)
            {
                Array.Clear(_scratch);
                _scratch = new short[samples.Length];
            }

            samples.CopyTo(_scratch);
            var endpoint = recognizer.AcceptWaveform(_scratch, samples.Length);
            Array.Clear(_scratch, 0, samples.Length);

            return endpoint ? NonEmpty(recognizer.Result()) : null;
        }

        public Utterance? Final() => _disposed ? null : NonEmpty(recognizer.FinalResult());

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Array.Clear(_scratch);

            // The recognizer belongs to the engine and is reused by fingerprint. Reset rather than
            // dispose, or the next hold decodes on top of this one's tail.
            recognizer.Reset();
            gate.Release();
        }

        /// <summary>An utterance with no tokens is silence, and silence is not an intent.</summary>
        private static Utterance? NonEmpty(string? json)
        {
            var utterance = VoskResultReader.Read(json);
            return utterance.Tokens.Count > 0 ? utterance : null;
        }
    }

    private static Utterance Decode(Vosk.VoskRecognizer recognizer, AudioBuffer buffer)
    {
        recognizer.Reset();
        recognizer.AcceptWaveform(buffer.Storage, buffer.Length);
        return VoskResultReader.Read(recognizer.FinalResult());
    }

    private Vosk.VoskRecognizer Recognizer(CompiledSpeechGrammar compiled)
    {
        if (_recognizer is not null && string.Equals(_fingerprint, compiled.Fingerprint, StringComparison.Ordinal))
        {
            return _recognizer;
        }

        _recognizer?.Dispose();
        _recognizer = new Vosk.VoskRecognizer(
            _model.Handle,
            AudioBuffer.SampleRateHz,
            compiled.ToRecognizerGrammarJson());
        _recognizer.SetWords(true);
        _fingerprint = compiled.Fingerprint;
        _log.Note(SpeechEvent.RecognizerRebuilt, compiled.Fingerprint);
        return _recognizer;
    }
}
