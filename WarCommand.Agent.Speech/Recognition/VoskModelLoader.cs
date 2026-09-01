using System.Reflection;

namespace WarCommand.Agent.Speech.Recognition;

/// <summary>The model directory is missing, unreadable, or not a Vosk model.</summary>
public sealed class SpeechModelUnavailableException : Exception
{
    /// <summary>A model was unavailable, with no reason given.</summary>
    public SpeechModelUnavailableException()
        : base("The speech model is unavailable.")
    {
    }

    /// <summary>A model was unavailable.</summary>
    public SpeechModelUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>A model was unavailable, wrapping the loader's own failure.</summary>
    public SpeechModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The loaded Vosk acoustic model. One per process: it is ~40 MB resident and ~400 ms to load.
/// </summary>
public sealed class VoskModel : IDisposable
{
    private readonly Vosk.Model _model;
    private bool _disposed;

    internal VoskModel(Vosk.Model model, string modelDirectory)
    {
        _model = model;
        ModelDirectory = modelDirectory;
    }

    /// <summary>Where it was loaded from. Reported in the tray, never in a request.</summary>
    public string ModelDirectory { get; }

    /// <summary>True until the model is released.</summary>
    public bool IsLoaded => !_disposed;

    internal Vosk.Model Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _model;
        }
    }

    /// <summary>Releases the native model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _model.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The one class that knows how a Vosk model is loaded. Everything else in the agent talks to
/// <see cref="ISpeechEngine"/>, so replacing the recognizer touches this file and the composition
/// root and nothing else.
/// </summary>
/// <remarks>
/// The load is ~400 ms and never runs on the UI thread. It is also the only place a bad model path
/// is caught: the native loader returns a null handle rather than failing, and handing that to a
/// recognizer takes the process down.
/// </remarks>
public sealed class VoskModelLoader
{
    /// <summary>Where the installer puts the small English model, under %LOCALAPPDATA%.</summary>
    public const string DefaultModelFolder = @"WarCommand\models\vosk-small-en-us";

    private readonly ISpeechLog _log;

    /// <summary>A loader that reports through <paramref name="log"/>.</summary>
    public VoskModelLoader(ISpeechLog? log = null) => _log = log ?? NullSpeechLog.Instance;

    /// <summary>The default model directory for this machine's user.</summary>
    public static string DefaultModelDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultModelFolder);

    /// <summary>
    /// Loads the model off the calling thread. Throws
    /// <see cref="SpeechModelUnavailableException"/> rather than returning a half-built model.
    /// </summary>
    public async Task<VoskModel> LoadAsync(string modelDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _log.Note(SpeechEvent.ModelLoadStarted, modelDirectory);

        if (!System.IO.Directory.Exists(modelDirectory))
        {
            _log.Note(SpeechEvent.ModelUnavailable, modelDirectory);
            throw new SpeechModelUnavailableException(
                $"No Vosk model at '{modelDirectory}'. Voice recognition is unavailable until one is installed.");
        }

        var model = await Task.Run(() => Open(modelDirectory), cancellationToken).ConfigureAwait(false);
        _log.Note(SpeechEvent.ModelLoaded, modelDirectory);
        return model;
    }

    private VoskModel Open(string modelDirectory)
    {
        Vosk.Model native;
        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            native = new Vosk.Model(modelDirectory);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _log.Note(SpeechEvent.ModelUnavailable, modelDirectory);
            throw new SpeechModelUnavailableException(
                $"The Vosk model at '{modelDirectory}' failed to load.",
                error);
        }

        if (!HasNativeHandle(native))
        {
            native.Dispose();
            _log.Note(SpeechEvent.ModelUnavailable, modelDirectory);
            throw new SpeechModelUnavailableException(
                $"'{modelDirectory}' is not a Vosk model directory.");
        }

        return new VoskModel(native, modelDirectory);
    }

    /// <summary>
    /// True when the native loader produced a handle. Vosk reports a bad model directory by
    /// returning null rather than throwing, and the next recognizer built on it faults the process,
    /// so the null is caught here. An unrecognised binding layout is treated as loaded, because
    /// refusing on a failed reflection lookup would break voice on a working model.
    /// </summary>
    private static bool HasNativeHandle(Vosk.Model model)
    {
        var field = typeof(Vosk.Model).GetField(
            "swigCPtr",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (field?.GetValue(model) is not System.Runtime.InteropServices.HandleRef handle)
        {
            return true;
        }

        return handle.Handle != IntPtr.Zero;
    }
}
