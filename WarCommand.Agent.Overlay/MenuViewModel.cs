using System.Globalization;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Fire;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Overlay;

/// <summary>One offered digit and what it does.</summary>
public sealed record MenuOptionViewModel(
    string Digit,
    string Label,
    bool IsChosen = false,
    bool IsHighlighted = false,
    bool IsInfo = false)
{
    /// <summary>A chosen entry is marked, so several toggled on read as several.</summary>
    public string Mark => IsChosen ? "*" : string.Empty;

    /// <summary>
    /// The highlight marker, in a fixed column so the eye finds it in the same place on every
    /// level. The row's whole background carries the highlight; this is the second cue for anybody
    /// who cannot rely on the first.
    /// </summary>
    public string Caret => IsHighlighted ? ">" : " ";

    /// <summary>
    /// Read-only text draws no digit, and neither does an entry that has none.
    /// </summary>
    /// <remarks>
    /// Every line on the match page used to carry a 0, which read as a key you could press and
    /// could not. Then ARTILLERY, which is reachable by navigating and has no digit to spare,
    /// printed the sentinel that means "no digit" and the overlay read "-1 ARTILLERY".
    /// </remarks>
    public string DigitDisplay => IsInfo || !HasDigit ? string.Empty : Digit;

    /// <summary>False for read-only text and for a control reachable only by navigating to it.</summary>
    public bool HasDigit =>
        Digit.Length > 0 && Digit[0] is >= '0' and <= '9';
}

/// <summary>
/// The menu as the surface draws it: a title, the digits on offer, and the line being typed.
/// </summary>
/// <remarks>
/// A projection, never the machine itself. The overlay renders what it is handed and holds no menu
/// state of its own, so the only thing that can decide what a digit means is the state machine.
/// </remarks>
public sealed record MenuViewModel
{
    /// <summary>Nothing is open. The board draws normally.</summary>
    public static MenuViewModel Closed { get; } = new() { IsOpen = false, Title = string.Empty };

    public bool IsOpen { get; init; }

    /// <summary>
    /// The hold key is down but no menu has been opened yet. The surface has to say so: a held key
    /// that changes nothing on screen is indistinguishable from a key that is not working, which is
    /// exactly how it read.
    /// </summary>
    public bool IsArmed { get; init; }

    /// <summary>True when the panel should be on screen at all.</summary>
    public bool IsVisible => IsOpen || IsArmed;

    /// <summary>
    /// The slot the highlight is on, when it is on a board row. The row draws the highlight itself,
    /// so the menu panel must not also list the board.
    /// </summary>
    public int? HighlightedSlot { get; init; }



    /// <summary>Where you are: REQUEST, BOARD, the selected type, the slot being acted on.</summary>
    public required string Title { get; init; }

    /// <summary>The entries drawn above the board. Empty on a level that takes typed digits.</summary>
    public IReadOnlyList<MenuOptionViewModel> Options { get; init; } = [];

    /// <summary>The entries drawn below the board, so the on-screen order matches the list order.</summary>
    public IReadOnlyList<MenuOptionViewModel> Trailing { get; init; } = [];

    /// <summary>The coordinate or invite code being typed, already spaced for reading.</summary>
    public string? Typed { get; init; }

    /// <summary>
    /// The control legend, built from the live bindings so a rebound key is named correctly the
    /// moment it changes. Never a literal key string.
    /// </summary>
    public string Legend { get; init; } = string.Empty;

    /// <summary>
    /// The hold is down and nothing is open yet. Draws the one line that says what to press.
    /// </summary>
    public static MenuViewModel Armed(string prompt) => new()
    {
        IsOpen = false,
        IsArmed = true,
        Title = "HOLDING",
        Legend = prompt,
    };

