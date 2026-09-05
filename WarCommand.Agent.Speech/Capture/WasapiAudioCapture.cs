using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace WarCommand.Agent.Speech.Capture;

/// <summary>
/// WASAPI shared-mode capture on the configured input device, downmixed to 16 kHz mono.
/// </summary>
/// <remarks>
/// <para>
/// Device loss is handled while the match is running. The person who needs this product most is
/// already running a headset plus a stream mic, and unplugging one must not silently end
/// recognition: capture falls back to Default, names what happened, and turns the tray amber.
/// </para>
/// <para>
/// With no input device at all the state is <see cref="AudioCaptureState.NoInputDevice"/> and the
/// PTT key does nothing rather than appearing to work. The failure mode this is designed against is
/// an agent that looks healthy and hears nothing.
/// </para>
/// </remarks>
public sealed class WasapiAudioCapture : IAudioCapture, IAudioDeviceCatalog
{
    private readonly ISpeechLog _log;

    /// <summary>Guards open, close and health. Never held across a wait on the capture thread.</summary>
    private readonly object _lifecycle = new();

    /// <summary>Guards the conversion path. The only lock the capture thread ever takes.</summary>
    private readonly object _data = new();

    private readonly MMDeviceEnumerator _enumerator;
    private readonly EndpointWatcher _watcher;

    private WasapiCapture? _capture;
    private PcmResampler? _resampler;
    private short[] _scratch = [];
    private AudioBuffer? _destination;
    private string? _requestedDeviceId;
    private double _levelPeak;
    private bool _disposed;

    /// <summary>Opens the enumerator and subscribes to endpoint changes. Does not start capture.</summary>
    public WasapiAudioCapture(ISpeechLog? log = null)
    {
        _log = log ?? NullSpeechLog.Instance;
        _enumerator = new MMDeviceEnumerator();
        _watcher = new EndpointWatcher(this);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
        Health = new AudioCaptureHealth(AudioCaptureState.Closed, null, null);
    }

    /// <inheritdoc />
    public event EventHandler<AudioCaptureHealth>? HealthChanged;

    /// <inheritdoc />
    public AudioDevice? Device { get; private set; }

    /// <inheritdoc />
    public AudioCaptureHealth Health { get; private set; }

    /// <inheritdoc />
    public bool IsHolding
    {
        get
        {
            lock (_data)
            {
                return _destination is not null;
            }
        }
    }

    /// <inheritdoc />
    public double LevelDbfs
    {
        get
        {
            lock (_data)
            {
                return _levelPeak <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(Math.Min(_levelPeak, 1.0));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioDevice> Inputs => Enumerate(DataFlow.Capture, Role.Communications);

    /// <inheritdoc />
    public AudioDevice? DefaultInput => Inputs.FirstOrDefault(d => d.IsDefault);

    /// <summary>
    /// Render endpoints, for the sound output list. Console rather than Communications: readback
    /// and the board's ticks follow where the user hears the game, not where their comms app is.
    /// </summary>
    public IReadOnlyList<AudioDevice> Outputs => Enumerate(DataFlow.Render, Role.Console);

    /// <inheritdoc />
    public AudioDevice? DefaultOutput => Outputs.FirstOrDefault(d => d.IsDefault);

    /// <summary>
    /// Active endpoints for one direction, Default first. Opens nothing: this reads the shell's
    /// device list, which is the same list Windows' own sound settings show.
    /// </summary>
    private IReadOnlyList<AudioDevice> Enumerate(DataFlow flow, Role role)
    {
        var defaultId = DefaultEndpointId(flow, role);
        var devices = new List<AudioDevice>();

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                devices.Add(new AudioDevice(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A machine mid-driver-install enumerates nothing rather than throwing at a settings
            // window. An empty list renders as Default only, which is what it was before.
            return [];
        }

        return [.. devices
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCulture)];
    }

    /// <inheritdoc />
    public void Open(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycle)
        {
            _requestedDeviceId = deviceId;
            OpenLocked(deviceId, fellBack: false);
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (_lifecycle)
        {
            var name = Device?.FriendlyName;
            Teardown();
            Report(new AudioCaptureHealth(AudioCaptureState.Closed, name, null));
            _log.Note(SpeechEvent.CaptureClosed, name);
        }
    }

    /// <inheritdoc />
    public AudioChunkHandler? OnChunk { get; set; }

    /// <inheritdoc />
    public void BeginHold(AudioBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Reset();
        lock (_data)
        {
            _destination = destination;
        }
    }

    /// <inheritdoc />
    public void EndHold()
    {
        lock (_data)
        {
            _destination = null;
        }
    }

    /// <summary>Stops capture, zeroes the scratch buffer, and releases the enumerator.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lifecycle)
        {
            Teardown();
        }

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_watcher);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // The enumerator is going away with us; a failed unregister changes nothing.
        }

        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    private static PcmFormat FormatOf(WaveFormat format)
    {
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            try
            {
                encoding = extensible.ToStandardWaveFormat().Encoding;
            }
            catch (InvalidOperationException)
            {
                // An extensible format naming a subformat NAudio does not map. 32-bit shared-mode
                // mix formats are IEEE float in practice, and anything else is read as integer.
                encoding = format.BitsPerSample == 32 ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
            }
        }

        return new PcmFormat(
            format.SampleRate,
            format.Channels,
            format.BitsPerSample,
            encoding == WaveFormatEncoding.IeeeFloat);
    }

