namespace WarCommand.Agent.Speech;

/// <summary>
/// One PTT hold's audio: 16 kHz mono, signed 16-bit, bounded at 8 seconds and zeroed on release.
/// </summary>
/// <remarks>
/// The type carries no path, no stream and no serializer, so there is no member through which audio
/// could reach a disk or a socket even by accident. That is structural rather than a promise:
/// <c>WarCommand.Agent.Tests.Speech.SpeechIsolationTests</c> asserts both the shape of this type and
/// that the whole assembly references no file-write and no network API.
/// </remarks>
public sealed class AudioBuffer : IDisposable
{
    /// <summary>The one rate the recognizer accepts. Capture resamples to it before it gets here.</summary>
    public const int SampleRateHz = 16_000;

    /// <summary>The hard cap. A PTT key that sticks must not allocate without limit.</summary>
    public const int MaxDurationSeconds = 8;

    /// <summary>8 seconds at 16 kHz mono.</summary>
    public const int MaxSamples = SampleRateHz * MaxDurationSeconds;

    private readonly short[] _samples;
    private int _length;
    private int _peak;
    private bool _disposed;

    /// <summary>A buffer at the full 8 second cap.</summary>
    public AudioBuffer()
        : this(MaxSamples)
    {
    }

    /// <summary>A buffer of <paramref name="capacitySamples"/>, which may never exceed the cap.</summary>
    public AudioBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacitySamples, MaxSamples);
        _samples = new short[capacitySamples];
    }

    /// <summary>Samples this buffer can hold. Never above <see cref="MaxSamples"/>.</summary>
    public int Capacity => _samples.Length;

    /// <summary>Samples held.</summary>
    public int Length => _length;

    /// <summary>True once the cap is reached. Further audio is dropped, never queued.</summary>
    public bool IsFull => _length >= _samples.Length;

    /// <summary>How much audio is held.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_length / (double)SampleRateHz);

    /// <summary>What is held. A span, so the samples never escape as an array reference.</summary>
    public ReadOnlySpan<short> Samples => _samples.AsSpan(0, _length);

    /// <summary>
    /// Peak level of what is held, in dBFS, computed as samples arrive. This is the number the
    /// silent-hold check reads, and it is discarded with the buffer.
    /// </summary>
    public double PeakDbfs =>
        _peak == 0 ? double.NegativeInfinity : 20.0 * Math.Log10(_peak / (double)short.MaxValue);

    /// <summary>True while nothing but digital silence has been written.</summary>
    public bool IsSilent => _peak == 0;

    /// <summary>True once this buffer has been released and zeroed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The backing array, for the recognizer only. Never public, never copied out.</summary>
    internal short[] Storage => _samples;

    /// <summary>
    /// Appends samples up to the cap and returns how many were taken. The overflow is dropped:
    /// a stuck key must not grow this buffer.
    /// </summary>
    public int Append(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var room = _samples.Length - _length;
        var taken = Math.Min(room, samples.Length);
        if (taken <= 0)
        {
            return 0;
        }

        samples[..taken].CopyTo(_samples.AsSpan(_length));
        for (var i = 0; i < taken; i++)
        {
            var magnitude = Math.Abs((int)samples[i]);
            if (magnitude > _peak)
            {
                _peak = Math.Min(magnitude, short.MaxValue);
            }
        }

        _length += taken;
        return taken;
    }

    /// <summary>Appends one sample. Returns false when the cap is already reached.</summary>
    public bool Append(short sample)
    {
        Span<short> one = [sample];
        return Append(one) == 1;
    }

    /// <summary>
    /// Zeroes every sample and the peak and rewinds to empty. Reuse is deliberate: one buffer per
    /// agent means the 8 seconds are allocated once and never grow.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_samples);
        _length = 0;
        _peak = 0;
    }

    /// <summary>Zeroes the buffer and releases it. Audio must not linger in memory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_samples);
        _length = 0;
        _peak = 0;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
