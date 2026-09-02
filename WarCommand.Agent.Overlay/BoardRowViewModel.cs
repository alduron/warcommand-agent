using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WarCommand.Agent.Core.Fire;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Which state colour a row carries. One accent drives the edge bar, the digit and the state word
/// at once, so a row can never show an urgent edge next to a green digit.
/// </summary>
/// <remarks>Named states only. Role colour is web-only; the overlay's colours are all state.</remarks>
public enum RowAccent
{
    /// <summary>Open and claimable by somebody. No edge, ink digit, no state word.</summary>
    None = 0,

    /// <summary>Urgent and open. Red edge, red word.</summary>
    Urgent,

    /// <summary>Claimed by the viewer. Green edge, green digit, [YOU].</summary>
    Mine,

    /// <summary>The requester moved, or the viewer lost a claim race. Amber.</summary>
    Warned,

    /// <summary>Holds no digit: overflow, demoted, or claimed by somebody else. Dim digit.</summary>
    Muted,
}

/// <summary>
/// One row, already formatted for display. The window binds to this rather than to
/// <see cref="BoardRow"/> directly, so every formatting rule from 06-overlay-ux.md lives in one
/// place instead of being reinvented in XAML converters.
/// </summary>
/// <remarks>
/// The fields are the row anatomy drawn in docs/design/mocks/OverlayRows.dc.html: an edge bar, a
/// digit, a label and coordinate line, an optional second point, a dim meta line, and an optional
/// countdown. A field with no value collapses its line rather than rendering an empty one.
/// </remarks>
public sealed class BoardRowViewModel : INotifyPropertyChanged
{
    /// <summary>The label column is 150 wide on every mock. Kept here so XAML cannot drift from it.</summary>
    public const double LabelColumnWidth = 150;

    /// <summary>The requester column on the meta line.</summary>
    public const double RequesterColumnWidth = 80;

    public required string SlotDisplay
    {
        get => _slotDisplay;
        set => Set(ref _slotDisplay, value);
    }

    private string _slotDisplay = string.Empty;

    /// <summary>The lead target role's id. Empty when the row names none.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>The role glyph's two paths, resolved from the served catalog. Null draws nothing.</summary>
    public System.Windows.Media.Geometry? RoleGlyphFirst { get; set; }

    public System.Windows.Media.Geometry? RoleGlyphSecond { get; set; }

    /// <summary>Resource key of the role's brush. Same hue the web paints the same role.</summary>
    public string RoleBrushKey { get; set; } = "RoleCommand";

    /// <summary>Type plus its one qualifier word, uppercase. 'MORTAR SMOKE'.</summary>
    public required string TypeAndQualifier
    {
        get => _typeAndQualifier;
        set => Set(ref _typeAndQualifier, value);
    }

    private string _typeAndQualifier = string.Empty;

    public required string CoordinatesDisplay
    {
        get => _coordinatesDisplay;
        set => Set(ref _coordinatesDisplay, value);
    }

    private string _coordinatesDisplay = string.Empty;

    /// <summary>
    /// The catalog's own name for point 1, PICKUP or FROM. Empty on a type whose single point
    /// needs no naming, which is every arity-1 type.
    /// </summary>
    public string FirstPointLabel
    {
        get => _firstPointLabel;
        set => Set(ref _firstPointLabel, value);
    }

    private string _firstPointLabel = string.Empty;

    /// <summary>The second point of an arity-2 row. Null on a one-point row.</summary>
    public string? SecondPointDisplay
    {
        get => _secondPointDisplay;
        set => Set(ref _secondPointDisplay, value);
    }

    private string? _secondPointDisplay;

    /// <summary>The catalog's own name for point 2, DROPOFF or TO. Null on a one-point row.</summary>
    public string? SecondPointLabel
    {
        get => _secondPointLabel;
        set => Set(ref _secondPointLabel, value);
    }

    private string? _secondPointLabel;

    /// <summary>Bearing and range between the two points, in map units. Null on a one-point row.</summary>
    public string? LegDisplay
    {
        get => _legDisplay;
        set => Set(ref _legDisplay, value);
    }

    private string? _legDisplay;

    public required string Requester
    {
        get => _requester;
        set => Set(ref _requester, value);
    }

    private string _requester = string.Empty;

    /// <summary>Coarse relative age: 4s, 31s, 1m02. Never an absolute time.</summary>
    public required string AgeDisplay
    {
        get => _ageDisplay;
        set => Set(ref _ageDisplay, value);
    }

    private string _ageDisplay = string.Empty;

    /// <summary>'RETRY x2' and the like. Null when the row has nothing extra to say.</summary>
    public string? MetaExtra
    {
        get => _metaExtra;
        set => Set(ref _metaExtra, value);
    }

