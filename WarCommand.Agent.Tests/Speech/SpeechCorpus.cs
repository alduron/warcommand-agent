using System.Buffers.Binary;
using System.Text.Json;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Speech;
using WarCommand.Agent.Speech.Recognition;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>One row of the recorded-buffer corpus.</summary>
internal sealed record GridUtteranceCase
{
    public string Id { get; init; } = string.Empty;

    /// <summary>File name under Speech/buffers. 16 kHz mono 16-bit PCM.</summary>
    public string Wav { get; init; } = string.Empty;

    /// <summary>What was said, for the failure message.</summary>
    public string Said { get; init; } = string.Empty;

    public decimal X { get; init; }

    public decimal Y { get; init; }

    /// <summary>Floor for the minimum per-token digit confidence. Null takes the manifest default.</summary>
    public decimal? MinDigitConfidence { get; init; }

    /// <summary>Set only on the marker row that stands in for an absent corpus or model.</summary>
    public string? Unavailable { get; init; }
}

/// <summary>Envelope of Speech/buffers/manifest.json. Unknown fields are ignored.</summary>
internal sealed record GridUtteranceManifest
{
    public decimal? DefaultMinDigitConfidence { get; init; }

    public IReadOnlyList<string> CorpusRequirements { get; init; } = [];

    public IReadOnlyList<GridUtteranceCase> Utterances { get; init; } = [];
}

/// <summary>
/// The recorded-buffer corpus and the engine it is played through.
/// </summary>
/// <remarks>
/// Excluding timing-sensitive audio capture is not the same as excluding recognizer accuracy.
/// WASAPI, device switching and PTT timing are genuinely untestable in CI; whether Vosk hears
/// "eight five" as "three five" through our grammar is neither. That question needs a real model
/// and real recordings, and when either is absent this class says so by name rather than
/// substituting something that would make the suite look like it had measured something.
/// </remarks>
internal static class SpeechCorpus
{
    /// <summary>Points the harness at a model outside %LOCALAPPDATA%, for CI and for a dev box.</summary>
    public const string ModelPathVariable = "WARCOMMAND_VOSK_MODEL";

    /// <summary>The id of the marker row used when nothing can actually be measured.</summary>
    public const string UnavailableId = "corpus-unavailable";

    private static readonly Lazy<GridUtteranceManifest> LazyManifest = new(LoadManifest);
    private static readonly Lazy<string?> LazyModelDirectory = new(FindModel);
    private static readonly Lazy<VoskSpeechEngine?> LazyEngine = new(OpenEngine);
    private static readonly Lazy<IReadOnlyList<GridUtteranceCase>> LazyCases = new(BuildCases);

    /// <summary>The directory holding the committed wav buffers.</summary>
    public static string BuffersDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Speech", "buffers");

    /// <summary>What a real corpus still needs, read from the manifest rather than from a comment.</summary>
    public static IReadOnlyList<string> Requirements => LazyManifest.Value.CorpusRequirements;

    /// <summary>Rows as declared in the manifest, before availability is considered.</summary>
    public static IReadOnlyList<GridUtteranceCase> Declared => LazyManifest.Value.Utterances;

    /// <summary>The resolved model directory, or null when no model is installed.</summary>
    public static string? ModelDirectory => LazyModelDirectory.Value;

    /// <summary>The engine, or null when no model is installed.</summary>
    public static ISpeechEngine? Engine => LazyEngine.Value;

    /// <summary>
    /// Runnable rows, or a single marker row naming why nothing is runnable. Never empty: a theory
    /// with no rows is reported as an error rather than as the honest "there is no corpus".
    /// </summary>
    public static IReadOnlyList<GridUtteranceCase> Cases => LazyCases.Value;

    /// <summary>True when a real measurement happened. False means the theory asserted nothing about Vosk.</summary>
    public static bool IsMeasuring => Cases.Count > 0 && Cases[0].Unavailable is null;

