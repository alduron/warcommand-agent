using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    /// <summary>The second point of an arity-2 row, prefixed. Null on a one-point row.</summary>
    public string? SecondPointDisplay
    {
        get => _secondPointDisplay;
        set => Set(ref _secondPointDisplay, value);
    }

    private string? _secondPointDisplay;

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

    /// <summary>Track width is 120 in the mock; this is the filled portion of it.</summary>
    public double CountdownWidth
    {
        get => _countdownWidth;
        set => Set(ref _countdownWidth, value);
    }

    private double _countdownWidth;

    public string? CountdownText
    {
        get => _countdownText;
        set => Set(ref _countdownText, value);
    }

    private string? _countdownText;

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
        CountdownWidth = other.CountdownWidth;
        CountdownText = other.CountdownText;
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

    /// <summary>Bearing and distance between the two points of an arity-2 row, in map units, never
    /// meters: converting to meters needs game-profile.json's units_to_meters, a served fact this
    /// dev viewer never fetches. Rendering meters here would be exactly the hardcoded game fact
    /// binding rule 5 forbids.</summary>
    private static string? Leg(BoardRow row)
    {
        if (row.Points.Count != 2)
        {
            return null;
        }

        var a = row.Points[0].Point;
        var b = row.Points[1].Point;
        var dx = (double)(b.X - a.X);
        var dy = (double)(b.Y - a.Y);
        var bearing = (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0;
        var distance = a.DistanceUnitsTo(b);
        return FormattableString.Invariant($"{bearing:000}deg {distance:0.0}u");
    }

    /// <summary>
    /// The sub-15s countdown from 06-overlay-ux.md. A row with minutes left carries no bar, which
    /// is what makes the bar mean something when it appears.
    /// </summary>
    private static (bool Show, double Width, string? Text) Countdown(BoardRow row, DateTimeOffset now)
    {
        const double track = 120;
        const double window = 15;

        var left = row.ExpiresAt - now;
        if (left <= TimeSpan.Zero || left.TotalSeconds > window)
        {
            return (false, 0, null);
        }

        var fraction = Math.Clamp(left.TotalSeconds / window, 0, 1);
        return (true, track * fraction, $"{FormatAge(left)} left");
    }

    public static BoardRowViewModel FromPrimary(BoardRow row, Guid viewerParticipantId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        var qualifier = Qualifier(row);
        var typeAndQualifier = qualifier.Length == 0 ? row.OverlayLabel : $"{row.OverlayLabel} {qualifier}";
        var primary = row.Points.Count > 0 ? FormatCoordinate(row.Points[0].Point) : string.Empty;
        var second = row.Points.Count > 1 ? $"-> {FormatCoordinate(row.Points[1].Point)}" : null;
        var mine = row.IsClaimedBy(viewerParticipantId);
        var heldByOther = row.RendersOnSecondaryStrip(viewerParticipantId);
        var urgent = row.Priority == Priority.Urgent && row.IsOpen;

        var (accent, word) = Accented(row, mine, urgent);
        var (showBar, barWidth, barText) = Countdown(row, now);

        return new BoardRowViewModel
        {
            SlotDisplay = row.Slot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            TypeAndQualifier = typeAndQualifier.ToUpperInvariant(),
            CoordinatesDisplay = primary,
            SecondPointDisplay = second,
            LegDisplay = Leg(row),
            Requester = row.RequestedByCallsign,
            AgeDisplay = FormatAge(now - row.CreatedAt),
            MetaExtra = row.ReleaseCount > 0
                ? $"RETRY x{row.ReleaseCount.ToString(CultureInfo.InvariantCulture)}"
                : null,
            TicketCode = row.TicketCode,
            StateWord = word,
            Accent = accent,
            RowOpacity = heldByOther ? 0.4 : 1.0,
            HasCountdown = showBar,
            CountdownWidth = barWidth,
            CountdownText = barText,
        };
    }

    /// <summary>
    /// One accent per row, in the precedence the row gallery draws: the viewer's own claim wins,
    /// then a warning about the point, then urgency.
    /// </summary>
    private static (RowAccent Accent, string? Word) Accented(BoardRow row, bool mine, bool urgent)
    {
        if (mine)
        {
            return (RowAccent.Mine, row.State == RequestState.InProgress ? "[YOU]" : "[YOU]");
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

    public static BoardRowViewModel FromSecondary(BoardRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        var primary = row.Points.Count > 0 ? FormatCoordinate(row.Points[0].Point) : string.Empty;

        return new BoardRowViewModel
        {
            SlotDisplay = string.Empty,
            TypeAndQualifier = row.OverlayLabel.ToUpperInvariant(),
            CoordinatesDisplay = primary,
            Requester = row.ClaimantCallsign ?? string.Empty,
            AgeDisplay = FormatAge(now - row.CreatedAt),
            TicketCode = row.TicketCode,
            Accent = RowAccent.None,
            StateWord = StripState(row, now),
        };
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
