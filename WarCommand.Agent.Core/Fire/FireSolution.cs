using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Fire;

/// <summary>Which half of a bracket, if either, had to be withheld.</summary>
public enum FireSolutionStatus
{
    /// <summary>Geometry and elevation both rendered.</summary>
    Ok,

    /// <summary>Geometry rendered; elevation withheld while table_confidence is 'placeholder'.</summary>
    NoFiringTable,

    /// <summary>Below min_range_m or above max_range_m. The gun cannot reach it.</summary>
    OutOfRange,

    /// <summary>No map, and the profile carries a per-map scale, so metres are unknowable.</summary>
    MapUnknown,
}

/// <summary>
/// An opening bracket for one gun and one target. Never a firing solution: flat terrain, a
/// player-measured table and 50 MOA grouping, so it gets the first round close enough for a spotter
/// to correct from.
/// </summary>
/// <remarks>
/// Geometry and elevation have different dependencies and block separately. Azimuth is exact the
/// moment two coordinates are trusted; elevation needs a measured table.
/// </remarks>
public sealed record FireSolution
{
    /// <summary>The word the overlay uses. Never 'firing solution'.</summary>
    public const string BracketLabel = "BRACKET";

    /// <summary>Rendered when the current map is unknown and metres cannot be trusted.</summary>
    public const string MapUnknownMessage = "MAP UNKNOWN";

    /// <summary>Rendered instead of a mil value that would look measured and is not.</summary>
    public const string NoFiringTableMessage = "NO FIRING TABLE";

    /// <summary>Rendered when the gun position is older than its stale window.</summary>
    public const string GunPositionStaleMessage = "GUN POS STALE";

    public required string WeaponId { get; init; }

    /// <summary>'high' or 'low'. Low exists on the SPH-2 above its low_arc_min_range_m only.</summary>
    public required string Arc { get; init; }

    /// <summary>Degrees from true north. atan2 on two deltas: unitless and exact.</summary>
    public required decimal AzimuthDegrees { get; init; }

    /// <summary>hypot on the two deltas, in map units. Always present.</summary>
    public required decimal RangeUnits { get; init; }

    /// <summary>Null when no scale could be resolved. Then the overlay reads map units.</summary>
    public decimal? RangeMeters { get; init; }

    /// <summary>True when range must render as map units, because a wrong metre figure is worse.</summary>
    public bool RangeInMapUnits => RangeMeters is null;

    /// <summary>Null while the table is a placeholder, out of range, or the map is unknown.</summary>
    public int? ElevationMils { get; init; }

    /// <summary>Null under the same conditions as <see cref="ElevationMils"/>.</summary>
    public decimal? TimeOfFlightS { get; init; }

    public required FireSolutionStatus Status { get; init; }

    /// <summary>What the overlay renders in place of the withheld half. Null when nothing is withheld.</summary>
    public string? Message { get; init; }

    public required decimal MinRangeM { get; init; }

    public required decimal MaxRangeM { get; init; }

    /// <summary>The SPH-2 drives, and the agent cannot know it moved.</summary>
    public required bool GunPositionStale { get; init; }

    /// <summary>Carried by every render. From solution_rules.presentation.spotter_hint.</summary>
    public required string SpotterHint { get; init; }

    /// <summary>Always <see cref="BracketLabel"/>.</summary>
    public string Label { get; } = BracketLabel;

    /// <summary>True when a mil value and a time of flight are safe to show.</summary>
    public bool HasElevation => ElevationMils is not null;
}

/// <summary>
/// Bearing and distance along one leg, which is what a driver or a pilot reads off a two-point
/// request. The same atan2 and hypot as the bracket, with no table involved.
/// </summary>
public sealed record LegGeometry(decimal BearingDegrees, decimal DistanceUnits, decimal? DistanceMeters)
{
    public bool DistanceInMapUnits => DistanceMeters is null;
}

