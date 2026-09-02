using System.Linq;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Dev;

/// <summary>
/// The board drawn in 06-overlay-ux.md "Layout", as rows the overlay can render with no API, no
/// deployment and no game.
/// </summary>
/// <remarks>
/// The overlay's iteration loop: WARCOMMAND_OVERLAY_DEMO=1 draws these on the primary monitor so
/// placement, opacity, anchoring and the row anatomy can be worked on. Wardogs is not out, so
/// without this the only way to see the surface at all is to have the game, and the surface would
/// ship having been looked at by nobody.
/// </remarks>
internal static class OverlayDemo
{
    /// <summary>
    /// The real resolver over the bundled catalog, so the demo draws the shipped glyph and hue for
    /// each role rather than a hand-drawn approximation of them.
    /// </summary>
    private static readonly RoleGlyphSource Glyphs =
        new(BundledContracts.Catalog().Current.Role);

    /// <summary>The header from the mock. A group, a deployment, a headcount, a map, an invite.</summary>
    internal static BoardHeader Header { get; } = new BoardHeader
    {
        Title = "61ST / ALPHA",
        PeopleCount = 31,
        Where = "Bakurani",
        Right = "invite 921585",
        RoleIds = ["mortar", "logistics"],
        Hint = OverlayHint.Resolve(new HintState { PttLabel = "Mouse5" }),
    }.WithGlyph(Glyphs);

    /// <summary>The four slotted rows, in the order and with the states the mock draws them.</summary>
    internal static IReadOnlyList<BoardRowViewModel> Rows { get; } = WithGlyphs(
    [
        new BoardRowViewModel
        {
            SlotDisplay = "1",
            RoleId = "mortar",
            TypeAndQualifier = "MORTAR",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "12s",
            TicketCode = "MTR-14",
            HasCountdown = true,
            CountdownFraction = 0.90,
        },
        new BoardRowViewModel
        {
            SlotDisplay = "2",
            RoleId = "mortar",
            TypeAndQualifier = "MORTAR SMOKE",
            CoordinatesDisplay = "x84.10 y70.88",
            Requester = "Bear",
            AgeDisplay = "4s",
            MetaExtra = "RETRY x2",
            TicketCode = "MTR-15",
            HasCountdown = true,
            CountdownFraction = 0.97,
            StateWord = "URGENT",
            Accent = RowAccent.Urgent,
        },
        new BoardRowViewModel
        {
            SlotDisplay = "3",
            RoleId = "ground_transport",
            TypeAndQualifier = "TRANSPORT",
            CoordinatesDisplay = "x88.00 y61.20",
            FirstPointLabel = "PICKUP",
            SecondPointDisplay = "x91.44 y58.02",
            SecondPointLabel = "DROPOFF",
            // The true distance between this row's own two points: 4.6847 units at Bakurani's
            // 100 m per unit. The demo previously carried '074deg 2.4km', a number that matched
            // neither the coordinates above it nor anything the code could produce.
            LegDisplay = "469m",
            Requester = "Wolf",
            AgeDisplay = "31s",
            TicketCode = "TPT-16",
            HasCountdown = true,
            CountdownFraction = 0.74,
        },
    ]);

    /// <summary>
    /// YOURS: the rows this viewer is one half of, at the foot of the board. Everything else
    /// somebody is already on is the IN PROGRESS count and nothing more.
    /// </summary>
    internal static IReadOnlyList<BoardRowViewModel> SecondaryStrip { get; } = WithGlyphs(
    [
        // Asked for by this viewer, taken by Bear. Amber and his callsign: news, not a job, and
        // no digit because there is nothing here to address by voice.
        new BoardRowViewModel
        {
            SlotDisplay = string.Empty,
            RoleId = "logistics",
            TypeAndQualifier = "AMMO",
            CoordinatesDisplay = "x82.91 y66.04",
            Requester = "Kite",
            AgeDisplay = "2m",
            TicketCode = "LOG-09",
            StateWord = "BEAR",
            Accent = RowAccent.Warned,
        },

        // Taken by this viewer. Green, and it keeps digit 4 so 'done 4' still reaches it.
        new BoardRowViewModel
        {
            SlotDisplay = "4",
            RoleId = "mortar",
            TypeAndQualifier = "MORTAR",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "1m02",
            TicketCode = "MTR-12",
            StateWord = "FOR GHOST",
            Accent = RowAccent.Mine,
        },
    ]);

    /// <summary>Held by somebody this viewer has no part in. One line, never rows.</summary>
    internal const int InProgressCount = 3;

    /// <summary>The overflow line: twelve more, three of them urgent.</summary>
    internal const int OverflowCount = 12;

    /// <summary>Of the overflow, how many are urgent.</summary>
    internal const int OverflowUrgentCount = 3;

    /// <summary>
    /// Attaches the served glyph and hue to every demo row. Rendering a row that never went
    /// through the resolver is what made the demo the one surface where roles were invisible.
    /// </summary>
    private static IReadOnlyList<BoardRowViewModel> WithGlyphs(IReadOnlyList<BoardRowViewModel> rows) =>
        [.. rows.Select(r => r.WithGlyph(Glyphs))];
}