    private string? _metaExtra;

    public required string TicketCode
    {
        get => _ticketCode;
        set => Set(ref _ticketCode, value);
    }

    private string _ticketCode = string.Empty;

    /// <summary>'URGENT', '[YOU]', 'TAKEN', 'REQUESTER MOVED'. Null on a plain open row.</summary>
    public string? StateWord
    {
        get => _stateWord;
        set => Set(ref _stateWord, value);
    }

    private string? _stateWord;

    public RowAccent Accent
    {
        get => _accent;
        set => Set(ref _accent, value);
    }

    private RowAccent _accent;

    /// <summary>A row held by another participant renders at .4, as drawn in the row gallery.</summary>
    public double RowOpacity
    {
        get => _rowOpacity;
        set => Set(ref _rowOpacity, value);
    }

    private double _rowOpacity = 1.0;

    /// <summary>The sub-15s bar. False on a row with plenty of time left.</summary>
    public bool HasCountdown
    {
        get => _hasCountdown;
        set => Set(ref _hasCountdown, value);
    }

    private bool _hasCountdown;

    /// <summary>
    /// How much of its 120 s the row has left, 1 down to 0. Drawn as a wash across the whole row
    /// rather than a bar, so the countdown costs no height on a board that has to show many rows.
    /// </summary>
    public double CountdownFraction
    {
        get => _countdownFraction;
        set => Set(ref _countdownFraction, value);
    }

    private double _countdownFraction;

    /// <summary>
    /// The one pulsing slot digit. Never set by the row itself: it is a board-wide budget, and
    /// BoardView is the only thing that can see the whole board to spend it.
    /// </summary>
    /// <remarks>
    /// 06-overlay-ux.md: "Only one slot digit pulses at a time, the soonest to expire... With 300 s
    /// TTL types at saturation three or four would pulse at once, which turns the digit column into
    /// the moving thing and destroys the one property that makes it findable."
    /// </remarks>
    public bool Pulses
    {
        get => _pulses;
        set => Set(ref _pulses, value);
    }

