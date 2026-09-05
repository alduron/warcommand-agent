using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The range readout as its own section of the board.
/// </summary>
/// <remarks>
/// It used to be one string squeezed into the menu's typed-text field, which clipped it and tied it
/// to the menu being open: the crew could not read the numbers they were about to dial unless they
/// were holding the key, and even then the line ran off the panel. Broken into fields here, drawn
/// for as long as a gun is set, and never trimmed.
/// <para>
/// Always a BRACKET and never a firing solution. The tables are player-measured, the L81 groups at
/// 50 MOA, and there is no altitude anywhere in the system, so the answer is flat-earth and wrong
/// on slopes. Every readout carries ADJUST FROM SPOTTER.
/// </para>
/// </remarks>
public sealed record ArtilleryViewModel
{
    /// <summary>Where the range is measured from, or what is still missing.</summary>
    public required string Gun { get; init; }

    /// <summary>The calculator this range is judged against: SNIPING, MORTAR, ARTILLERY.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>The straight-line range between the two ends, or empty while one is missing.</summary>
    public string Range { get; init; } = string.Empty;

    /// <summary>True while the range is outside what the chosen weapon can reach.</summary>
    public bool RangeIsOutOfReach { get; init; }

    /// <summary>Where the target is, or what is still missing.</summary>
    public required string Target { get; init; }

    /// <summary>The numbers to dial. Empty until both ends are set.</summary>
    public string Bracket { get; init; } = string.Empty;

    /// <summary>The spotter hint, or why part of the bracket is withheld.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// The readout for a tool with either end possibly unset. Null when no gun has been read, which
    /// is the normal state for anybody not on a gun.
    /// </summary>
    public static ArtilleryViewModel? For(
        MapPoint? gun,
        MapPoint? target,
        Func<MapPoint, MapPoint, (string Bracket, string Note)> compute,
        RangeMode? mode = null,
        Func<MapPoint, MapPoint, decimal>? metres = null)
    {
        ArgumentNullException.ThrowIfNull(compute);

        if (gun is null)
        {
            return null;
        }

        var readout = new ArtilleryViewModel
        {
            Mode = mode?.Label ?? string.Empty,
            Gun = FormattableString.Invariant($"ORIGIN  x{gun.X:0.00} y{gun.Y:0.00}"),
            Target = target is { } aimed
                ? FormattableString.Invariant($"TARGET  x{aimed.X:0.00} y{aimed.Y:0.00}")
                : "TARGET  NOT SET",
        };

        if (target is not { } aim)
        {
            return readout with { Note = "SET THE TARGET FOR A RANGE" };
        }

        if (metres is not null)
        {
            var range = metres(gun, aim);
            readout = readout with
            {
                Range = FormattableString.Invariant($"{range:0} M"),
                RangeIsOutOfReach = mode?.IsOutOfRange(range) == true,
            };
        }

        var (bracket, note) = compute(gun, aim);
        return readout with { Bracket = bracket, Note = note };
    }
}
