namespace WarCommand.Agent.Speech.Capture;

/// <summary>
/// The interleaved PCM layout a WASAPI shared-mode stream delivers.
/// </summary>
/// <param name="SampleRateHz">Device rate. Typically 44100 or 48000, never 16000.</param>
/// <param name="Channels">Device channel count. Typically 1 or 2.</param>
/// <param name="BitsPerSample">8, 16, 24 or 32.</param>
/// <param name="IsFloat">True for IEEE float, which is what the shared-mode mix format usually is.</param>
public sealed record PcmFormat(int SampleRateHz, int Channels, int BitsPerSample, bool IsFloat)
{
    /// <summary>Bytes per interleaved frame across every channel.</summary>
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    /// <summary>True when this layout can be decoded.</summary>
    public bool IsSupported =>
        SampleRateHz > 0
        && Channels > 0
        && BytesPerFrame > 0
        && (IsFloat ? BitsPerSample == 32 : BitsPerSample is 8 or 16 or 24 or 32);
}

/// <summary>
/// Downmixes to mono and resamples to 16 kHz, keeping its phase across callbacks.
/// </summary>
/// <remarks>
/// WASAPI shared mode delivers the device's mix format, which is never 16 kHz mono, and the
/// recognizer accepts nothing else. Doing the conversion here rather than asking the audio stack
/// for a format it may refuse keeps capture working on every device, and makes the arithmetic
/// testable with no microphone.
/// </remarks>
public sealed class PcmResampler
{
    private readonly double _ratio;

    /// <summary>Source samples consumed since the last reset. Integer phase, so it cannot drift.</summary>
    private long _consumed;

    /// <summary>Output samples produced since the last reset.</summary>
    private long _emitted;

    private double _previous;

    /// <summary>Converts from <paramref name="source"/> to <see cref="AudioBuffer.SampleRateHz"/>.</summary>
    public PcmResampler(PcmFormat source)
        : this(source, AudioBuffer.SampleRateHz)
    {
    }

    /// <summary>Converts from <paramref name="source"/> to <paramref name="targetSampleRateHz"/>.</summary>
    public PcmResampler(PcmFormat source, int targetSampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetSampleRateHz, 1);

        if (!source.IsSupported)
        {
            throw new NotSupportedException(
                $"Unsupported capture format: {source.BitsPerSample} bit "
                + $"{(source.IsFloat ? "float" : "integer")}, {source.Channels} channels.");
        }

        Source = source;
        TargetSampleRateHz = targetSampleRateHz;
        _ratio = targetSampleRateHz / (double)source.SampleRateHz;
    }

    /// <summary>The device layout being read.</summary>
    public PcmFormat Source { get; }

    /// <summary>The rate being written. 16 kHz for the recognizer.</summary>
    public int TargetSampleRateHz { get; }

    /// <summary>The most output samples <paramref name="sourceBytes"/> can produce.</summary>
    public int MaxOutputFor(int sourceBytes) =>
        (int)Math.Ceiling(sourceBytes / (double)Source.BytesPerFrame * _ratio) + 1;

    /// <summary>
    /// Converts one capture callback's bytes into mono 16-bit samples and returns how many were
    /// written. Output stops at <paramref name="destination"/>'s length.
    /// </summary>
    public int Convert(ReadOnlySpan<byte> source, Span<short> destination)
    {
        var frameBytes = Source.BytesPerFrame;
        var frames = source.Length / frameBytes;
        var written = 0;
        long sourceRate = Source.SampleRateHz;
        long targetRate = TargetSampleRateHz;

        for (var frame = 0; frame < frames; frame++)
        {
            var mono = Downmix(source.Slice(frame * frameBytes, frameBytes));
            var index = _consumed;
            _consumed++;

            // Output n sits at source position n * sourceRate / targetRate. Comparing that
            // position as integers rather than accumulating a fractional step is what keeps a
            // 44.1 kHz stream from losing a sample every few callbacks.
            while (_emitted * sourceRate <= index * targetRate)
            {
                if (written >= destination.Length)
                {
                    return written;
                }

                var position = _emitted * (double)sourceRate / targetRate;
                var fraction = position - (index - 1);
                destination[written++] = Clamp(_previous + ((mono - _previous) * fraction));
                _emitted++;
            }

            _previous = mono;
        }

        return written;
    }

    /// <summary>Drops the carried phase. Called when the device or the format changes.</summary>
    public void Reset()
    {
        _consumed = 0;
        _emitted = 0;
        _previous = 0;
    }

    /// <summary>
    /// Scales -1..1 back to 16-bit. The divisor on the way in and the multiplier on the way out are
    /// both 32768, so a 16-bit source round-trips exactly instead of losing a least significant bit
    /// on every sample.
    /// </summary>
    private static short Clamp(double value) =>
        (short)Math.Clamp(Math.Round(value * 32768.0), short.MinValue, short.MaxValue);

    /// <summary>One interleaved frame as a mono sample in -1..1.</summary>
    private double Downmix(ReadOnlySpan<byte> frame)
    {
        var bytes = Source.BitsPerSample / 8;
        double total = 0;
        for (var channel = 0; channel < Source.Channels; channel++)
        {
            total += Sample(frame.Slice(channel * bytes, bytes));
        }

        return total / Source.Channels;
    }

    private double Sample(ReadOnlySpan<byte> sample) => Source.BitsPerSample switch
    {
        8 => (sample[0] - 128) / 128.0,
        16 => BitConverter.ToInt16(sample) / 32768.0,
        24 => ((sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608.0),
        32 when Source.IsFloat => BitConverter.ToSingle(sample),
        _ => BitConverter.ToInt32(sample) / 2147483648.0,
    };
}