    private bool _pulses;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Copies every displayed field from a freshly built row onto this one, raising a change for
    /// each field that actually moved and none for the rest.
    /// </summary>
    /// <remarks>
    /// This is what keeps a poll from being a flash. Replacing the ItemsSource rebuilds every
    /// container, so a board where one age went from 11s to 16s re-created eight rows and replayed
    /// eight entrance animations. Updating in place touches the one TextBlock that changed.
    /// </remarks>
    public void CopyFrom(BoardRowViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        SlotDisplay = other.SlotDisplay;
        RoleId = other.RoleId;
        RoleGlyphFirst = other.RoleGlyphFirst;
        RoleGlyphSecond = other.RoleGlyphSecond;
        RoleBrushKey = other.RoleBrushKey;
        TypeAndQualifier = other.TypeAndQualifier;
        CoordinatesDisplay = other.CoordinatesDisplay;
        SecondPointDisplay = other.SecondPointDisplay;
        LegDisplay = other.LegDisplay;
        Requester = other.Requester;
        AgeDisplay = other.AgeDisplay;
        MetaExtra = other.MetaExtra;
        StateWord = other.StateWord;
        Accent = other.Accent;
        RowOpacity = other.RowOpacity;
        HasCountdown = other.HasCountdown;
        CountdownFraction = other.CountdownFraction;

        // Pulses is deliberately not copied. It is the board's budget, applied after the reconcile.
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>Row anatomy: exactly one qualifier word. Supply kind and ordnance modifier both
    /// arrive as catalog modifier ids on the wire; this dev viewer has no catalog metadata to tell
    /// them apart from a plain count, so it renders the first modifier, then quantity, in that
    /// order. The full precedence in 06-overlay-ux.md needs the request-types catalog wired in.</summary>
    private static string Qualifier(BoardRow row)
    {
        if (row.Modifiers.Count > 0)
        {
            return row.Modifiers[0].ToUpperInvariant();
        }

        return row.QuantityRequested is { } qty ? $"x{qty.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    private static string FormatCoordinate(MapPoint point) =>
        FormattableString.Invariant($"x{point.X:0.00} y{point.Y:0.00}");

    /// <summary>Coarse relative age: 4s, 31s, 1m02, 4m. Never an absolute time.</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 60)
        {
            var minutes = (int)age.TotalMinutes;
            var seconds = age.Seconds;
            return seconds == 0 ? $"{minutes}m" : $"{minutes}m{seconds:00}";
        }

        return $"{(int)age.TotalHours}h{age.Minutes:00}";
    }

    /// <summary>
    /// How far the load has to travel, between the two points of an arity-2 row.
    /// </summary>
    /// <remarks>
    /// Distance only, and deliberately no bearing. A bearing here would be measured from the
    /// pickup to the dropoff, because those are the only two coordinates anybody has: nothing in
    /// the system knows where a player is standing, so it could never orient a driver. What it
    /// answers instead is whose job this is, since transport_move reaches ground and air transport
    /// at the same time and a 200 m move is a truck while a 3 km move is a lift.
    ///
    /// Metres when the caller hands in the map's units_to_meters, which is a served fact per
    /// binding rule 5 and never a constant here. Map units when it does not, because a wrong
    /// distance is worse than an honest unitless one.
    /// </remarks>
    private static string? Leg(BoardRow row, decimal? unitsToMeters)
    {
        if (row.Points.Count != 2)
        {
            return null;
        }

        var leg = FireSolutionCalculator.Leg(row.Points[0].Point, row.Points[1].Point, unitsToMeters);

        if (leg.DistanceMeters is not { } metres)
        {
            return FormattableString.Invariant($"{leg.DistanceUnits:0.0}u");
        }

        return metres >= 1000m
            ? FormattableString.Invariant($"{metres / 1000m:0.0}km")
            : FormattableString.Invariant($"{metres:0}m");
    }

    /// <summary>
    /// The catalog's label for one point, uppercased for the row. PICKUP and DROPOFF come from
    /// request-types.json point_labels and are never written down here: binding rule 5.
    /// </summary>
    /// <remarks>
    /// Only an arity-2 row names its points. On a one-point row the coordinate is the whole
    /// request and a label beside it is noise.
    /// </remarks>
    private static string PointLabel(BoardRow row, int ordinal) =>
        row.Points.Count > 1 && ordinal < row.Points.Count
            ? row.Points[ordinal].Label.ToUpperInvariant()
            : string.Empty;

    /// <summary>
    /// The auto-cancel bar: how much of its 120 s an OPEN row has left before it drops off the
    /// queue on its own. Every open row carries one, draining over its whole life.
    /// </summary>
    /// <remarks>
    /// It was a sub-15s bar on at most two rows, because a bar that appears late means something
    /// when it appears. With a flat 120 s the whole life IS the urgent window, and the bar is the
    /// only thing on the surface saying the row will cancel itself, so it earns being on every row.
    ///
    /// A claimed row has no bar at all: it does not expire, and drawing a draining bar on work
    /// somebody is doing would say the opposite. No text either; the bar is the whole message.
    /// </remarks>
    private static (bool Show, double Fraction) Countdown(BoardRow row, DateTimeOffset now)
    {
        if (!row.IsOpen)
        {
            return (false, 0);
        }

        var left = row.ExpiresAt - now;
        var life = row.ExpiresAt - row.CreatedAt;
        if (left <= TimeSpan.Zero || life <= TimeSpan.Zero)
        {
            return (false, 0);
        }

        return (true, Math.Clamp(left.TotalSeconds / life.TotalSeconds, 0, 1));
    }

    /// <summary>Fills the glyph from the current catalog. A row with no glyph renders its text.</summary>
    public BoardRowViewModel WithGlyph(RoleGlyphSource glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);

        var (first, second) = glyphs.Geometry(RoleId);
        RoleGlyphFirst = first;
        RoleGlyphSecond = second;
        RoleBrushKey = glyphs.BrushKey(RoleId);
        return this;
    }

    public static BoardRowViewModel FromPrimary(
        BoardRow row,
        Guid viewerParticipantId,
        DateTimeOffset now,
        decimal? unitsToMeters = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        var qualifier = Qualifier(row);
        var typeAndQualifier = qualifier.Length == 0 ? row.OverlayLabel : $"{row.OverlayLabel} {qualifier}";
        var primary = row.Points.Count > 0 ? FormatCoordinate(row.Points[0].Point) : string.Empty;
        var second = row.Points.Count > 1 ? FormatCoordinate(row.Points[1].Point) : null;
        var mine = row.IsClaimedBy(viewerParticipantId);

        // Somebody took a row this viewer asked for. Amber and the claimant's callsign: news, not
        // a job. A row held by somebody with no connection to this viewer never reaches here at
        // all, it is counted in IN PROGRESS instead.
        var takenFromMe = row.IsHeld && !mine && row.IsRequestedBy(viewerParticipantId);
        var urgent = row.Priority == Priority.Urgent && row.IsOpen;

        var (accent, word) = Accented(row, mine, takenFromMe, urgent);
        var (showBar, barFraction) = Countdown(row, now);

        // YOU, not your own callsign. Your own request is always on your board, whatever roles you
        // run, because you have to be able to watch it and cancel it. Printed as a callsign it
        // looks identical to work addressed to you, which reads as the role filter being broken.
        var requester = row.IsRequestedBy(viewerParticipantId) ? "YOU" : row.RequestedByCallsign;

        return new BoardRowViewModel
        {
            SlotDisplay = row.Slot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RoleId = row.TargetRoleIds.Count > 0 ? row.TargetRoleIds[0] : string.Empty,
            TypeAndQualifier = typeAndQualifier.ToUpperInvariant(),
            CoordinatesDisplay = primary,
            FirstPointLabel = PointLabel(row, 0),
            SecondPointDisplay = second,
            SecondPointLabel = PointLabel(row, 1),
            LegDisplay = Leg(row, unitsToMeters),
            Requester = requester,
            AgeDisplay = FormatAge(now - row.CreatedAt),
            MetaExtra = row.ReleaseCount > 0
                ? $"RETRY x{row.ReleaseCount.ToString(CultureInfo.InvariantCulture)}"
                : null,
            TicketCode = row.TicketCode,
            StateWord = word,
            Accent = accent,
            RowOpacity = 1.0,
            HasCountdown = showBar,
            CountdownFraction = barFraction,
        };
    }