    /// <summary>One theory row per case id.</summary>
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var id in Cases.Select(c => c.Id))
        {
            data.Add(id);
        }

        return data;
    }

    public static GridUtteranceCase Case(string id) =>
        Cases.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"the corpus has no case '{id}'");

    /// <summary>The floor a row's minimum digit confidence must clear.</summary>
    public static decimal FloorFor(GridUtteranceCase testCase) =>
        testCase.MinDigitConfidence
        ?? LazyManifest.Value.DefaultMinDigitConfidence
        ?? ContractFixtures.Profile.PointConfidence.Warn;

    /// <summary>Reads one committed buffer. Refuses anything that is not 16 kHz mono 16-bit PCM.</summary>
    public static AudioBuffer Load(string wavFileName)
    {
        var path = Path.Combine(BuffersDirectory, wavFileName);
        var samples = ReadWav(File.ReadAllBytes(path), path);
        var buffer = new AudioBuffer(Math.Min(Math.Max(samples.Length, 1), AudioBuffer.MaxSamples));
        buffer.Append(samples);
        return buffer;
    }

    private static short[] ReadWav(byte[] bytes, string path)
    {
        if (bytes.Length < 12
            || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidOperationException($"{path} is not a RIFF/WAVE file");
        }

        var offset = 12;
        short channels = 0;
        var sampleRate = 0;
        short bits = 0;

        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;

            if (id == "fmt ")
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id == "data")
            {
                if (channels != 1 || sampleRate != AudioBuffer.SampleRateHz || bits != 16)
                {
                    throw new InvalidOperationException(
                        $"{path} is {sampleRate} Hz {channels}ch {bits}-bit; the corpus is 16 kHz mono 16-bit");
                }

                var count = Math.Min(size, bytes.Length - body) / sizeof(short);
                var samples = new short[count];
                Buffer.BlockCopy(bytes, body, samples, 0, count * sizeof(short));
                return samples;
            }

            offset = body + size + (size % 2);
        }

        throw new InvalidOperationException($"{path} carries no data chunk");
    }

    private static GridUtteranceManifest LoadManifest()
    {
        var path = Path.Combine(BuffersDirectory, "manifest.json");
        return (File.Exists(path)
                   ? JsonSerializer.Deserialize<GridUtteranceManifest>(File.ReadAllText(path), AgentJson.Options)
                   : null)
               ?? new GridUtteranceManifest();
    }

    private static string? FindModel()
    {
        var configured = Environment.GetEnvironmentVariable(ModelPathVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        var installed = VoskModelLoader.DefaultModelDirectory;
        return Directory.Exists(installed) ? installed : null;
    }

    private static VoskSpeechEngine? OpenEngine()
    {
        if (LazyModelDirectory.Value is not { } directory)
        {
            return null;
        }

        var model = new VoskModelLoader()
            .LoadAsync(directory, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return new VoskSpeechEngine(model);
    }

    private static List<GridUtteranceCase> BuildCases()
    {
        var declared = LazyManifest.Value.Utterances
            .Where(c => !string.IsNullOrWhiteSpace(c.Wav) && File.Exists(Path.Combine(BuffersDirectory, c.Wav)))
            .ToList();

        if (declared.Count == 0)
        {
            return [Unavailable(
                "no recorded buffers are committed under WarCommand.Agent.Tests/Speech/buffers, so nothing "
                + "about recognizer accuracy has been measured. See corpus_requirements in manifest.json.")];
        }

        if (LazyModelDirectory.Value is null)
        {
            return [Unavailable(
                $"no Vosk model is installed. Set {ModelPathVariable} or install one at "
                + $"'{VoskModelLoader.DefaultModelDirectory}'. The corpus is committed but was not played "
                + "through a recognizer, so nothing about recognizer accuracy has been measured.")];
        }

        return declared;
    }

    private static GridUtteranceCase Unavailable(string reason) =>
        new() { Id = UnavailableId, Unavailable = reason };
}
