using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>Process names and window polling. A list and an interval, never a literal.</summary>
public sealed record GameSection
{
    public string Display { get; init; } = string.Empty;

    /// <summary>Every executable name the game may ship under. A launcher may sit in front of it.</summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    public int? SteamAppId { get; init; }

    public string? MinSupportedBuild { get; init; }

    public required int WindowPollMs { get; init; }
}

/// <summary>Headcount context for the overlay header and after-action. Nothing enforces these.</summary>
public sealed record TeamsSection
{
    public int TeamsPerMatch { get; init; }

    public IReadOnlyList<string> TeamNames { get; init; } = [];

    public int PlayersPerTeam { get; init; }

    public int PlayersPerMatch { get; init; }
}

/// <summary>
/// How the map coordinate readout is found and trusted. There is deliberately no rectangle here:
/// the readout is anchored to the moving crosshair, so the panel is scanned for
/// <see cref="Pattern"/>.
/// </summary>
public sealed record MapReadoutSection
{
    public string? Confidence { get; init; }

    public required string Pattern { get; init; }

    public required string AnchoredPattern { get; init; }

    public required IReadOnlyList<string> Glyphs { get; init; }

    /// <summary>All channels at or above this value count as readout text.</summary>
    public required int NearWhiteThreshold { get; init; }

    /// <summary>
    /// Widest gap between two characters still treated as one run. Too small and a readout arrives
    /// at the decoder in fragments; the decoder cannot recover from that.
    /// </summary>
    public int GlyphGapPx { get; init; } = 12;

    /// <summary>
    /// Largest coordinate any map can produce. A decode above it is a misread, not a point.
    /// </summary>
    public decimal CoordinateSanityMax { get; init; } = 400m;

    public required int ExpectedMatchesPerFrame { get; init; }

    /// <summary>
    /// How far from the crosshair the readout may sit, and the half-size of the captured region.
    /// </summary>
    /// <remarks>
    /// Anchored to the CURSOR, never to the screen. A fixed centre panel clipped the readout
    /// whenever the cursor neared the edge of the map, so plainly readable numbers were never
    /// captured at all.
    /// </remarks>
    public int SearchRadiusPx { get; init; } = 420;

    /// <summary>
    /// Thresholds to try in order until a complete pair decodes. Empty falls back to the single
    /// <see cref="NearWhiteThreshold"/>.
    /// </summary>
    /// <remarks>
    /// The readout dims near the edges of the map, so one fixed threshold reads the middle and goes
    /// blind at the border on text a human finds perfectly legible. The black outline around the
    /// glyphs is what makes dropping the threshold safe.
    /// </remarks>
    public IReadOnlyList<int> NearWhiteLadder { get; init; } = [];

    /// <summary>'scan_panel_for_pattern'. Never a fixed rectangle.</summary>
    public string ScanStrategy { get; init; } = "scan_panel_for_pattern";

    public required int CorroborationFrames { get; init; }

    public required int CorroborationWindowMs { get; init; }

    /// <summary>A point further than this from the requester's last accepted one needs a confirm.</summary>
    public required decimal MaxJumpUnits { get; init; }

    public required int MaxJumpWindowS { get; init; }

    /// <summary>A requester later seen further than this from their own open row marks it REQUESTER MOVED.</summary>
    public required decimal MovedThresholdUnits { get; init; }

    /// <summary>Any glyph below this margin rejects the whole readout, never the single character.</summary>
    public required decimal GlyphMarginFloor { get; init; }

    /// <summary>The glyph atlas is pre-rendered at each of these.</summary>
    public required IReadOnlyList<decimal> UiScales { get; init; }

    /// <summary>How the atlas is rendered before matching. The typeface is a fact about the game.</summary>
    public AtlasSection Atlas { get; init; } = new();

    /// <summary>Null means nobody has established it. The agent must not assume either way.</summary>
    public bool? ZoomIndependent { get; init; }
}

/// <summary>
/// The typeface the atlas renders before it is matched against a captured run. A candidate list
/// rather than one name: the reader renders each, scores it, and keeps the best.
/// </summary>
public sealed record AtlasSection
{
    public IReadOnlyList<string> FontCandidates { get; init; } = [];

    public bool FontBold { get; init; }

    /// <summary>
    /// What one more character must earn before the solver accepts it. Near the typical per-glyph
    /// match score: too low and a run splits into extra thin glyphs, too high and real ones vanish.
    /// </summary>
    public double GlyphCost { get; init; } = 0.72;