/// <summary>
/// Computes brackets locally on every render. No server round trip: the inputs are two coordinates
/// the agent already has and a table it fetched at startup. Pure; <c>now</c> is a parameter.
/// </summary>
public static class FireSolutionCalculator
{
    /// <summary>
    /// The bracket for one gun and one target. Geometry always renders; elevation is withheld
    /// whenever the table is a placeholder, the range is outside the weapon, or the map is unknown.
    /// </summary>
    public static FireSolution Compute(
        GunPosition gun,
        MapPoint target,
        WeaponDef weapon,
        Ballistics ballistics,
        GameProfile profile,
        string? currentMapId,
        DateTimeOffset now,
        string? arc = null,
        TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(gun);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(ballistics);
        ArgumentNullException.ThrowIfNull(profile);

        var rules = ballistics.SolutionRules;
        var azimuth = RoundToNearest(Azimuth(gun.Position, target), rules.RoundAzimuthTo);
        var rangeUnits = RangeUnits(gun.Position, target);
        var stale = staleAfter is { } window ? gun.Stale(now, window) : gun.Stale(now);
        var scale = UnitsToMeters(profile, ballistics, currentMapId);

        var solution = new FireSolution
        {
            WeaponId = weapon.Id,
            Arc = rules.DefaultArc,
            AzimuthDegrees = azimuth,
            RangeUnits = Round(rangeUnits, 2),
            MinRangeM = weapon.MinRangeM,
            MaxRangeM = weapon.MaxRangeM,
            GunPositionStale = stale,
            SpotterHint = rules.Presentation.SpotterHint,
            Status = FireSolutionStatus.Ok,
        };

        if (scale is not { } unitsToMeters)
        {
            return solution with { Status = FireSolutionStatus.MapUnknown, Message = FireSolution.MapUnknownMessage };
        }

        var rangeM = Round(rangeUnits * unitsToMeters, 1);
        solution = solution with { RangeMeters = rangeM };

        if (!weapon.InRange(rangeM))
        {
            return solution with { Status = FireSolutionStatus.OutOfRange, Message = rules.RefuseMessage };
        }

        var chosen = ChooseArc(weapon, rangeM, arc ?? rules.DefaultArc);
        solution = solution with { Arc = chosen };

        if (!weapon.HasMeasuredTable)
        {
            return solution with
            {
                Status = FireSolutionStatus.NoFiringTable,
                Message = FireSolution.NoFiringTableMessage,
            };
        }

        var interpolated = Interpolate(weapon, rangeM, chosen);
        if (interpolated is null)
        {
            return solution with
            {
                Status = FireSolutionStatus.NoFiringTable,
                Message = FireSolution.NoFiringTableMessage,
            };
        }

        var (mils, tof) = interpolated.Value;
        return solution with
        {
            ElevationMils = (int)RoundToNearest(mils, rules.RoundMilsTo),
            TimeOfFlightS = Round(tof, 1),
            Status = FireSolutionStatus.Ok,
        };
    }

    /// <summary>Bearing and distance from one point to another. What a two-point request needs.</summary>
    public static LegGeometry Leg(MapPoint from, MapPoint to, decimal? unitsToMeters, int roundBearingTo = 1)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var units = RangeUnits(from, to);
        return new LegGeometry(
            RoundToNearest(Azimuth(from, to), roundBearingTo),
            Round(units, 2),
            unitsToMeters is { } scale ? Round(units * scale, 1) : null);
    }

    /// <summary>Degrees clockwise from north. Unitless, exact, and needs no map scale.</summary>
    public static decimal Azimuth(MapPoint from, MapPoint to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var dx = (double)(to.X - from.X);
        var dy = (double)(to.Y - from.Y);
        var degrees = (Math.Atan2(dx, dy) * 180.0 / Math.PI) + 360.0;
        return (decimal)(degrees % 360.0);
    }

    /// <summary>hypot on the two deltas, in map units.</summary>
    public static decimal RangeUnits(MapPoint from, MapPoint to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return from.DistanceUnitsTo(to);
    }

    /// <summary>
    /// The current map's scale, falling back to default_units_to_meters and then to the value in
    /// ballistics.json. Null while the map is unknown and the profile carries a per-map scale that
    /// differs from the default, which is the case the overlay renders as MAP UNKNOWN.
    /// </summary>
    public static decimal? UnitsToMeters(GameProfile profile, Ballistics ballistics, string? currentMapId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ballistics);

        if (currentMapId is not null)
        {
            return profile.UnitsToMetersFor(currentMapId)
                   ?? Fallback(profile, ballistics);
        }

        return profile.AllMapsShareDefaultScale ? Fallback(profile, ballistics) : null;
    }

    private static decimal Fallback(GameProfile profile, Ballistics ballistics) =>
        profile.DefaultUnitsToMeters > 0 ? profile.DefaultUnitsToMeters : ballistics.MapGeometry.MapUnitsToMeters;

    private static string ChooseArc(WeaponDef weapon, decimal rangeM, string requested) =>
        string.Equals(requested, "low", StringComparison.Ordinal) && weapon.LowArcAvailable(rangeM)
            ? "low"
            : "high";

    private static (decimal Mils, decimal Tof)? Interpolate(WeaponDef weapon, decimal rangeM, string arc)
    {
        var low = string.Equals(arc, "low", StringComparison.Ordinal);
        var table = weapon.Table;

        for (var i = 0; i < table.Count - 1; i++)
        {
            var a = table[i];
            var b = table[i + 1];
            if (rangeM < a.RangeM || rangeM > b.RangeM)
            {
                continue;
            }

            decimal? milA = low ? a.LowMil : a.HighMil;
            decimal? milB = low ? b.LowMil : b.HighMil;
            if (milA is null || milB is null)
            {
                return null;
            }

            var span = b.RangeM - a.RangeM;
            var t = span == 0 ? 0m : (rangeM - a.RangeM) / span;
            return (milA.Value + ((milB.Value - milA.Value) * t), a.TofS + ((b.TofS - a.TofS) * t));
        }

        // Inside the weapon's stated envelope but outside its measured rows. Never extrapolate.
        return null;
    }

    private static decimal Round(decimal value, int decimals) =>
        Math.Round(value, Math.Max(decimals, 0), MidpointRounding.AwayFromZero);

    /// <summary>round_azimuth_to and round_mils_to are step sizes, not decimal places.</summary>
    private static decimal RoundToNearest(decimal value, int step) =>
        step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
}
