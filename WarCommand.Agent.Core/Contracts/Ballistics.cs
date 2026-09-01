using System.Text.Json.Serialization;

namespace WarCommand.Agent.Core.Contracts;

/// <summary>Where the tables came from. Nothing here is official; every row is player-measured.</summary>
public sealed record BallisticsProvenance
{
    public string? MeasuredBy { get; init; }

    public bool Official { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = [];
}

/// <summary>Units and reference the tables are stated in.</summary>
public sealed record MapGeometrySection
{
    /// <summary>Fallback scale. The current map's entry in game-profile.json wins over this.</summary>
    public required decimal MapUnitsToMeters { get; init; }

    public string? Confidence { get; init; }

    /// <summary>'true_north'. Azimuth 0 is north.</summary>
    public string AzimuthReference { get; init; } = "true_north";

    public string AzimuthUnit { get; init; } = "degrees";

    public string ElevationUnit { get; init; } = "mils";
}

/// <summary>Elevation envelope a weapon can actually dial.</summary>
public sealed record ElevationEnvelope
{
    public required int Min { get; init; }

    public required int Max { get; init; }
}

/// <summary>One measured row. Interpolate linearly between adjacent rows, never past the last one.</summary>
public sealed record BallisticsRow
{
    public required decimal RangeM { get; init; }

    public required int HighMil { get; init; }

    public required decimal TofS { get; init; }

    /// <summary>Null where the low arc does not reach.</summary>
    public int? LowMil { get; init; }
}

/// <summary>One weapon and its table.</summary>
public sealed record WeaponDef
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    /// <summary>Catalog role that fires it.</summary>
    public required string Role { get; init; }

    public string? Platform { get; init; }

    public required decimal MinRangeM { get; init; }

    public required decimal MaxRangeM { get; init; }

    public string? MaxRangeConfidence { get; init; }

    public IReadOnlyList<string> Arcs { get; init; } = [];

    /// <summary>Shortest range at which the low arc exists, or null when it never does.</summary>
    public decimal? LowArcMinRangeM { get; init; }

    public int GroupingMoa { get; init; }

    public required ElevationEnvelope ElevationMils { get; init; }

    public required IReadOnlyList<BallisticsRow> Table { get; init; }

    /// <summary>'placeholder' means the interior rows are interpolated guesses.</summary>
    public required string TableConfidence { get; init; }

    /// <summary>
    /// False while the table is a placeholder. Elevation and time of flight are withheld and the
    /// overlay reads NO FIRING TABLE; geometry still renders.
    /// </summary>
    [JsonIgnore]
    public bool HasMeasuredTable => !string.Equals(TableConfidence, "placeholder", StringComparison.Ordinal);

    /// <summary>Outside the table the answer is a refusal, never an extrapolation.</summary>
    public bool InRange(decimal rangeM) => rangeM >= MinRangeM && rangeM <= MaxRangeM;

    /// <summary>True when the low arc is available at this range.</summary>
    public bool LowArcAvailable(decimal rangeM) =>
        Arcs.Contains("low", StringComparer.Ordinal)
        && LowArcMinRangeM is { } min
        && rangeM >= min
        && rangeM <= MaxRangeM;
}

/// <summary>What a bracket is called, and what always renders beside it.</summary>
public sealed record PresentationRules
{
    /// <summary>'opening bracket'.</summary>
    public string CallIt { get; init; } = "opening bracket";

    /// <summary>'firing solution'. Never this.</summary>
    public string NeverCallIt { get; init; } = "firing solution";

    public IReadOnlyList<string> AlwaysShowAlongside { get; init; } = [];

    /// <summary>'ADJUST FROM SPOTTER'. Carried by the first render of every row.</summary>
    public required string SpotterHint { get; init; }
}

/// <summary>Flat-earth by necessity: the readout carries no altitude channel.</summary>
public sealed record TerrainRules
{
    public bool AppliesHeightCorrection { get; init; }
}

/// <summary>How a bracket is computed, rounded and refused.</summary>
public sealed record SolutionRulesSection
{
    public string Interpolation { get; init; } = "linear_between_adjacent_rows";

    public int RoundMilsTo { get; init; } = 1;

    public int RoundAzimuthTo { get; init; } = 1;

    /// <summary>'refuse'. Never extrapolate below the first row.</summary>
    public string BelowMinRange { get; init; } = "refuse";

    /// <summary>'refuse'. Never extrapolate past the last row.</summary>
    public string AboveMaxRange { get; init; } = "refuse";

    public required string RefuseMessage { get; init; }

    public string DefaultArc { get; init; } = "high";

    public TerrainRules Terrain { get; init; } = new();

