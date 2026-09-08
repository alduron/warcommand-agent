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

    /// <summary>The unconstrained recognizer, built on first use. Never the grammar one.</summary>
    /// <remarks>
    /// Separate instance and separate lock on purpose: <see cref="BeginSession(Grammar)"/> holds
    /// the grammar recognizer and its gate for the whole hold, and the near-miss fallback runs
    /// from inside that hold. Sharing either would deadlock.
    /// </remarks>
    private readonly object _freeLock = new();
    private Vosk.VoskRecognizer? _free;
    private short[] _freeScratch = [];

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

    /// <inheritdoc />
    public ISpeechSession BeginSession(IReadOnlyList<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gate.Wait();
        try
        {
            var recognizer = Recognizer(GrammarJson(phrases), FingerprintFor(phrases));
            recognizer.Reset();
            return new Session(recognizer, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public Utterance Transcribe(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (samples.IsEmpty)
        {
            return VoskResultReader.Empty;
        }

        lock (_freeLock)
        {
            _free ??= Built();

            if (_freeScratch.Length < samples.Length)
            {
                Array.Clear(_freeScratch);
                _freeScratch = new short[samples.Length];
            }

            samples.CopyTo(_freeScratch);
            try
            {
                _free.Reset();
                _free.AcceptWaveform(_freeScratch, samples.Length);
                return VoskResultReader.Read(_free.FinalResult());
            }
            finally
            {
                // The audio is gone before the lock is, same rule the session's scratch follows.
                Array.Clear(_freeScratch, 0, samples.Length);
            }
        }

        Vosk.VoskRecognizer Built()
        {
            var recognizer = new Vosk.VoskRecognizer(_model.Handle, AudioBuffer.SampleRateHz);
            recognizer.SetWords(true);
            return recognizer;
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

        lock (_freeLock)
        {
            _free?.Dispose();
            _free = null;
            Array.Clear(_freeScratch);
            _freeScratch = [];
        }

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

    private Vosk.VoskRecognizer Recognizer(CompiledSpeechGrammar compiled) =>
        Recognizer(compiled.ToRecognizerGrammarJson(), compiled.Fingerprint);

    /// <summary>
    /// The recognizer for one phrase list, rebuilt only when the list changes.
    /// </summary>
    /// <remarks>
    /// A menu surface changes under the hold, so this is hit on every panel that opens. The
    /// fingerprint check is what keeps that to one FST rebuild per distinct surface rather than one
    /// per chunk.
    /// </remarks>
    private Vosk.VoskRecognizer Recognizer(string grammarJson, string fingerprint)
    {
        if (_recognizer is not null && string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return _recognizer;
        }

        _recognizer?.Dispose();
        _recognizer = new Vosk.VoskRecognizer(_model.Handle, AudioBuffer.SampleRateHz, grammarJson);
        _recognizer.SetWords(true);
        _fingerprint = fingerprint;
        _log.Note(SpeechEvent.RecognizerRebuilt, fingerprint);
        return _recognizer;
    }

    /// <summary>
    /// The phrase array for a drawn surface, with the out-of-vocabulary sink appended.
    /// </summary>
    /// <remarks>
    /// The sink is not optional, and it was measured. Dropping it forces the decoder to pick one of
    /// the drawn lines for ANY audio, and on a six line page 'attack' came back as MECH at
    /// confidence 1.00: the score does not separate a forced guess from a real word, so no floor
    /// can catch it. With the sink the same page decodes every one of its own labels, resolves
    /// 'hammer' to HAMMERS, and returns [unk] for attack, helicopter and supply, which press
    /// nothing.
    /// <para>
    /// What the sink costs is the deliberate near miss: 'armor' against a page holding AMMO also
    /// lands in it. Pressing a line nobody named is the worse failure of the two, so that case
    /// wants a second decode of the same audio rather than a decoder made to guess.
    /// </para>
    /// </remarks>
    private static string GrammarJson(IReadOnlyList<string> phrases) =>
        System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<string>>(
            [.. phrases, CompiledSpeechGrammar.UnknownPhrase]);

    /// <summary>Stable hash of a phrase list. A changed one rebuilds the recognizer.</summary>
    internal static string FingerprintFor(IReadOnlyList<string> phrases)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', phrases)));
        return System.Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