    /// <summary>
    /// Character advance as a fraction of the line height, smallest and largest. A split implying a
    /// pitch outside this is not a reading of this font, whatever it scores.
    /// </summary>
    public double PitchRatioMin { get; init; } = 0.52;

    public double PitchRatioMax { get; init; } = 0.74;

    /// <summary>
    /// The game's own glyph shapes, keyed by character, each as a near-white mask one string per
    /// row. Present means match against these and ignore the font candidates entirely.
    /// </summary>
    /// <remarks>
    /// Wardogs ships its own typeface. No installed face reproduces the readout, so rendering an
    /// approximation picked wrong digits however the solver was tuned. These are cut from runs
    /// whose text a human read off the screen, so they match the real thing exactly.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Learned { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public string? Confidence { get; init; }
}

/// <summary>A region of the client rect, as fractions of its width and height.</summary>
public sealed record PanelRect
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; } = 1;

    public double Height { get; init; } = 1;
}

/// <summary>The two thresholds that make request_points.confidence mean something.</summary>
public sealed record PointConfidenceSection
{
    /// <summary>Below this the agent rejects the point. Nothing reaches the server.</summary>
    public required decimal Floor { get; init; }

    /// <summary>Between floor and warn the row renders LOW CONF dim.</summary>
    public required decimal Warn { get; init; }

    /// <summary>'spoken_grid_only'. Readback is off for map_readout points.</summary>
    public string? TtsReadbackDefault { get; init; }
}

/// <summary>Audio properties of the capture device. Nothing here is ever written or sent.</summary>
public sealed record SpeechSection
{
    /// <summary>A hold whose peak energy never crossed this is counted as silent.</summary>
    public required decimal NoiseFloorDbfs { get; init; }

    /// <summary>Consecutive silent holds before NO AUDIO FROM &lt;device&gt;. Consecutive, not cumulative.</summary>
    public required int SilentHoldsBeforeWarning { get; init; }
}

/// <summary>One map, with its own coordinate scale. Per map, never global.</summary>
public sealed record MapDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public string? Region { get; init; }

    public int SizeM { get; init; }

    public decimal CoordMin { get; init; }

    public decimal CoordMax { get; init; }

    /// <summary>Overrides the fallback in ballistics.json. Inferred, not measured.</summary>
    public required decimal UnitsToMeters { get; init; }

    public string? UnitsConfidence { get; init; }

    public string? Heightmap { get; init; }
}

/// <summary>Tails the newest game log for a connect line. Fails to null, never to a guess.</summary>
public sealed record LogTailDetectorSection
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> SearchPaths { get; init; } = [];

    public string FileGlob { get; init; } = "*.log";

    public IReadOnlyList<string> ConnectPatterns { get; init; } = [];
}

/// <summary>Server-side only. The agent never ships or initialises the Steamworks SDK.</summary>
public sealed record SteamDetectorSection
{
    public bool Enabled { get; init; }

    public int PollSeconds { get; init; }

    public IReadOnlyList<string> FieldPriority { get; init; } = [];
}

/// <summary>Which server the player is on. Both automatic sources are unverified.</summary>
public sealed record DetectionSection
{
    public LogTailDetectorSection LogTail { get; init; } = new();

    public SteamDetectorSection Steam { get; init; } = new();
}

/// <summary>A clipboard text format for pasting a coordinate into game chat.</summary>
public sealed record HandoffFormat
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    /// <summary>Placeholders in braces: {x}, {y}, {ticket}.</summary>
    public required string Template { get; init; }

    /// <summary>Substitutes every placeholder present in <paramref name="values"/>. Never presses a key.</summary>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rendered = Template;
        foreach (var (key, value) in values)
        {
            rendered = rendered.Replace($"{{{key}}}", value, StringComparison.Ordinal);
        }

        return rendered;
    }
}

/// <summary>
/// The chat text format, which is a fact about the game rather than about our vocabulary.
/// WarCommand writes the clipboard and never synthesises the paste.
/// </summary>
public sealed record CoordinateHandoffSection
{
    public bool ClipboardEnabledDefault { get; init; } = true;

    public required IReadOnlyList<HandoffFormat> Formats { get; init; }

    public required string DefaultFormat { get; init; }

    public HandoffFormat? Format(string id) =>
        Formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}

/// <summary>A coordinate source the agent may ask.</summary>
public sealed record CoordinateSourceDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public string? Gives { get; init; }

    public string? Status { get; init; }
}

/// <summary>
/// Which sources are asked and in what order. Adding a source is a class plus two lines here.
/// </summary>
public sealed record CoordinateSourcesSection
{
    /// <summary>Sources the agent may ask at all.</summary>
    public required IReadOnlyList<string> Enabled { get; init; }