    public required PresentationRules Presentation { get; init; }
}

/// <summary>Labelled samples of the true solution. Off at M1, per group, owner opts in.</summary>
public sealed record FireObservationsSection
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> WhatIsRecorded { get; init; } = [];
}

/// <summary>
/// contracts/ballistics.json, served at GET /v1/catalog/ballistics. Never hardcode a range or a mil
/// value. What this produces is an opening bracket, never a firing solution.
/// </summary>
public sealed record Ballistics : IValidatableContract
{
    public required int Version { get; init; }

    public BallisticsProvenance Provenance { get; init; } = new();

    public required MapGeometrySection MapGeometry { get; init; }

    public required IReadOnlyList<WeaponDef> Weapons { get; init; }

    public required SolutionRulesSection SolutionRules { get; init; }

    public FireObservationsSection FireObservations { get; init; } = new();

    public WeaponDef? Weapon(string id) =>
        Weapons.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));

    public void Validate(ContractValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        validation.Require(Version > 0, "ballistics: version must be positive");
        validation.Require(MapGeometry.MapUnitsToMeters > 0, "map_geometry.map_units_to_meters must be positive");
        validation.Require(Weapons.Count > 0, "ballistics: no weapons");
        validation.RequireDistinctIds(Weapons.Select(w => w.Id), "ballistics.weapons");

        foreach (var weapon in Weapons)
        {
            validation.Require(weapon.MinRangeM > 0, $"weapon {weapon.Id}: min_range_m must be positive");
            validation.Require(
                weapon.MaxRangeM > weapon.MinRangeM,
                $"weapon {weapon.Id}: max_range_m is not above min_range_m");
            validation.Require(weapon.Arcs.Count > 0, $"weapon {weapon.Id}: no arcs");
            validation.Require(
                weapon.ElevationMils.Max > weapon.ElevationMils.Min,
                $"weapon {weapon.Id}: elevation envelope is inverted");
            validation.Require(weapon.Table.Count >= 2, $"weapon {weapon.Id}: a table needs at least two rows");
            validation.Require(
                !string.IsNullOrWhiteSpace(weapon.TableConfidence),
                $"weapon {weapon.Id}: no table_confidence, so the agent cannot tell a guess from a measurement");

            if (weapon.Arcs.Contains("low", StringComparer.Ordinal))
            {
                validation.Require(
                    weapon.LowArcMinRangeM is not null,
                    $"weapon {weapon.Id}: offers a low arc with no low_arc_min_range_m");
            }

            decimal previous = -1;
            foreach (var row in weapon.Table)
            {
                validation.Require(
                    row.RangeM > previous,
                    $"weapon {weapon.Id}: table rows must ascend by range_m, saw {row.RangeM} after {previous}");
                previous = row.RangeM;

                validation.Require(
                    row.RangeM > 0 && row.RangeM <= weapon.MaxRangeM,
                    $"weapon {weapon.Id}: table row {row.RangeM} m sits past the stated max_range_m");
                validation.Require(
                    row.HighMil >= weapon.ElevationMils.Min && row.HighMil <= weapon.ElevationMils.Max,
                    $"weapon {weapon.Id}: high_mil {row.HighMil} at {row.RangeM} m is outside the elevation envelope");
                validation.Require(row.TofS > 0, $"weapon {weapon.Id}: non-positive tof_s at {row.RangeM} m");

                if (row.LowMil is { } low)
                {
                    validation.Require(
                        low >= weapon.ElevationMils.Min && low <= weapon.ElevationMils.Max,
                        $"weapon {weapon.Id}: low_mil {low} at {row.RangeM} m is outside the elevation envelope");
                }
            }
        }

        validation.Require(
            string.Equals(SolutionRules.BelowMinRange, "refuse", StringComparison.Ordinal),
            "solution_rules.below_min_range must be 'refuse'; extrapolating past the table is never allowed");
        validation.Require(
            string.Equals(SolutionRules.AboveMaxRange, "refuse", StringComparison.Ordinal),
            "solution_rules.above_max_range must be 'refuse'; extrapolating past the table is never allowed");
        validation.Require(
            !string.IsNullOrWhiteSpace(SolutionRules.RefuseMessage),
            "solution_rules.refuse_message: empty");
        validation.Require(
            !string.IsNullOrWhiteSpace(SolutionRules.Presentation.SpotterHint),
            "solution_rules.presentation.spotter_hint: empty; every bracket renders one");
        validation.Require(
            !SolutionRules.Terrain.AppliesHeightCorrection,
            "solution_rules.terrain.applies_height_correction: there is no altitude channel to correct with");
    }
}