    /// <summary>
    /// One accent per row, in the precedence the row gallery draws: the viewer's own claim wins,
    /// then a warning about the point, then urgency.
    /// </summary>
    private static (RowAccent Accent, string? Word) Accented(
        BoardRow row,
        bool mine,
        bool takenFromMe,
        bool urgent)
    {
        if (mine)
        {
            return (RowAccent.Mine, "[YOU]");
        }

        if (takenFromMe)
        {
            return (RowAccent.Warned, row.ClaimantCallsign?.ToUpperInvariant() ?? "TAKEN");
        }

        if (row.RequesterMoved)
        {
            return (RowAccent.Warned, "REQUESTER MOVED");
        }

        if (urgent)
        {
            return (RowAccent.Urgent, "URGENT");
        }

        return row.HoldsSlot ? (RowAccent.None, null) : (RowAccent.Muted, null);
    }

    /// <summary>
    /// A row for the YOURS section: one the viewer claimed, or one of theirs somebody took.
    /// </summary>
    /// <remarks>
    /// The right-hand word is always the OTHER person, because that is who you would talk to. If
    /// you took the job it names who wants it; if somebody took your request it names who has it.
    /// Which side you are on is carried by the colour, green for yours to do and amber for news.
    /// </remarks>
    public static BoardRowViewModel FromSecondary(
        BoardRow row,
        DateTimeOffset now,
        decimal? unitsToMeters = null,
        Guid viewerParticipantId = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        var primary = row.Points.Count > 0 ? FormatCoordinate(row.Points[0].Point) : string.Empty;

        var mine = row.IsClaimedBy(viewerParticipantId);
        var takenFromMe = row.IsHeld && !mine && row.IsRequestedBy(viewerParticipantId);

        var (accent, counterparty) = mine
            ? (RowAccent.Mine, Fragment("FOR", row.RequestedByCallsign))
            : takenFromMe
                ? (RowAccent.Warned, Fragment(null, row.ClaimantCallsign))
                : (RowAccent.None, StripState(row, now));

        return new BoardRowViewModel
        {
            SlotDisplay = row.Slot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RoleId = row.TargetRoleIds.Count > 0 ? row.TargetRoleIds[0] : string.Empty,
            TypeAndQualifier = row.OverlayLabel.ToUpperInvariant(),
            CoordinatesDisplay = primary,
            Requester = row.RequestedByCallsign,
            AgeDisplay = FormatAge(now - row.CreatedAt),
            TicketCode = row.TicketCode,
            LegDisplay = Leg(row, unitsToMeters),
            Accent = accent,
            StateWord = counterparty,
        };
    }

    /// <summary>A callsign, uppercased, optionally with a one-word lead. Never a sentence.</summary>
    private static string? Fragment(string? lead, string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return null;
        }

        var name = callsign.ToUpperInvariant();
        return lead is null ? name : $"{lead} {name}";
    }

    /// <summary>
    /// The strip's right-hand word, from the requester-feedback list in OverlayStrip.dc.html: who
    /// holds it and for how long, or what ended it.
    /// </summary>
    private static string StripState(BoardRow row, DateTimeOffset now)
    {
        var age = FormatAge(now - row.CreatedAt);
        var who = row.ClaimantCallsign?.ToUpperInvariant();

        return row.State switch
        {
            RequestState.InProgress when who is not null => $"{who} WORKING {age}",
            RequestState.Claimed when who is not null => $"{who} {age}",
            RequestState.Open => $"OPEN {age}",
            _ => age,
        };
    }
}