    /// <summary>Ask order. First non-null answer wins.</summary>
    public required IReadOnlyList<string> Priority { get; init; }

    public IReadOnlyList<CoordinateSourceDef> Known { get; init; } = [];

    public bool IsEnabled(string sourceId) => Enabled.Contains(sourceId, StringComparer.Ordinal);

    /// <summary>
    /// Rank for <see cref="Abstractions.ICoordinateSource.Priority"/>: 0 is asked first. A source
    /// absent from the priority list ranks after every listed one.
    /// </summary>
    public int PriorityOf(string sourceId)
    {
        for (var i = 0; i < Priority.Count; i++)
        {
            if (string.Equals(Priority[i], sourceId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}

/// <summary>A kill switch per subsystem. Flipping one stops every agent on its next fetch.</summary>
public sealed record FeatureFlagsSection
{
    public bool Capture { get; init; }

    public bool FireSolutions { get; init; }

    public bool LogDetector { get; init; }

    public bool SteamDetector { get; init; }

    public bool CoordinateClipboard { get; init; }

    public bool Voice { get; init; }

    /// <summary>Looks a flag up by its contract name. Unknown names are off.</summary>
    public bool IsEnabled(string flag) => flag switch
    {
        "capture" => Capture,
        "fire_solutions" => FireSolutions,
        "log_detector" => LogDetector,
        "steam_detector" => SteamDetector,
        "coordinate_clipboard" => CoordinateClipboard,
        "voice" => Voice,
        _ => false,
    };
}

/// <summary>
/// contracts/game-profile.json, served at GET /v1/catalog/game-profile. Every fact about Wardogs
/// lives here rather than in agent code, so a wrong guess is a data edit and not a signed release.
/// </summary>
public sealed record GameProfile : IValidatableContract
{
    public int SchemaVersion { get; init; } = 1;

    public required int ProfileVersion { get; init; }

    public required GameSection Game { get; init; }

    public TeamsSection Teams { get; init; } = new();

    public required MapReadoutSection MapReadout { get; init; }

    public required PointConfidenceSection PointConfidence { get; init; }

    public required SpeechSection Speech { get; init; }

    public IReadOnlyList<MapDef> Maps { get; init; } = [];

    /// <summary>Used when the current map carries no scale of its own.</summary>
    public required decimal DefaultUnitsToMeters { get; init; }

    public DetectionSection Detection { get; init; } = new();

    public required CoordinateHandoffSection CoordinateHandoff { get; init; }

    public required CoordinateSourcesSection CoordinateSources { get; init; }

    public FeatureFlagsSection FeatureFlags { get; init; } = new();

    /// <summary>Dotted paths that are a guess or a single sample. Named in the tray when one breaks.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    public MapDef? Map(string mapId) => Maps.FirstOrDefault(m => string.Equals(m.Id, mapId, StringComparison.Ordinal));

    /// <summary>Scale for one map, or null when the map is not in the profile.</summary>
    public decimal? UnitsToMetersFor(string? mapId) =>
        mapId is null ? null : Map(mapId)?.UnitsToMeters;

    /// <summary>
    /// True when every map shares <see cref="DefaultUnitsToMeters"/>. While it is false and the
    /// current map is unknown, a range in metres cannot be trusted and the caller must refuse.
    /// </summary>
    [JsonIgnore]
    public bool AllMapsShareDefaultScale => Maps.All(m => m.UnitsToMeters == DefaultUnitsToMeters);

    /// <summary>True when this dotted path is a guess or a single sample.</summary>
    public bool IsUnverified(string fieldPath) => Unverified.Contains(fieldPath, StringComparer.Ordinal);

    /// <summary>
    /// The unverified fields among the ones a subsystem depends on, so a failure can name the knob
    /// to turn instead of becoming a bug report.
    /// </summary>
    public IReadOnlyList<string> UnverifiedAmong(params string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(fieldPaths);
        return [.. fieldPaths.Where(IsUnverified)];
    }

    public void Validate(ContractValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        validation.Require(ProfileVersion > 0, "profile: profile_version must be positive");
        validation.Require(Game.ProcessNames.Count > 0, "game.process_names: empty");
        validation.Require(Game.WindowPollMs > 0, "game.window_poll_ms must be positive");

        ValidateRegex(validation, "map_readout.pattern", MapReadout.Pattern);
        ValidateRegex(validation, "map_readout.anchored_pattern", MapReadout.AnchoredPattern);
        validation.Require(MapReadout.Glyphs.Count > 0, "map_readout.glyphs: empty");
        validation.Require(
            MapReadout.NearWhiteThreshold is >= 0 and <= 255,
            "map_readout.near_white_threshold must be 0..255");
        validation.Require(
            MapReadout.ExpectedMatchesPerFrame > 0,
            "map_readout.expected_matches_per_frame must be positive");
        validation.Require(MapReadout.CorroborationFrames > 0, "map_readout.corroboration_frames must be positive");
        validation.Require(
            MapReadout.CorroborationWindowMs > 0,
            "map_readout.corroboration_window_ms must be positive");
        validation.Require(
            MapReadout.GlyphMarginFloor is >= 0 and <= 1,
            "map_readout.glyph_margin_floor must be 0..1");
        validation.Require(MapReadout.MaxJumpUnits > 0, "map_readout.max_jump_units must be positive");
        validation.Require(MapReadout.MaxJumpWindowS > 0, "map_readout.max_jump_window_s must be positive");
        validation.Require(MapReadout.MovedThresholdUnits > 0, "map_readout.moved_threshold_units must be positive");
        validation.Require(MapReadout.UiScales.Count > 0, "map_readout.ui_scales: empty");
        validation.Require(MapReadout.UiScales.All(s => s > 0), "map_readout.ui_scales: non-positive scale");
        validation.Require(
            string.Equals(MapReadout.ScanStrategy, "scan_panel_for_pattern", StringComparison.Ordinal),
            $"map_readout.scan_strategy '{MapReadout.ScanStrategy}' is not supported; the readout moves with the crosshair");

        validation.Require(PointConfidence.Floor is >= 0 and <= 1, "point_confidence.floor must be 0..1");
        validation.Require(PointConfidence.Warn is >= 0 and <= 1, "point_confidence.warn must be 0..1");
        validation.Require(
            PointConfidence.Floor <= PointConfidence.Warn,
            "point_confidence: floor is above warn");

        validation.Require(Speech.NoiseFloorDbfs < 0, "speech.noise_floor_dbfs must be negative dBFS");
        validation.Require(
            Speech.SilentHoldsBeforeWarning > 0,
            "speech.silent_holds_before_warning must be positive");

        validation.RequireDistinctIds(Maps.Select(m => m.Id), "maps");
        foreach (var map in Maps)
        {
            validation.Require(map.UnitsToMeters > 0, $"map {map.Id}: units_to_meters must be positive");
            validation.Require(map.CoordMax > map.CoordMin, $"map {map.Id}: coord_max is not above coord_min");
        }

        validation.Require(DefaultUnitsToMeters > 0, "default_units_to_meters must be positive");

        foreach (var pattern in Detection.LogTail.ConnectPatterns)
        {
            ValidateRegex(validation, "detection.log_tail.connect_patterns", pattern);
        }

        validation.Require(CoordinateHandoff.Formats.Count > 0, "coordinate_handoff.formats: empty");
        validation.RequireDistinctIds(CoordinateHandoff.Formats.Select(f => f.Id), "coordinate_handoff.formats");
        validation.Require(
            CoordinateHandoff.Format(CoordinateHandoff.DefaultFormat) is not null,
            $"coordinate_handoff.default_format '{CoordinateHandoff.DefaultFormat}' is not a listed format");
        foreach (var format in CoordinateHandoff.Formats)
        {
            validation.Require(
                !string.IsNullOrWhiteSpace(format.Template),
                $"coordinate_handoff.formats.{format.Id}: empty template");
        }

        var known = CoordinateSources.Known.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
        validation.RequireDistinctIds(CoordinateSources.Known.Select(k => k.Id), "coordinate_sources.known");
        validation.Require(CoordinateSources.Enabled.Count > 0, "coordinate_sources.enabled: empty");
        foreach (var id in CoordinateSources.Enabled)
        {
            validation.Require(known.Contains(id), $"coordinate_sources.enabled: '{id}' is not in known");
            validation.Require(
                CoordinateSources.PriorityOf(id) != int.MaxValue,
                $"coordinate_sources: '{id}' is enabled but absent from priority, so it is never asked");
        }

        foreach (var id in CoordinateSources.Priority)
        {
            validation.Require(known.Contains(id), $"coordinate_sources.priority: '{id}' is not in known");
        }
    }

    private static void ValidateRegex(ContractValidation validation, string field, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            validation.Add($"{field}: empty");
            return;
        }

        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            validation.Add($"{field}: '{pattern}' is not a valid regex ({ex.Message})");
        }
        catch (RegexMatchTimeoutException)
        {
            validation.Add($"{field}: '{pattern}' timed out on an empty input");
        }
    }
}
