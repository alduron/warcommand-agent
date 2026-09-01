namespace WarCommand.Agent.Core.Model;

/// <summary>
/// One map coordinate and where it came from. Snapshotted at PTT key-down, never at key-up.
/// </summary>
/// <param name="X">Map x, two decimals, never a float on the wire.</param>
/// <param name="Y">Map y, two decimals, never a float on the wire.</param>
/// <param name="Source">The winning source id, written verbatim to request_points.source.</param>
/// <param name="RawText">Null for sources that never produced a string.</param>
/// <param name="Confidence">Null where the concept does not apply. Never assume it is present.</param>
public sealed record MapPoint(
    decimal X,
    decimal Y,
    string Source,
    string? RawText,
    decimal? Confidence)
{
    /// <summary>Map-unit distance to another point. Unitless; multiply by units_to_meters for metres.</summary>
    public decimal DistanceUnitsTo(MapPoint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var dx = (double)(other.X - X);
        var dy = (double)(other.Y - Y);
        return (decimal)Math.Sqrt((dx * dx) + (dy * dy));
    }
}