    /// <summary>
    /// The options the PANEL draws. On the board the slot rows are excluded, because the real rows
    /// on the surface carry their own highlight and drawing them again is drawing the board twice.
    /// </summary>
    /// <remarks>
    /// Everything that is NOT a row still draws, which is how MORE stays reachable. Filtering the
    /// whole list at this level once hid MORE completely, and with it roles, the join code, the
    /// match, people, help, gun position, restart and account linking: every one of them lives
    /// behind it.
    /// </remarks>
    /// <summary>
    /// The bracket between the tool's two ends, or what is still missing.
    /// </summary>
    /// <remarks>
    /// Computed here rather than in the machine so the machine stays free of ballistics. Always a
    /// BRACKET and never a firing solution: player-measured tables, no altitude, flat earth.
    /// </remarks>
    private static string Bracket(MenuStateMachine menu)
    {
        if (menu.ToolGun is not { } gun)
        {
            return "SET YOUR GUN FIRST";
        }

        if (menu.ToolTarget is not { } target)
        {
            return "NOW SET THE TARGET";
        }

        var ballistics = BundledContracts.Ballistics().Current;
        var weapon = ballistics.Weapons.Count > 0 ? ballistics.Weapons[0] : null;
        if (weapon is null)
        {
            return string.Empty;
        }

        var solution = FireSolutionCalculator.Compute(
            new GunPosition(weapon.Id, gun, DateTimeOffset.UtcNow),
            target,
            weapon,
            ballistics,
            BundledContracts.GameProfile().Current,
            null,
            DateTimeOffset.UtcNow);

        return BoardRowViewModel.BracketLine(solution);
    }

    /// <summary>
    /// Drawn BELOW the board rather than above: the artillery tool and MORE.
    /// </summary>
    /// <remarks>
    /// Anything not listed here and not a board row is drawn above, so a new trailing entry that
    /// forgets to register lands in neither list and is invisible while still being selectable.
    /// </remarks>
    private static bool IsTrailing(string path) =>
        path is "home.fire" or "home.more";

    /// <summary>A board row, which draws itself on the surface rather than in the panel.</summary>
    private static bool IsRow(string path) =>
        path.StartsWith("board.", StringComparison.Ordinal);

    private static List<MenuOptionViewModel> Project(MenuStateMachine menu) =>
        [.. menu.Options.Select((o, i) => new MenuOptionViewModel(
            // The sentinel for "no digit" is negative and is not something to print.
            o.Digit < 0 ? string.Empty : o.Digit.ToString(CultureInfo.InvariantCulture),
            o.Label,
            o.IsChosen,
            i == menu.Highlight,
            o.IsInfo))];

    /// <summary>
    /// The entries drawn ABOVE the board. On the home list that is the request categories; on any
    /// other level it is the whole list, because no board rows are interleaved with it.
    /// </summary>
    private static List<MenuOptionViewModel> LeadingFor(MenuStateMachine menu)
    {
        var all = Project(menu);
        if (menu.Level != MenuLevel.Root)
        {
            return all;
        }

        // The request list is drawn only while the highlight is IN it. Drawing it always made DOWN
        // from rest look exactly like UP: the board was highlighted underneath, but the categories
        // filled the panel above it and the surface read as the request menu either way.
        if (!menu.HighlightIsARequest)
        {
            return [];
        }

        // A FILTER, not a prefix. Taking everything before the first row assumed an ordering, and
        // an entry that fell outside both this and the trailing list was drawn nowhere while still
        // being selectable: the highlight landed on something invisible.
        var entries = menu.Options;
        var leading = new List<MenuOptionViewModel>(all.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!IsRow(entries[i].Path) && !IsTrailing(entries[i].Path))
            {
                leading.Add(all[i]);
            }
        }

