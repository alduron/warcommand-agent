using System.Globalization;
using System.Text;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Speech.Readback;

/// <summary>
/// Reads a coordinate back, digit by digit, for the one source where that is worth a second.
/// </summary>
/// <remarks>
/// Readback defaults on for <c>spoken_grid</c> points only, from
/// <c>point_confidence.tts_readback_default</c> in the served profile. A spoken grid is the one
/// case where the user said the number and can hear that it came back wrong; for a
/// <c>map_readout</c> point they never said one, so reading it back tests nothing and costs a
/// second of the glance budget on every request.
/// </remarks>
public sealed class GridReadback
{
    /// <summary>The source id a spoken grid writes to <c>request_points.source</c>.</summary>
    public const string SpokenGridSource = "spoken_grid";

    /// <summary>The profile value that turns readback on for spoken grids and nothing else.</summary>
    public const string SpokenGridOnly = "spoken_grid_only";

    private static readonly string[] DigitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private readonly ITextToSpeech _tts;
    private readonly ISpeechLog _log;
    private readonly string _policy;

    /// <summary>Takes its default from the served profile, never from a constant here.</summary>
    public GridReadback(ITextToSpeech tts, GameProfile profile, ISpeechLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(profile);

        _tts = tts;
        _log = log ?? NullSpeechLog.Instance;
        _policy = profile.PointConfidence.TtsReadbackDefault ?? SpokenGridOnly;
        Enabled = true;
    }

    /// <summary>The user's toggle, over the top of the per-source policy.</summary>
    public bool Enabled { get; set; }

    /// <summary>The policy string the profile carried.</summary>
    public string Policy => _policy;

    /// <summary>True when readback is on for points from this source.</summary>
    public bool DefaultsOnFor(string? sourceId) => _policy switch
    {
        SpokenGridOnly => string.Equals(sourceId, SpokenGridSource, StringComparison.Ordinal),
        "all" => true,
        _ => false,
    };

    /// <summary>
    /// Reads the point back if its source and the toggle both allow it. Returns whether it spoke,
    /// so a caller can extend the preview hold only when something is actually being said.
    /// </summary>
    public bool Read(MapPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (!Enabled || !_tts.IsAvailable || !DefaultsOnFor(point.Source))
        {
            return false;
        }

        _tts.Speak(Spell(point));
        _log.Note(SpeechEvent.ReadbackSpoken, point.Source);
        return true;
    }

    /// <summary>Both axes as spoken digits: 85.53, 69.42 becomes the eight-token form.</summary>
    public static string Spell(MapPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return $"{Spell(point.X)}, {Spell(point.Y)}";
    }

    /// <summary>
    /// One axis, digit by digit with 'point' for the separator. Military convention, and the way
    /// the grid was spoken in the first place.
    /// </summary>
    public static string Spell(decimal value)
    {
        var text = value.ToString("0.00", CultureInfo.InvariantCulture);
        var spoken = new StringBuilder();

        foreach (var character in text)
        {
            if (spoken.Length > 0)
            {
                spoken.Append(' ');
            }

            if (character is >= '0' and <= '9')
            {
                spoken.Append(DigitWords[character - '0']);
            }
            else if (character == '.')
            {
                spoken.Append("point");
            }
            else if (character == '-')
            {
                spoken.Append("minus");
            }
        }

        return spoken.ToString();
    }
}
