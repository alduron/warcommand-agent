using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Fire;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Which state color a row carries. One accent drives the edge bar, the digit and the state word
/// at once, so a row can never show an urgent edge next to a green digit.
/// </summary>
/// <remarks>Named states only. Role color is web-only; the overlay's colors are all state.</remarks>
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
/// Which list a row is being built for. It changes the state word and nothing else: every other
/// fact is carried on both.
/// </summary>
public enum RowSurface
{
    /// <summary>The claimable queue. The state word says whether the row is the viewer's.</summary>
    Queue = 0,

    /// <summary>ACTIVE, the viewer's own work. The state word names the counterparty.</summary>
    Active,
}

/// <summary>
/// One row, already formatted for display. The window binds to this rather than to
/// <see cref="BoardRow"/> directly, so every formatting rule from 06-overlay-ux.md lives in one
/// place instead of being reinvented in XAML converters.
/// </summary>
/// <remarks>
/// The fields are the row anatomy drawn in docs/design/mocks/OverlayRows.dc.html and, for the two
/// arity-2 types, OverlayTwoPoint.dc.html: an edge bar, a digit, an identity and coordinate line,
/// an optional second point under it in the same column, a dim meta line, and an optional
/// countdown. A field with no value collapses its line rather than rendering an empty one.
/// </remarks>
public sealed class BoardRowViewModel : INotifyPropertyChanged
{
    public required string SlotDisplay
    {
        get => _slotDisplay;
        set => Set(ref _slotDisplay, value);
    }

    private string _slotDisplay = string.Empty;

    /// <summary>
    /// The navigation highlight is sitting on this row. Notifying, because the highlight moves
    /// under a held key and the board is reconciled rather than rebuilt.
    /// </summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    private bool _isHighlighted;

    /// <summary>
    /// The opening bracket for this row, when the viewer has set a gun position and the row is a
    /// gun's job. Empty otherwise, which is every row for anybody who is not on a gun.
    /// </summary>
    /// <remarks>
    /// Always a BRACKET, never a firing solution: the tables are player-measured and there is no
    /// altitude, so the answer is flat-earth and wrong on slopes. The row carries ADJUST FROM
    /// SPOTTER for the same reason.
    /// </remarks>
    public string SolutionDisplay
    {
        get => _solutionDisplay;
        set => Set(ref _solutionDisplay, value);
    }

    private string _solutionDisplay = string.Empty;

    /// <summary>The lead target role's id. Empty when the row names none.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>The role glyph's two paths, resolved from the served catalog. Null draws nothing.</summary>
    public System.Windows.Media.Geometry? RoleGlyphFirst { get; set; }

    public System.Windows.Media.Geometry? RoleGlyphSecond { get; set; }

    /// <summary>Resource key of the role's brush. Same hue the web paints the same role.</summary>
    public string RoleBrushKey { get; set; } = "RoleCommand";

    /// <summary>The type, uppercase. 'MORTAR', 'RIFLE'. Never the tags: those are their own line.</summary>
    public required string TypeAndQualifier
    {
        get => _typeAndQualifier;
        set => Set(ref _typeAndQualifier, value);
    }

    private string _typeAndQualifier = string.Empty;

    /// <summary>
    /// Every tag on the row and the quantity, uppercase. 'MAGS SCOPE SUPPRESS x2'. Empty on a
    /// row that carries none, which collapses the line.
    /// </summary>
    /// <remarks>
    /// On the meta line, never beside the type. The identity column is 100 units wide and a role
    /// glyph takes 18 of them, so a rifle delivery carrying four tags rendered as 'RIFLE MA...':
    /// the row said a rifle was wanted and dropped every fact about which one. The meta line
    /// wraps instead, and costs height only on the rows that have tags.
    /// </remarks>
    public string TagsDisplay
    {
        get => _tagsDisplay;
        set => Set(ref _tagsDisplay, value);
    }

    private string _tagsDisplay = string.Empty;

    /// <summary>
    /// The same tags, one entry each, so the row can draw them as tags rather than as a sentence.
    /// </summary>
    /// <remarks>
    /// A tag has to LOOK like a tag on both surfaces. The web draws a bordered chip per tag and the
    /// overlay drew one dim run-on line, so the same request read as two different things and the
    /// tags were indistinguishable from the meta text beside them.
    /// <para>
    /// Compared by sequence, not by reference: the board reconciles in place and a fresh list every
    /// poll would raise a change on every row and rebuild every chip.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_tags.SequenceEqual(value, StringComparer.Ordinal))
            {
                return;
            }

