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
    /// <summary>The header from the mock. A group, a deployment, a headcount, a map, an invite.</summary>
    internal static BoardHeader Header { get; } = new()
    {
        Title = "61ST / ALPHA",
        PeopleCount = 31,
        Where = "Bakurani",
        Right = "invite 921585",
        Roles = "mortar logistics",
        Hint = "RightAlt+H ?",
    };

    /// <summary>The four slotted rows, in the order and with the states the mock draws them.</summary>
    internal static IReadOnlyList<BoardRowViewModel> Rows { get; } =
    [
        new BoardRowViewModel
        {
            SlotDisplay = "1",
            TypeAndQualifier = "MORTAR",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "12s",
            TicketCode = "MTR-14",
        },
        new BoardRowViewModel
        {
            SlotDisplay = "2",
            TypeAndQualifier = "MORTAR SMOKE",
            CoordinatesDisplay = "x84.10 y70.88",
            Requester = "Bear",
            AgeDisplay = "4s",
            MetaExtra = "RETRY x2",
            TicketCode = "MTR-15",
            StateWord = "URGENT",
            Accent = RowAccent.Urgent,
        },
        new BoardRowViewModel
        {
            SlotDisplay = "3",
            TypeAndQualifier = "TRANSPORT",
            CoordinatesDisplay = "x88.00 y61.20",
            SecondPointDisplay = "-> x91.44 y58.02",
            LegDisplay = "074deg 2.4km",
            Requester = "Wolf",
            AgeDisplay = "31s",
            TicketCode = "TPT-16",
        },
        new BoardRowViewModel
        {
            SlotDisplay = "4",
            TypeAndQualifier = "MORTAR",
            CoordinatesDisplay = "x85.53 y69.42",
            Requester = "Ghost",
            AgeDisplay = "1m02",
            TicketCode = "MTR-12",
            StateWord = "[YOU]",
            Accent = RowAccent.Mine,
            HasCountdown = true,
            CountdownWidth = 48,
            CountdownText = "4m left",
        },
    ];

    /// <summary>Claimed rows, which stay visible without holding a slot.</summary>
    internal static IReadOnlyList<BoardRowViewModel> SecondaryStrip { get; } =
    [
        new BoardRowViewModel
        {
            SlotDisplay = string.Empty,
            TypeAndQualifier = "AMMO",
            CoordinatesDisplay = "x82.91 y66.04",
            Requester = "Kite",
            AgeDisplay = "2m",
            TicketCode = "LOG-09",
            StateWord = "BEAR",
            Accent = RowAccent.Muted,
        },
    ];

    /// <summary>The overflow line: twelve more, three of them urgent.</summary>
    internal const int OverflowCount = 12;

    /// <summary>Of the overflow, how many are urgent.</summary>
    internal const int OverflowUrgentCount = 3;
}