    private string? DefaultEndpointId() => DefaultEndpointId(DataFlow.Capture, Role.Communications);

    private string? DefaultEndpointId(DataFlow flow, Role role)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, role)
                ? _enumerator.GetDefaultAudioEndpoint(flow, role).ID
                : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private MMDevice? Resolve(string? deviceId)
    {
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                var named = _enumerator.GetDevice(deviceId);
                if (named is not null && named.State == DeviceState.Active)
                {
                    return named;
                }
            }

            return _enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private void OpenLocked(string? deviceId, bool fellBack)
    {
        Teardown();

        var device = Resolve(deviceId);
        if (device is null)
        {
            Device = null;
            _log.Note(SpeechEvent.NoInputDevice);
            Report(new AudioCaptureHealth(
                AudioCaptureState.NoInputDevice,
                null,
                "No input device. Voice is unavailable until one is connected."));
            return;
        }

        try
        {
            var capture = new WasapiCapture(device);
            var format = FormatOf(capture.WaveFormat);
            var resampler = new PcmResampler(format);

            lock (_data)
            {
                _resampler = resampler;
                _scratch = new short[resampler.MaxOutputFor(format.SampleRateHz / 5 * format.BytesPerFrame)];
            }

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _capture = capture;
            Device = new AudioDevice(
                device.ID,
                device.FriendlyName,
                string.Equals(device.ID, DefaultEndpointId(), StringComparison.Ordinal));

            _log.Note(SpeechEvent.CaptureOpened, device.FriendlyName);
            Report(fellBack
                ? new AudioCaptureHealth(
                    AudioCaptureState.FellBackToDefault,
                    device.FriendlyName,
                    $"The selected microphone went away. Using {device.FriendlyName}.")
                : new AudioCaptureHealth(AudioCaptureState.Running, device.FriendlyName, null));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Teardown();
            Device = null;
            Report(new AudioCaptureHealth(
                AudioCaptureState.Failed,
                device.FriendlyName,
                $"{device.FriendlyName} could not be opened: {error.Message}"));
        }
    }

    /// <summary>
    /// Detaches, stops and zeroes. The conversion state is cleared under the data lock first, so
    /// the capture thread can never sit inside <see cref="OnDataAvailable"/> waiting on the
    /// lifecycle lock while this method waits on the capture thread.
    /// </summary>
    private void Teardown()
    {
        var capture = _capture;
        _capture = null;

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
        }

        lock (_data)
        {
            _destination = null;
            _resampler = null;
            _levelPeak = 0;

            // Converted samples must not linger any longer than the buffer's do.
            Array.Clear(_scratch);
            _scratch = [];
        }

        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // The endpoint is already gone. Disposing is still correct.
        }

        capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_data)
        {
            if (_resampler is null || args.BytesRecorded <= 0)
            {
                return;
            }

            var needed = _resampler.MaxOutputFor(args.BytesRecorded);
            if (_scratch.Length < needed)
            {
                Array.Clear(_scratch);
                _scratch = new short[needed];
            }

            var written = _resampler.Convert(args.Buffer.AsSpan(0, args.BytesRecorded), _scratch);
            if (written <= 0)
            {
                return;
            }

            var peak = 0;
            for (var i = 0; i < written; i++)
            {
                var magnitude = Math.Abs((int)_scratch[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            _levelPeak = Math.Max(_levelPeak * 0.7, peak / (double)short.MaxValue);

            if (_destination is { } destination)
            {
                destination.Append(_scratch.AsSpan(0, written));

                // The streaming tap, and only while a hold is actually open. It copies and
                // returns: this is the capture thread inside the data lock, so anything that
                // decodes here would starve capture and Windows would drop buffers.
                OnChunk?.Invoke(_scratch.AsSpan(0, written));
            }

            Array.Clear(_scratch, 0, written);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null || _disposed)
        {
            return;
        }

        _log.Note(SpeechEvent.CaptureDeviceLost, Device?.FriendlyName);
        FallBackToDefault();
    }

    /// <summary>
    /// Reopens on Default after losing the configured device. Runs off the notification thread:
    /// WASAPI endpoint callbacks arrive on a COM thread that must not be blocked.
    /// </summary>
    private void FallBackToDefault() => Task.Run(() =>
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _log.Note(SpeechEvent.CaptureFellBackToDefault);
            OpenLocked(null, fellBack: true);
        }
    });

    private void Report(AudioCaptureHealth health)
    {
        Health = health;
        HealthChanged?.Invoke(this, health);
    }

    /// <summary>
    /// Endpoint change notifications. Nested and private so the five COM callbacks are not part of
    /// this assembly's surface.
    /// </summary>
    private sealed class EndpointWatcher(WasapiAudioCapture owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active && Affects(deviceId))
            {
                owner.FallBackToDefault();
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            // A new endpoint never steals an open one. The user picks it from the tray.
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (Affects(deviceId))
            {
                owner.FallBackToDefault();
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Follow the default only when the user asked for the default and capture is open.
            if (flow == DataFlow.Capture && owner._requestedDeviceId is null && owner._capture is not null)
            {
                owner.FallBackToDefault();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Volume and name changes do not affect the stream.
        }

        private bool Affects(string deviceId) =>
            owner.Device is { } device && string.Equals(device.Id, deviceId, StringComparison.Ordinal);
    }
}