            _tags = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tags)));
        }
    }

    private IReadOnlyList<string> _tags = [];

    /// <summary>
    /// Everything the row draws as a bordered chip: the tags, then RETRY. One list, because they
    /// share one band and the band is the only part of a row allowed to grow.
    /// </summary>
    /// <remarks>
    /// RETRY joined the tags rather than keeping a slot of its own. The row is two lines and a
    /// chip band, and a fixed slot for something present on one row in twenty spends width every
    /// other row cannot give back.
    /// <para>Sequence-compared, like <see cref="Tags"/>: the board reconciles in place.</para>
    /// </remarks>
    public IReadOnlyList<string> Chips
    {
        get => _chips;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_chips.SequenceEqual(value, StringComparer.Ordinal))
            {
                return;
            }

            _chips = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chips)));
        }
    }

    private IReadOnlyList<string> _chips = [];

    public required string CoordinatesDisplay
    {
        get => _coordinatesDisplay;
        set => Set(ref _coordinatesDisplay, value);
    }

    private string _coordinatesDisplay = string.Empty;

    /// <summary>The second point of an arity-2 row. Null on a one-point row.</summary>
    public string? SecondPointDisplay
    {
        get => _secondPointDisplay;
        set => Set(ref _secondPointDisplay, value);
    }

    private string? _secondPointDisplay;

    /// <summary>
    /// How far the load travels, point 1 to point 2. Meters with a map scale, map units without,
    /// and never a bearing. Null on a one-point row.
    /// </summary>
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

    /// <summary>The requester's note, quoted. Empty on a row carrying none, which collapses it.</summary>
    public string NoteDisplay
    {
        get => _noteDisplay;
        set => Set(ref _noteDisplay, value);
    }

    private string _noteDisplay = string.Empty;

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
        TagsDisplay = other.TagsDisplay;
        Tags = other.Tags;
        Chips = other.Chips;
        SolutionDisplay = other.SolutionDisplay;
        CoordinatesDisplay = other.CoordinatesDisplay;
        SecondPointDisplay = other.SecondPointDisplay;
        LegDisplay = other.LegDisplay;
        Requester = other.Requester;
        AgeDisplay = other.AgeDisplay;
        NoteDisplay = other.NoteDisplay;
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
    /// <summary>
    /// Every modifier on the row and the quantity, through the one derivation the menu uses.
    /// </summary>
    /// <remarks>
    /// It used to take Modifiers[0] and uppercase the raw id, so a request made with danger close
    /// AND he read as DANGER_CLOSE: the wrong spelling, and a claim about the row that was not
    /// true. Quantity used to be an else, so a modified request never showed how many were wanted.
    /// </remarks>
    private static string Qualifier(BoardRow row, Catalog? catalog) =>
        ModifierLabels.Line(row.Modifiers, row.QuantityRequested, catalog);

    /// <summary>The board's line number, falling back to the slot when a caller has not one.</summary>
    private static string Number(int? line, BoardRow row) =>
        line is { } n
            ? n.ToString(CultureInfo.InvariantCulture)
            : row.Slot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

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
    /// Meters when the caller hands in the map's units_to_meters, which is a served fact per
    /// binding rule 5 and never a constant here. Map units when it does not, because a wrong
    /// distance is worse than an honest unitless one.
    /// </remarks>
    /// <summary>
    /// The bracket line, or empty when this row is not a gun's job or no gun position is set.
    /// </summary>
    /// <remarks>
    /// The calculator was written and tested long before anything called it: no surface ever
    /// rendered a bracket, so a mortarman got a grid and did the arithmetic himself.
    /// </remarks>
    private static string Solution(BoardRow row, FireContext? fire, DateTimeOffset now)
    {
        if (fire is null || row.Points.Count == 0)
        {
            return string.Empty;
        }

        if (!row.TargetRoleIds.Contains(fire.RoleId, StringComparer.Ordinal))
        {
            return string.Empty;
        }

        var weapon = fire.Ballistics.Weapon(fire.Gun.WeaponId);
        if (weapon is null)
        {
            return string.Empty;
        }

        var solution = FireSolutionCalculator.Compute(
            fire.Gun,
            row.Points[0].Point,
            weapon,
            fire.Ballistics,
            fire.Profile,
            fire.MapId,
            now);

        return BracketLine(solution);
    }

    /// <summary>
    /// The bracket as one line, and only the halves that are safe to show.
    /// </summary>
    /// <remarks>
    /// Geometry and elevation block on different things, so a placeholder table withholds the mils
    /// and the time of flight while azimuth and range still render. Calling the whole thing blocked
    /// hid the two thirds that worked. Every line carries the spotter hint.
    /// </remarks>
    internal static string BracketLine(FireSolution solution)
    {
        var parts = new List<string>(5) { FireSolution.BracketLabel };

        // The bearing and the range are rendered whatever the status. Out of range used to return
        // here with the refusal alone, which threw away the two numbers that were never in doubt:
        // an azimuth is exact and needs no table, and the range is what the refusal is ABOUT.
        // A crew told only OUT OF RANGE cannot even tell which way to move to fix it.
        parts.Add(FormattableString.Invariant($"AZ {solution.AzimuthDegrees:0}"));

        parts.Add(solution.RangeMeters is { } meters
            ? FormattableString.Invariant($"{meters:0}m")
            : FormattableString.Invariant($"{solution.RangeUnits:0.0}u"));

        if (solution.Status is FireSolutionStatus.OutOfRange)
        {
            parts.Add(solution.Message ?? "OUT OF RANGE");
            parts.Add(solution.SpotterHint);
            return string.Join("  ", parts);
        }

        if (solution.ElevationMils is { } mils)
        {
            parts.Add(FormattableString.Invariant($"EL {mils}"));

            if (solution.TimeOfFlightS is { } tof)
            {
                parts.Add(FormattableString.Invariant($"TOF {tof:0.0}s"));
            }
        }
        else if (solution.Message is { } withheld)
        {
            parts.Add(withheld);
        }

        if (solution.GunPositionStale)
        {
            parts.Add(FireSolution.GunPositionStaleMessage);
        }

        parts.Add(solution.SpotterHint);
        return string.Join("  ", parts);
    }

    /// <summary>
    /// The same bracket, split so a section can put the numbers and the caveats on separate lines.
    /// </summary>
    /// <remarks>
    /// One line is right on a board row, where the bracket is a footnote to the request. It is
    /// wrong in the artillery section, where the numbers are the whole point and running them
    /// together with ADJUST FROM SPOTTER pushed the elevation off the edge of the panel.
    /// </remarks>
    internal static (string Bracket, string Note) BracketParts(FireSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var notes = new List<string>(3);

        // The numbers come first and are built whatever the status. Returning an EMPTY bracket for
        // an out-of-range shot left the section with a range line and a refusal and no bearing at
        // all, and with a placeholder table that is most shots: the direction was missing from the
        // one surface that stays on screen while a crew dials, which is where they read it.
        var numbers = new List<string>(4)
        {
            FormattableString.Invariant($"AZ {solution.AzimuthDegrees:0}"),
            solution.RangeMeters is { } meters
                ? FormattableString.Invariant($"{meters:0}m")
                : FormattableString.Invariant($"{solution.RangeUnits:0.0}u"),
        };

        if (solution.Status is FireSolutionStatus.OutOfRange)
        {
            notes.Add(solution.Message ?? "OUT OF RANGE");
            notes.Add(solution.SpotterHint);
            return (string.Join("   ", numbers), string.Join("  ", notes));
        }

        if (solution.ElevationMils is { } mils)
        {
            numbers.Add(FormattableString.Invariant($"EL {mils}"));

            if (solution.TimeOfFlightS is { } tof)
            {
                numbers.Add(FormattableString.Invariant($"TOF {tof:0.0}s"));
            }
        }
        else if (solution.Message is { } withheld)
        {
            notes.Add(withheld);
        }

        if (solution.GunPositionStale)
        {
            notes.Add(FireSolution.GunPositionStaleMessage);
        }

        notes.Add(solution.SpotterHint);
        return (string.Join("   ", numbers), string.Join("  ", notes));
    }

    private static string? Leg(BoardRow row, decimal? unitsToMeters)
    {
        if (row.Points.Count != 2)
        {
            return null;
        }

        var leg = FireSolutionCalculator.Leg(row.Points[0].Point, row.Points[1].Point, unitsToMeters);

        if (leg.DistanceMeters is not { } meters)
        {
            return FormattableString.Invariant($"{leg.DistanceUnits:0.0}u");
        }

        return meters >= 1000m
            ? FormattableString.Invariant($"{meters / 1000m:0.0}km")
            : FormattableString.Invariant($"{meters:0}m");
    }

    /// <summary>
    /// The catalog's label for one point, uppercased for the row. PICKUP and DROPOFF come from
    /// request-types.json point_labels and are never written down here: binding rule 5.
    /// </summary>
    /// <remarks>
    /// Only an arity-2 row names its points. On a one-point row the coordinate is the whole
    /// request and a label beside it is noise.
    /// </remarks>
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

    /// <param name="catalog">
    /// For the tag words. Null renders the derived form, which mangles every real name in the game.
    /// </param>
    public static BoardRowViewModel FromPrimary(
        BoardRow row,
        Guid viewerParticipantId,
        DateTimeOffset now,
        decimal? unitsToMeters = null,
        FireContext? fire = null,
        Catalog? catalog = null,
        int? line = null,
        RowSurface surface = RowSurface.Queue)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The tags are their OWN line, never appended to the type. Joined on, a rifle carrying
        // MAGS SCOPE SUPPRESS x2 overflowed the 150-unit label column and trimmed to 'RIFLE MA...'.
        var tags = Qualifier(row, catalog);
        var tagList = ModifierLabels.Words(row.Modifiers, row.QuantityRequested, catalog);
        var primary = row.Points.Count > 0 ? FormatCoordinate(row.Points[0].Point) : string.Empty;
        var second = row.Points.Count > 1 ? FormatCoordinate(row.Points[1].Point) : null;
        var mine = row.IsClaimedBy(viewerParticipantId);

        // Somebody took a row this viewer asked for. Amber and the claimant's callsign: news, not
        // a job. A row held by somebody with no connection to this viewer never reaches here at
        // all, it is counted in IN PROGRESS instead.
        var takenFromMe = row.IsHeld && !mine && row.IsRequestedBy(viewerParticipantId);
        var urgent = row.Priority == Priority.Urgent && row.IsOpen;

        var (accent, word) = Accented(row, mine, takenFromMe, urgent, surface);
        var (showBar, barFraction) = Countdown(row, now);

        // Urgency is already the red edge AND the state word, and the API leaves 'urgent' in the
        // modifier list because it is what set the priority. Drawn as a chip as well it said the
        // same thing three times on one row and pushed the facts that are only said once off the
        // end. The chip comes BACK the moment the state word is something else, a claim or a
        // moved requester, because then nothing else on the row is carrying it.
        if (string.Equals(word, "URGENT", StringComparison.Ordinal))
        {
            tagList = [.. tagList.Where(t => !string.Equals(t, "URGENT", StringComparison.OrdinalIgnoreCase))];
        }

        // YOU, not your own callsign. Your own request is always on your board, whatever roles you
        // run, because you have to be able to watch it and cancel it. Printed as a callsign it
        // looks identical to work addressed to you, which reads as the role filter being broken.
        var requester = Requesters(row, viewerParticipantId);

        return new BoardRowViewModel
        {
            // The LINE, not the slot. A slot is an allocation token and leaves holes; the number
            // somebody reads off the board and says out loud is the position on it.
            SlotDisplay = Number(line, row),
            RoleId = row.TargetRoleIds.Count > 0 ? row.TargetRoleIds[0] : string.Empty,
            TypeAndQualifier = row.OverlayLabel.ToUpperInvariant(),
            TagsDisplay = tags.ToUpperInvariant(),
            Tags = tagList,
            Chips = Chipped(tagList, row),
            CoordinatesDisplay = primary,
            SecondPointDisplay = second,
            LegDisplay = Leg(row, unitsToMeters),
            SolutionDisplay = Solution(row, fire, now),
            Requester = requester,
            AgeDisplay = FormatAge(now - row.CreatedAt),
            NoteDisplay = string.IsNullOrWhiteSpace(row.Note) ? string.Empty : $"\"{row.Note.Trim()}\"",
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
    /// <remarks>
    /// The state word names the counterparty in both directions and in one shape: FOR the person
    /// who asked, BY the person doing it. In ACTIVE every row is already the viewer's, so [YOU]
    /// carries nothing and the counterparty takes the slot.
    /// </remarks>
    private static (RowAccent Accent, string? Word) Accented(
        BoardRow row,
        bool mine,
        bool takenFromMe,
        bool urgent,
        RowSurface surface)
    {
        if (mine)
        {
            return surface == RowSurface.Active
                ? (RowAccent.Mine, Fragment("FOR", row.RequestedByCallsign) ?? "[YOU]")
                : (RowAccent.Mine, "[YOU]");
        }

        if (takenFromMe)
        {
            return (RowAccent.Warned, Fragment("BY", row.ClaimantCallsign) ?? "TAKEN");
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
    /// The tags, then RETRY, then the roles past the one the glyph draws, in band order.
    /// </summary>
    private static IReadOnlyList<string> Chipped(IReadOnlyList<string> tags, BoardRow row)
    {
        var chips = new List<string>(tags.Count + 2);
        chips.AddRange(tags);

        if (row.ReleaseCount > 0)
        {
            chips.Add($"RETRY x{row.ReleaseCount.ToString(CultureInfo.InvariantCulture)}");
        }

        if (row.TargetRoleIds.Count > 1)
        {
            chips.Add($"+{(row.TargetRoleIds.Count - 1).ToString(CultureInfo.InvariantCulture)} ROLES");
        }

        return chips.Count == tags.Count ? tags : chips;
    }

    /// <summary>
    /// Who asked. YOU for the viewer's own row, and '+N' for the others coalesced onto it.
    /// </summary>
    private static string Requesters(BoardRow row, Guid viewerParticipantId)
    {
        var lead = row.IsRequestedBy(viewerParticipantId) ? "YOU" : row.RequestedByCallsign;
        return row.CoRequesterCount > 1
            ? $"{lead} +{(row.CoRequesterCount - 1).ToString(CultureInfo.InvariantCulture)}"
            : lead;
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
}