        return leading;
    }

    /// <summary>
    /// The entries drawn BELOW the board, which is MORE and nothing else. Splitting the panel is
    /// what makes one list read as one list: the order on screen is the order you move through, so
    /// MORE sits under the rows rather than above them where it looked like a request.
    /// </summary>
    private static List<MenuOptionViewModel> TrailingFor(MenuStateMachine menu)
    {
        if (menu.Level != MenuLevel.Root)
        {
            return [];
        }

        var all = Project(menu);
        var entries = menu.Options;
        var trailing = new List<MenuOptionViewModel>(1);
        for (var i = 0; i < entries.Count; i++)
        {
            if (IsTrailing(entries[i].Path))
            {
                trailing.Add(all[i]);
            }
        }

        return trailing;
    }

    /// <summary>Builds the projection from the machine.</summary>
    public static MenuViewModel From(MenuStateMachine menu, string legend = "")
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (!menu.IsOpen)
        {
            return Closed;
        }

        return new MenuViewModel
        {
            Legend = legend,
            HighlightedSlot = menu.HighlightedSlot,
            IsOpen = true,
            Title = TitleFor(menu),
            Options = LeadingFor(menu),
            Trailing = TrailingFor(menu),
            Typed = TypedFor(menu),
        };
    }

    /// <summary>
    /// What is being typed, in the shape it will be submitted as.
    /// </summary>
    /// <remarks>
    /// The coordinate takes no punctuation: eight digits, four per axis, the last two of each the
    /// decimals, so 12213441 is 12.21 34.41. Shown as a raw run of digits that is invisible, and
    /// it reads as the coordinate not going in at all. The unfilled places are underscores so the
    /// shape of what is wanted is on screen before the first key.
    /// </remarks>
    private static string? TypedFor(MenuStateMachine menu)
    {
        // The artillery tool shows the BRACKET between its two ends, recomputed on every read.
        if (menu.Level == MenuLevel.FireTool)
        {
            return Bracket(menu);
        }

        // The confirm level shows the POINT. A screen read can be plausible and wrong, and the only
        // person who can catch that is the one looking at the map it came from, so it has to be on
        // screen before the key is released rather than after the row reaches the board.
        if (menu.Level == MenuLevel.Confirm && menu.Point is { } point)
        {
            return FormattableString.Invariant($"x{point.X:0.00}  y{point.Y:0.00}");
        }

        var wanted = menu.DigitsWanted;
        if (wanted == 0)
        {
            return menu.Digits.Count == 0
                ? null
                : string.Concat(menu.Digits.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        }

        var typed = menu.Digits.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
        var slots = new List<string>(wanted);
        for (var i = 0; i < wanted; i++)
        {
            slots.Add(i < typed.Count ? typed[i] : "_");
        }

        if (menu.Level != MenuLevel.Coordinate)
        {
            return string.Concat(slots);
        }

        var per = menu.DigitsPerAxis;
        return $"{Axis(slots, 0, per)}  {Axis(slots, per, per)}";
    }

    /// <summary>One axis, with the decimal point where the machine puts it: the last two places.</summary>
    private static string Axis(IReadOnlyList<string> slots, int start, int count)
    {
        var whole = string.Concat(slots.Skip(start).Take(count - 2));
        var fraction = string.Concat(slots.Skip(start + count - 2).Take(2));
        return $"{whole}.{fraction}";
    }

    private static string TitleFor(MenuStateMachine menu) => menu.Level switch
    {
        MenuLevel.Root => "WARCOMMAND",
        MenuLevel.Branch => menu.Selection?.Label.ToUpperInvariant() ?? "REQUEST",
        MenuLevel.Coordinate => menu.CurrentPointLabel,
        MenuLevel.FireTool => "ARTILLERY",
        MenuLevel.FireTarget => "TARGET",
        MenuLevel.GunPosition => "GUN POSITION",
        MenuLevel.Confirm => menu.SelectedTypeId is { } type ? type.ToUpperInvariant() : "CONFIRM",
        MenuLevel.Board => "BOARD",
        MenuLevel.BoardAction => "SLOT  PICK A VERB",
        MenuLevel.More => "MORE",
        MenuLevel.Join => "JOIN CODE",
        MenuLevel.Help => "HELP",
        MenuLevel.Roles => "ROLES  A DIGIT TOGGLES ONE",
        MenuLevel.Match => "MATCH",
        MenuLevel.People => "PEOPLE",
        _ => string.Empty,
    };
}
