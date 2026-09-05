using System.Globalization;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Input;

/// <summary>
/// The four key classes a menu may swallow, and everything else. Movement, leaning, stance,
/// vehicle controls, comms and the map key all keep working while a menu is open.
/// </summary>
public enum MenuKeyClass
{
    /// <summary>0 to 9. Swallowed.</summary>
    Digit,

    /// <summary>Swallowed.</summary>
    Escape,

    /// <summary>Swallowed.</summary>
    Backspace,

    /// <summary>The PTT key itself. Swallowed.</summary>
    PushToTalk,

    /// <summary>Everything else. Passed straight through to the game.</summary>
    Other,
}

/// <summary>Where an open menu is.</summary>
public enum MenuLevel
{
    Closed,

    /// <summary>
    /// The request surface: ARTILLERY, TOOLS and the request categories. Reached by UP from rest,
    /// and it holds no board rows. 0 is TOOLS.
    /// </summary>
    Root,

    /// <summary>
    /// The board surface: the occupied slot rows and nothing else. Reached by DOWN from rest. It is
    /// not nested in <see cref="Root"/> and cannot be walked into from it.
    /// </summary>
    Board,

    /// <summary>A compiled category or sub-category.</summary>
    Branch,

    /// <summary>Digit entry for the point. M1's only key-driven coordinate source.</summary>
    Coordinate,

    /// <summary>Modifiers, and the only level a release commits from.</summary>
    Confirm,

    /// <summary>The verbs available against one selected row.</summary>
    BoardAction,

    /// <summary>0 BOARD > 0. Every panel, plus join and gun position. Hand authored.</summary>
    More,

    /// <summary>Six digits, then commit. No confirm step.</summary>
    Join,

    /// <summary>The key reference, drawn while the key is held like every other level.</summary>
    Help,

    /// <summary>Your role subscriptions. A digit toggles one.</summary>
    Roles,

    /// <summary>The match: group, label, invite code, headcount. Read only.</summary>
    Match,

    /// <summary>Who is on the match. Read only.</summary>
    People,

    /// <summary>
    /// The range calculator: origin, target, the range between them, and the weapon the range is
    /// judged against. Both ends read the map in place; there is no page to walk into for either.
    /// </summary>
    RangeTool,
}

/// <summary>
/// One calculator the range page can be set to, cycled with the two mode keys.
/// </summary>
/// <remarks>
/// Built from the served ballistics rather than written here: RAW plus one per weapon, each
/// carrying that weapon's own envelope. No range limit is a constant in this assembly.
/// </remarks>
public sealed record RangeMode(string Id, string Label, decimal? MinRangeM = null, decimal? MaxRangeM = null)
{
    /// <summary>Raw range, judged against nothing. Always the first mode.</summary>
    public static RangeMode Raw { get; } = new("raw", "SNIPING");

    /// <summary>What a caller with no ballistics to hand gets: the raw range and no envelope.</summary>
    public static IReadOnlyList<RangeMode> Fallback { get; } = [Raw];

    /// <summary>True while this range is outside what the mode can reach.</summary>
    public bool IsOutOfRange(decimal metres) =>
        (MinRangeM is { } min && metres < min) || (MaxRangeM is { } max && metres > max);
}

/// <summary>One selectable entry. Compiled from menu_paths, never hand drawn.</summary>
public sealed record MenuEntry
{
    public required int Digit { get; init; }

    /// <summary>The catalog path this entry sits at, 'fire.1' or 'build.2.5'.</summary>
    public required string Path { get; init; }

    public required string Label { get; init; }

    /// <summary>The type this entry creates, inherited from its nearest ancestor that names one.</summary>
    public string? TypeId { get; init; }

    public string? SupplyKindId { get; init; }

    public string? StructureKindId { get; init; }

    /// <summary>Hand-authored board verb, on the 0 BOARD branch only.</summary>
    public string? VerbId { get; init; }

    /// <summary>True while this entry is toggled on. Only the modifier level has such a thing.</summary>
    public bool IsChosen { get; init; }

    /// <summary>
    /// Read-only text rather than a control. Draws no digit and the highlight skips over it, so a
    /// page can carry information without every line pretending to be pressable.
    /// </summary>
    public bool IsInfo { get; init; }

    public IReadOnlyList<MenuEntry> Children { get; init; } = [];

    public bool IsLeaf => Children.Count == 0;
}

/// <summary>
/// The request menu tree, compiled from menu_categories and menu_paths at the same time as the
/// grammar and from the same pruning, so a new request type reaches the menu with no separate edit.
/// </summary>
/// <remarks>
/// A type that reaches the grammar and not the menu is a feature that does not exist for anybody
/// who has voice turned off.
/// </remarks>
public sealed class MenuTree
{
    private MenuTree(IReadOnlyList<MenuEntry> root)
    {
        Root = root;
    }

    /// <summary>The nine categories, ascending by digit. 0 BOARD is not compiled; it is authored.</summary>
    public IReadOnlyList<MenuEntry> Root { get; }

    /// <summary>Compiles the tree. Types whose target roles are not enabled are absent.</summary>
    public static MenuTree Compile(Catalog catalog, IReadOnlyCollection<string>? enabledRoleIds = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var categories = new Dictionary<string, Builder>(StringComparer.Ordinal);
        foreach (var (name, digit) in catalog.MenuCategories)
        {
            categories[name] = new Builder(digit, name, name.ToUpperInvariant());
        }

        foreach (var type in catalog.RequestTypes)
        {
            if (enabledRoleIds is { Count: > 0 } && !type.TargetRoles.Any(enabledRoleIds.Contains))
            {
                continue;
            }

            foreach (var path in type.MenuPaths)
            {
                var node = Walk(categories, path);
                if (node is null)
                {
                    continue;
                }

                node.TypeId = type.Id;

                // A branch this type names explicitly wins over its own label, which is what stops
                // one type on two branches drawing the same word twice.
                node.Label = type.MenuPathLabels.TryGetValue(path, out var branch)
                    ? branch
                    : node.Label ?? type.OverlayLabel;
            }
        }

        AttachKinds(categories, catalog.SupplyKinds, supply: true);
        AttachKinds(categories, catalog.StructureKinds, supply: false);

        var root = categories.Values
            .OrderBy(c => c.Digit)
            .Select(c => c.Build(null))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        return new MenuTree(root);
    }

    /// <summary>The entry at a catalog path, or null.</summary>
    public MenuEntry? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<MenuEntry> level = Root;
        MenuEntry? found = null;

        foreach (var entry in level)
        {
            found = Search(entry, path);
            if (found is not null)
            {
                return found;
            }
        }

        return found;
    }

    private static MenuEntry? Search(MenuEntry entry, string path)
    {
        if (string.Equals(entry.Path, path, StringComparison.Ordinal))
        {
            return entry;
        }

        foreach (var child in entry.Children)
        {
            var found = Search(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void AttachKinds(
        Dictionary<string, Builder> categories,
        IReadOnlyList<KindDef> kinds,
        bool supply)
    {
        foreach (var kind in kinds)
        {
            if (kind.MenuPath is not { } path)
            {
                continue;
            }

            var node = Walk(categories, path);
            if (node is null)
            {
                continue;
            }

            if (supply)
            {
                node.SupplyKindId = kind.Id;
            }
            else
            {
                node.StructureKindId = kind.Id;
            }

            node.Label = kind.OverlayLabel;
        }
    }

    private static Builder? Walk(Dictionary<string, Builder> categories, string path)
    {
        var parts = path.Split('.');
        if (parts.Length < 2 || !categories.TryGetValue(parts[0], out var node))
        {
            return null;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var digit))
            {
                return null;
            }

            var childPath = string.Join('.', parts.Take(i + 1));
            if (!node.Children.TryGetValue(digit, out var child))
            {
                child = new Builder(digit, childPath, null);
                node.Children[digit] = child;
            }

            node = child;
        }

        return node;
    }

    private sealed class Builder(int digit, string path, string? label)
    {
        public int Digit { get; } = digit;

        public string Path { get; } = path;

        public string? Label { get; set; } = label;

        public string? TypeId { get; set; }

        public string? SupplyKindId { get; set; }

        public string? StructureKindId { get; set; }

        public SortedDictionary<int, Builder> Children { get; } = [];

        public MenuEntry? Build(string? inheritedTypeId)
        {
            var typeId = TypeId ?? inheritedTypeId;
            var children = Children.Values
                .Select(c => c.Build(typeId))
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();

            // A branch that also names a type keeps the bare type reachable on the free digit 0,
            // because 'fortify' with no structure is legal and must not be voice-only.
            if (children.Count > 0 && TypeId is not null && !Children.ContainsKey(0))
            {
                children.Insert(0, new MenuEntry
                {
                    Digit = 0,
                    Path = $"{Path}.0",
                    Label = Label ?? typeId!,
                    TypeId = TypeId,
                });
            }

            if (children.Count == 0 && typeId is null)
            {
                return null;
            }

            return new MenuEntry
            {
                Digit = Digit,
                Path = Path,
                Label = Label ?? typeId ?? Path.ToUpperInvariant(),
                TypeId = typeId,
                SupplyKindId = SupplyKindId,
                StructureKindId = StructureKindId,
                Children = children,
            };
        }
    }
}

/// <summary>What one menu event produced.</summary>
public abstract record MenuOutcome
{
    private protected MenuOutcome()
    {
    }

    /// <summary>Nothing happened. A digit with no entry at that position is ignored silently.</summary>
    public static MenuOutcome None { get; } = new MenuNothing();
}

/// <summary>The event changed nothing the host must act on.</summary>
public sealed record MenuNothing : MenuOutcome;

/// <summary>The menu moved. The host redraws.</summary>
public sealed record MenuNavigated(MenuLevel Level) : MenuOutcome;

/// <summary>The menu closed with nothing selected.</summary>
public sealed record MenuDiscarded(string Reason) : MenuOutcome;

/// <summary>
/// A complete key-driven request. For a two-point type this is point 0 only, and the host hands off
/// to the existing awaiting-point flow rather than committing.
/// </summary>
public sealed record MenuRequestReady(
    string TypeId,
    string? SupplyKindId,
    string? StructureKindId,
    MapPoint Point,
    IReadOnlyList<string> Modifiers) : MenuOutcome
{
    /// <summary>
    /// Every point the type takes, in ordinal order. One for most types, two for a transport or an
    /// escort.
    /// </summary>
    /// <remarks>
    /// <see cref="Point"/> is the first of these and stays for the callers that only ever want the
    /// target. The submit must use THIS: sending one point for a two-point type is rejected with
    /// point_count_mismatch, which made every TRANSPORT, LIFT and ESCORT request impossible.
    /// </remarks>
    public IReadOnlyList<MapPoint> Points { get; init; } = [Point];
}

/// <summary>Six digits off the keypad. The cold-start path for anybody with no microphone.</summary>
public sealed record MenuJoinReady(string InviteCode) : MenuOutcome;

/// <summary>A verb against one slot, executed immediately. Board actions never preview.</summary>
public sealed record MenuBoardAction(string VerbId, int Slot) : MenuOutcome
{
    /// <summary>Which way to walk the rounds, on adjust only.</summary>
    /// <remarks>
    /// Carried here because the voice path parses it and the board path needs it. Dropping it made
    /// "adjust 3 over 50" unroutable, so the whole spotter correction loop was unreachable.
    /// </remarks>
    public AdjustDirection? Direction { get; init; }

    /// <summary>How far, on adjust only.</summary>
    public int? Metres { get; init; }
}

/// <summary>
/// A panel off the MORE page: help, roles, match, people, restart, link. The panel owns the digits
/// from here and outlives the held key, which is why the menu closes as it hands one over.
/// </summary>
public sealed record MenuPanelRequested(string PanelId) : MenuOutcome;

/// <summary>One role subscription toggled from the ROLES page. The agent asks the server.</summary>
public sealed record MenuRoleToggled(string RoleId) : MenuOutcome;

/// <summary>
/// The select key was pressed on the coordinate level: read the map now. The app answers by
/// calling AcceptReadCoordinate on the machine, or by reporting a refusal on the surface.
/// </summary>
public sealed record MenuCoordinateReadRequested : MenuOutcome;

/// <summary>The invite code was taken from the match page. The app puts it on the clipboard.</summary>
public sealed record MenuInviteCopied(string InviteCode) : MenuOutcome;

/// <summary>ARTILLERY > GUN. Where the viewer's gun is, for the tool and for every row bracket.</summary>
public sealed record MenuGunPositionSet(MapPoint Point) : MenuOutcome;

/// <summary>
/// ARTILLERY > CLEAR. The gun is forgotten, so no row may draw a bracket from it either.
/// </summary>
public sealed record MenuGunPositionCleared : MenuOutcome;

/// <summary>
/// What the MORE page is allowed to offer this person right now. Its digits are fixed: an entry
/// nobody can use is absent, never renumbered, so a digit learned once stays learned.
/// </summary>
/// <summary>What one board slot holds, for deciding which verbs it can honour.</summary>
/// <param name="State">The row's state.</param>
/// <param name="ClaimedByViewer">True when the viewer is the one holding it.</param>
/// <param name="RequestedByViewer">True when the viewer is the one who asked for it.</param>
public readonly record struct SlotState(
    RequestState State,
    bool ClaimedByViewer,
    bool RequestedByViewer = false);

public sealed record MenuContext
{
    /// <summary>Slots holding a row. Only these are selectable at the board level.</summary>
    public IReadOnlyCollection<int> OccupiedSlots { get; init; } = [];

    /// <summary>
    /// What each occupied slot holds, so the verb list can offer only what the row will accept.
    /// </summary>
    /// <remarks>
    /// Absent means the old behaviour: every verb on every row. That offered START, DONE and
    /// RELEASE on an open row nobody had claimed, and ACCEPT on a row already yours. The server
    /// refuses each of them, so the press did nothing and the surface said nothing either.
    /// </remarks>
    public IReadOnlyDictionary<int, SlotState> Slots { get; init; } =
        new Dictionary<int, SlotState>();

    /// <summary>Admin and owner only. Restart is board-wide and destructive.</summary>
    public bool CanRestart { get; init; }

    /// <summary>True only while the one-time link prompt is showing.</summary>
    public bool LinkPromptPending { get; init; }

    /// <summary>Roles this group runs, in catalog order. The ROLES page offers these.</summary>
    public IReadOnlyList<string> EnabledRoleIds { get; init; } = [];

    /// <summary>Roles this participant currently receives. Marked on the ROLES page.</summary>
    public IReadOnlyCollection<string> SubscribedRoleIds { get; init; } = [];

    /// <summary>Group and deployment, for MATCH. Null draws nothing rather than an empty line.</summary>
    public string? GroupName { get; init; }

    public string? DeploymentLabel { get; init; }

    public string? InviteCode { get; init; }

    public int MemberCount { get; init; }

    /// <summary>Callsigns on the match, for PEOPLE.</summary>
    public IReadOnlyList<string> Roster { get; init; } = [];

    /// <summary>The keys in force, so HELP names the keys this player actually has.</summary>
    public MenuKeys Keys { get; init; } = MenuKeys.Defaults;

    /// <summary>The range calculator's modes, in cycle order, from the served ballistics.</summary>
    public IReadOnlyList<RangeMode> RangeModes { get; init; } = RangeMode.Fallback;
}

/// <summary>
/// The bound keys, by their labels, for any page that has to name one.
/// </summary>
/// <remarks>
/// Every one of them is rebindable, so HELP that hardcoded them would be wrong for anybody who
/// changed one, which is the reader most in need of it.
/// </remarks>
public sealed record MenuKeys(
    string Menu,
    string Up,
    string Down,
    string Select,
    string Back,
    string Tools = "E",
    string Cycle = "Q")
{
    /// <summary>What ships. Only used where the real bindings are not to hand.</summary>
    public static MenuKeys Defaults { get; } = new("CAPS LOCK", "W", "S", "D", "A", "E", "Q");
}

/// <summary>Timings and sizes for the menu. None of them is a fact about the game.</summary>
public sealed record MenuOptions
{
    /// <summary>
    /// How long an open menu survives with the hold key NOT down. This is a stuck-key guard, not
    /// a working timer: while the key is held there is no timeout at all.
    /// </summary>
    /// <remarks>
    /// It used to run whether the key was held or not, so a menu closed itself out from under
    /// somebody who was still holding the key and reading it. Working the overlay is exactly the
    /// case where no input arrives for a while. The only state that must not persist is an open
    /// menu with nothing held, because that swallows the digit row, Escape and Backspace across
    /// every window on the machine, so that is the only state this closes.
    /// </remarks>
    public int OrphanTimeoutMs { get; init; } = 1500;

    /// <summary>Two integer digits and two decimals, per axis. The decimal point is implied.</summary>
    public int DigitsPerAxis { get; init; } = 4;

    public int InviteCodeDigits { get; init; } = 6;

    /// <summary>Written verbatim to request_points.source.</summary>
    public string TypedGridSourceId { get; init; } = "typed_grid";
}

/// <summary>
/// The menu machine. Lives beside the PTT machine, pure, and driven by the same events.
/// </summary>
/// <remarks>
/// The coordinate was snapshotted on key DOWN, before the menu ever opened, and navigating the menu
/// does not resample it.
/// </remarks>
public sealed class MenuStateMachine
{
    /// <summary>0 at the root opens the board, and 0 at the board opens MORE. Never a slot.</summary>
    private const int ZeroDigit = 0;

    /// <summary>An entry the digit path cannot reach. The nav keys dispatch on Path, not Digit.</summary>
    private const int NoDigit = -1;

    /// <summary>The range page's two ends, in the order RangeToolEntries builds them.</summary>
    private const int GunIndex = 0;

    private const int TargetIndex = 1;

    /// <summary>
    /// How many entries a page may draw. Nine, because a tenth would need a digit there is not one
    /// of: 0 is reserved everywhere for the thing that is not a row.
    /// </summary>
    private const int MaxPageEntries = 9;

    /// <remarks>
    /// 2 stays empty where START was. Taking a job starts it, so the verb had nothing left to do,
    /// and a digit learned once stays learned rather than sliding up into a freed slot.
    /// </remarks>
    /// <summary>
    /// Every row verb, in the order they are offered. The digit is assigned at render, from the
    /// position in the FILTERED list, so what a row can honour always lands on 1 upward.
    /// </summary>
    /// <remarks>
    /// They used to carry fixed digits so a verb kept its number everywhere, which is the better
    /// rule when every number is reachable. They are not: a hand holding CapsLock covers 1 to 5 on
    /// the number row and nothing past it, so MUTE on 6 and COPY on 7 were dead keys. The lists are
    /// filtered by row state and the states are disjoint, so ordering by what that state needs most
    /// keeps every offer inside five and keeps it stable: on an open row 1 is always ACCEPT, on a
    /// row you hold 1 is always DONE.
    /// <para>
    /// ADJUST is absent on purpose. It carries a direction and a distance that this surface has no
    /// way to ask for, so offering it here would be a press that always refuses. It stays a voice
    /// verb until the menu can collect a direction.
    /// </para>
    /// </remarks>
    private static readonly (string VerbId, string Label)[] BoardVerbs =
    [
        ("accept", "ACCEPT"),
        ("done", "DONE"),
        ("cancel", "CANCEL"),
        ("rounds_away", "ROUNDS AWAY"),
        ("release", "RELEASE"),
        ("pass", "PASS"),
        ("copy", "COPY"),
        ("mute", "MUTE"),
    ];

    /// <summary>
    /// How many verbs a row may offer. Five, because that is how far a left hand reaches while its
    /// pinky is holding the menu key open.
    /// </summary>
    private const int MaxRowVerbs = 5;

    /// <summary>
    /// The MORE page. Fixed digits, hand authored, and the only route to every panel now that the
    /// chords are gone. A gated entry is absent rather than shown-and-dead.
    /// </summary>
    private static readonly (int Digit, string Id, string Label)[] MoreEntries =
    [
        (1, "help", "HELP"),
        (2, "roles", "ROLES"),
        (3, "match", "MATCH"),
        (4, "people", "PEOPLE"),
        (5, "range", "RANGE"),
        // 5 held GUN HERE before ARTILLERY took it. It set the same gun position the ARTILLERY tool sets,
        // by a second route with its own way back out, so backing out of a gun read landed you
        // somewhere different depending on which entry you had come in through.
        (6, "join", "JOIN CODE"),
        // 7 stays empty where RESTART was. A digit learned once stays learned, so LINK ACCOUNT
        // keeps the 8 it has always had rather than sliding up into a freed slot.
        (8, "link", "LINK ACCOUNT"),
    ];

    private readonly MenuTree _tree;
    private readonly Catalog _catalog;
    private readonly MenuOptions _options;
    private readonly List<MenuEntry> _path = [];
    private readonly List<string> _modifiers = [];
    private readonly List<int> _digits = [];

    private MapPoint? _snapshot;
    private MenuContext _context = new();
    private DateTimeOffset _lastInput;
    private MenuLevel _level = MenuLevel.Closed;

    // The range calculator's two ends. Kept on the machine so both survive a read and the page can
    // be re-entered without losing what was already set.
    private MapPoint? _toolGun;
    private MapPoint? _toolTarget;

    // Which end the next screen read fills. Set by pressing that end, cleared by the read, so both
    // ends are one press plus one read rather than a page to walk into and back out of.
    private string? _rangeAwaiting;

    // An index into the context's modes, kept across opens so the crew's weapon stays chosen.
    private int _rangeMode;

    // Points collected for the draft, in ordinal order. A two-point type wants both before it can
    // be submitted at all: TRANSPORT, LIFT and ESCORT were unsubmittable because the machine had
    // no notion of arity and the submit always sent exactly one.
    private readonly List<MapPoint> _points = [];

    // The subscribed roles as the USER has them right now, seeded from the context at open and
    // flipped the instant a toggle is pressed. Reading the context directly meant a toggled row
    // redrew identically, so the only feedback was the row you were already looking at not changing.
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private int _selectedSlot;

    public MenuStateMachine(MenuTree tree, Catalog catalog, MenuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(catalog);
        _tree = tree;
        _catalog = catalog;
        _options = options ?? new MenuOptions { InviteCodeDigits = catalog.GrammarRules.InviteCodeDigits };
    }

    /// <summary>
    /// Where the menu is. Changing level resets <see cref="Highlight"/> to the first option, so the
    /// wheel always starts at the top of whatever just appeared rather than at a stale index into a
    /// list that no longer exists.
    /// </summary>
    public MenuLevel Level
    {
        get => _level;
        private set
        {
            if (_level == value)
            {
                return;
            }

            _level = value;

            // The first line you can actually press, never blindly index 0. A level that opens
            // with the highlight parked on read-only text offers a select key that does nothing,
            // and the reader has no way to tell that from a broken key.
            Highlight = FirstSelectable();
        }
    }

    /// <summary>
    /// The first entry at this level that can be pressed, or -1 when the level is pure reference
    /// text. Nothing is highlighted in that case, which is the honest drawing of it.
    /// </summary>
    private int FirstSelectable()
    {
        var options = CurrentOptions();
        for (var i = 0; i < options.Count; i++)
        {
            if (!options[i].IsInfo)
            {
                return i;
            }
        }

        return NoHighlight;
    }

    /// <summary>No line is highlighted, because no line at this level can be pressed.</summary>
    public const int NoHighlight = -1;

    /// <summary>The entry currently drilled into, or null at the root.</summary>
    public MenuEntry? Selection => _path.Count > 0 ? _path[^1] : null;

    /// <summary>What the overlay draws at this level.</summary>
    public IReadOnlyList<MenuEntry> Options => CurrentOptions();

    /// <summary>
    /// Which option the wheel is sitting on, an index into <see cref="Options"/>. Always valid
    /// while a menu is open with anything to pick, so a renderer can highlight it without a guard.
    /// </summary>
    /// <remarks>
    /// The tree is unchanged and every entry keeps its digit. Scrolling picks a POSITION and Select
    /// presses that position's digit, so the mouse and the keyboard drive one machine and a new
    /// request type reaches both with no extra edit.
    /// </remarks>
    public int Highlight { get; private set; }

    /// <summary>
    /// The point the draft holds, from a screen read or from typed digits. Null until it has one.
    /// </summary>
    /// <remarks>
    /// Drawn on the confirm level so a wrong reading is visible BEFORE it is committed. A screen
    /// read can be plausible and wrong, and the only person who can catch that is the one looking
    /// at the map it came from.
    /// </remarks>
    public MapPoint? Point => CurrentPoint();

    /// <summary>Digits typed at the coordinate or join level.</summary>
    public IReadOnlyList<int> Digits => _digits;

    /// <summary>How many digits this level still wants, or 0 when it wants none.</summary>
    public int DigitsWanted => Level switch
    {
        MenuLevel.Coordinate => _options.DigitsPerAxis * 2,
        MenuLevel.Join => _options.InviteCodeDigits,
        _ => 0,
    };

    /// <summary>Digits per axis, so a renderer can put the decimal point where this class does.</summary>
    public int DigitsPerAxis => _options.DigitsPerAxis;

    /// <summary>Modifier ids toggled on at the confirm level.</summary>
    public IReadOnlyList<string> Modifiers => _modifiers;

    /// <summary>The type the confirm level will create, or null.</summary>
    public string? SelectedTypeId => _path.LastOrDefault(e => e.TypeId is not null)?.TypeId;

    /// <summary>
    /// The digit each MORE entry holds, so a hint can name a route without a second hand-written
    /// list of digits to drift out of step with this one.
    /// </summary>
    public static IReadOnlyDictionary<string, int> MoreDigits { get; } =
        MoreEntries.ToDictionary(e => e.Id, e => e.Digit, StringComparer.Ordinal);

    /// <summary>
    /// Every row verb, in offer order, for a screen that documents the keyboard rather than running
    /// it. Read from the same table the menu dispatches on, so the two cannot drift. There is no
    /// digit here: a row's verbs are numbered from what that row can honour.
    /// </summary>
    public static IReadOnlyList<string> BoardVerbList { get; } =
        [.. BoardVerbs.Select(v => v.Label)];

    /// <summary>The MORE page, same rule as <see cref="BoardVerbList"/>.</summary>
    public static IReadOnlyList<(int Digit, string Label)> MoreList { get; } =
        [.. MoreEntries.Select(e => (e.Digit, e.Label))];

    public bool IsOpen => Level != MenuLevel.Closed;

    /// <summary>
    /// True once the menu has stopped following the held key. Typing a six digit code with a key
    /// held down is not an interaction anybody completes, so JOIN detaches and Escape closes it.
    /// </summary>
    /// <summary>
    /// Always false. The menu never outlives the key that opened it.
    /// </summary>
    /// <remarks>
    /// A latch existed for the six digit join code and briefly for a tap-to-open. Either one left
    /// the digit row, Escape and Backspace swallowed with nothing held down, across every window on
    /// the machine, and a user cannot see that state or reason about it. Kept as a property so the
    /// invariant is stated and testable rather than merely absent.
    /// </remarks>
    public static bool IsLatched => false;

    /// <summary>
    /// Exactly four key classes are swallowed while a menu is open. This is a safety rule: a menu
    /// that ate W for a second and a half would get somebody killed.
    /// </summary>
    public static bool Swallows(MenuKeyClass key) => key is not MenuKeyClass.Other;

    /// <summary>
    /// Opens at the root. <paramref name="snapshot"/> is the key-down coordinate; when it is
    /// present the coordinate level is pre-filled and skipped.
    /// </summary>
    public MenuOutcome Open(DateTimeOffset now, MapPoint? snapshot = null, MenuContext? context = null)
    {
        _path.Clear();
        _modifiers.Clear();
        _digits.Clear();
        _points.Clear();
        _selectedSlot = 0;
        _snapshot = snapshot;
        _context = context ?? new MenuContext();

        // Reseeded on every open, so a toggle rejected by the server is corrected the next time the
        // key goes down rather than persisting as a local lie.
        _subscribed.Clear();
        foreach (var id in _context.SubscribedRoleIds)
        {
            _subscribed.Add(id);
        }
        _lastInput = now;
        Level = MenuLevel.Root;
        return new MenuNavigated(Level);
    }

    /// <summary>
    /// Moves the highlight. Negative is up the list, positive is down, one per key press.
    /// Scrolling up past the first row of the board rises into the root, which is the one place a
    /// level has somewhere to go rather than clamping.
    /// </summary>
    public MenuOutcome Scroll(int notches, DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed || notches == 0)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;

        var options = Options;
        if (options.Count == 0)
        {
            return MenuOutcome.None;
        }

        // A level with nothing pressable on it does not move: there is nowhere to move to, and
        // parking the highlight on reference text would offer a select key that does nothing.
        if (options.All(o => o.IsInfo))
        {
            Highlight = NoHighlight;
            return new MenuNavigated(Level);
        }

        var next = Highlight + notches;

        // Every end wraps. There are no crossover edges any more: requests, rows and MORE are one
        // list, so moving between them is just moving.
        Highlight = ((next % options.Count) + options.Count) % options.Count;

        // Step over anything that is not a control, in the direction of travel. A highlight sitting
        // on a read-only line is a press that does nothing.
        var step = notches < 0 ? -1 : 1;
        for (var guard = 0; guard < options.Count && options[Highlight].IsInfo; guard++)
        {
            Highlight = ((Highlight + step) % options.Count + options.Count) % options.Count;
        }

        return new MenuNavigated(Level);
    }

    /// <summary>
    /// Presses the highlighted option's digit. This is the select key, and it is the only commit
    /// the nav path has: nothing is chosen by moving onto it.
    /// </summary>
    public MenuOutcome Select(DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed)
        {
            return MenuOutcome.None;
        }

        // The coordinate level holds no options: select is a request to read the map.
        if (Level is MenuLevel.Coordinate)
        {
            _lastInput = now;
            return new MenuCoordinateReadRequested();
        }

        var options = Options;

        var chosen = Highlight >= 0 && Highlight < options.Count ? options[Highlight] : null;
        if (chosen is null)
        {
            return MenuOutcome.None;
        }

        if (chosen.IsInfo)
        {
            return MenuOutcome.None;
        }

        if (Level is MenuLevel.RangeTool)
        {
            return PressRangeEntry(chosen, now);
        }

        if (Level is MenuLevel.Match)
        {
            _lastInput = now;

            if (chosen.Path == "match.invite" && _context.InviteCode is { } invite)
            {
                return new MenuInviteCopied(invite);
            }

            if (chosen.Path == "match.restart")
            {
                Reset();
                return new MenuPanelRequested("restart");
            }

            return MenuOutcome.None;
        }

        // On the board surface the digit is the slot, and selecting a row opens its verbs.
        if (Level is MenuLevel.Board)
        {
            _lastInput = now;
            return SelectRow(chosen.Digit);
        }

        return Digit(chosen.Digit, now);
    }

    /// <summary>
    /// Opens the BOARD surface with its first row highlighted. This is what DOWN does from rest:
    /// taking a job is the common case and it must not go through the request menu to get there.
    /// </summary>
    /// <remarks>
    /// A level of its own, not an index into the home list. Nothing here walks into the requests;
    /// BACK is the only way off it.
    /// </remarks>
    public MenuOutcome OpenOnBoard(DateTimeOffset now, MenuContext? context = null, MapPoint? snapshot = null)
    {
        // The SAME snapshot the upward open carries. Passing null here meant the direction of your
        // first keypress decided whether a request you built afterwards already had its coordinate:
        // opening down then walking up asked you to type a grid that opening up had in hand.
        var opened = Open(now, snapshot, context);
        if (opened is MenuDiscarded)
        {
            return opened;
        }

        Level = MenuLevel.Board;
        return new MenuNavigated(Level);
    }

    /// <summary>The slot the highlight is sitting on, or null when it is not on a row.</summary>
    /// <remarks>
    /// Read by the surface so the HIGHLIGHT LANDS ON THE REAL BOARD ROW rather than on a copy of
    /// the board drawn inside a menu panel.
    /// </remarks>
    public int? HighlightedSlot
    {
        get
        {
            if (Level is MenuLevel.BoardAction)
            {
                return _selectedSlot > 0 ? _selectedSlot : null;
            }

            if (Level is not MenuLevel.Board)
            {
                return null;
            }

            var options = Options;
            if (Highlight < 0 || Highlight >= options.Count)
            {
                return null;
            }

            return options[Highlight].Digit;
        }
    }

    /// <summary>
    /// Fills the point from a screen read and moves on to the confirm level.
    /// </summary>
    /// <remarks>
    /// For a two point request the first read fills point 1 and the draft comes back here for the
    /// second, so pickup and dropoff are the same key pressed twice in two places rather than a
    /// different interaction each.
    /// </remarks>
    /// <summary>How many points the selected type still wants. Zero means it has them all.</summary>
    public int PointsWanted => Math.Max(0, ArityOfSelection() - _points.Count);

    /// <summary>The points collected so far, in ordinal order.</summary>
    public IReadOnlyList<MapPoint> Points => _points;

    /// <summary>What the point being collected right now is called: PICKUP, DROPOFF, TARGET.</summary>
    public string CurrentPointLabel
    {
        get
        {
            if (SelectedTypeId is not { } id || _catalog.RequestType(id) is not { } type)
            {
                return "POINT";
            }

            var index = Math.Min(_points.Count, Math.Max(0, type.PointLabels.Count - 1));
            return type.PointLabels.Count > index
                ? type.PointLabels[index].ToUpperInvariant()
                : "POINT";
        }
    }

    private int ArityOfSelection() =>
        SelectedTypeId is { } id && _catalog.RequestType(id) is { } type ? type.Arity : 1;

    /// <summary>
    /// Records a completed point and moves on: another point if the type wants one, confirm if not.
    /// </summary>
    private MenuNavigated AcceptPoint(MapPoint point)
    {
        _points.Add(point);
        _digits.Clear();

        if (PointsWanted > 0)
        {
            // A two-point request comes back here for its second end, so pickup and dropoff are the
            // same interaction twice rather than two different ones.
            _snapshot = null;
            Level = MenuLevel.Coordinate;
            return new MenuNavigated(Level);
        }

        _snapshot = point;
        Level = MenuLevel.Confirm;
        return new MenuNavigated(Level);
    }

    public MenuOutcome AcceptReadCoordinate(MapPoint point, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(point);

        // One read, three meanings, decided by what asked for it. On the range page the end that
        // was pressed takes it and the page never moved in the first place, so the highlight stays
        // where the finger left it and pressing again re-reads THAT end.
        if (Level is MenuLevel.RangeTool)
        {
            _lastInput = now;
            var origin = Awaiting("range.origin");
            var awaited = _rangeAwaiting;
            _rangeAwaiting = null;

            if (awaited is null)
            {
                return MenuOutcome.None;
            }

            if (origin)
            {
                _toolGun = point;
                Highlight = GunIndex;
                return new MenuGunPositionSet(point);
            }

            _toolTarget = point;
            Highlight = TargetIndex;
            return new MenuNavigated(Level);
        }

        if (Level is not MenuLevel.Coordinate)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;
        return AcceptPoint(point);
    }
    /// <summary>
    /// Up one level. At the top level it backs out to the held rest state rather than doing
    /// nothing, so the other surface is one press away without releasing the key.
    /// </summary>
    /// <remarks>
    /// Back means one thing everywhere: leave the level you are on. At the top level that is a
    /// return to rest, with the key still held and the board still on screen, and it discards
    /// nothing. It used to CLOSE the whole menu there, so the key that means "I did not mean that"
    /// also ended the interaction, and the only way back in was to let go and start again.
    /// <para>
    /// While digits are being typed it deletes the last one. That is the same act, one level down:
    /// take back the thing I just did. It is also why the Backspace key is gone; a hand holding
    /// CapsLock cannot reach it.
    /// </para>
    /// </remarks>
    public MenuOutcome Back(DateTimeOffset now)
    {
        // The top of either surface. Backing off it closes the menu and leaves the hold armed, so
        // the prompt comes back and the other surface is one press away. Clearing the highlight and
        // staying open left you inside a page you had asked to leave, with its keys still live.
        if (Level is MenuLevel.Root or MenuLevel.Board)
        {
            _lastInput = now;
            return Close("back");
        }

        return Backspace(now);
    }

    /// <summary>
    /// Corrects the local toggle state from the authority, for a toggle the server refused or one
    /// changed on another device while the menu was open.
    /// </summary>
    public void ReconcileSubscribedRoles(IEnumerable<string> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        _subscribed.Clear();
        foreach (var id in roleIds)
        {
            _subscribed.Add(id);
        }
    }

    /// <summary>A digit key. One with no entry at that position is ignored silently.</summary>
    public MenuOutcome Digit(int digit, DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed || digit is < 0 or > 9)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;
        // 0 is TOOLS from anywhere with a list on it, the same surface the TOOLS key opens. It is
        // never a slot and never a category, so it collides with nothing on either other surface.
        if (digit == ZeroDigit && Level is not (MenuLevel.More
            or MenuLevel.Coordinate or MenuLevel.Join or MenuLevel.Confirm or MenuLevel.Roles))
        {
            Level = MenuLevel.More;
            return new MenuNavigated(Level);
        }

        return Level switch
        {
            // A digit at HOME is a request CATEGORY, and 0 is TOOLS. Rows carry the same numbers,
            // but they are reached by walking down onto them and selecting, which is what the keys
            // are for; the number on a row is its identity, the thing voice says and the eye finds,
            // not a second keyboard route that would collide with this one.
            MenuLevel.Root => Descend(_tree.Root, digit),
            MenuLevel.Board => SelectRow(digit),
            MenuLevel.Branch => Descend(Selection!.Children, digit),
            MenuLevel.Coordinate => TypeCoordinateDigit(digit),
            MenuLevel.Confirm => ToggleModifier(digit),
            MenuLevel.BoardAction => RunBoardVerb(digit),

            // The artillery page drew 1, 2 and 3 and dispatched none of them, so every digit on it
            // was dead and the only way to work the tool was to navigate onto each line.
            MenuLevel.RangeTool => SelectRangeEntry(digit, now),
            MenuLevel.More => RunMore(digit),
            MenuLevel.Roles => ToggleRole(digit),
            MenuLevel.Join => TypeJoinDigit(digit),
            _ => MenuOutcome.None,
        };
    }

    /// <summary>Up one level. At the root it does nothing.</summary>
    public MenuOutcome Backspace(DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;

        switch (Level)
        {
            case MenuLevel.Coordinate or MenuLevel.Join when _digits.Count > 0:
                _digits.RemoveAt(_digits.Count - 1);
                return new MenuNavigated(Level);

            case MenuLevel.Join or MenuLevel.Help or MenuLevel.Roles
                or MenuLevel.Match or MenuLevel.People:
                Level = MenuLevel.More;
                return new MenuNavigated(Level);

            // RANGE lives on the TOOLS page, so backing out of it lands where it was entered from.
            // It is not a request and it is not on the request list.
            case MenuLevel.RangeTool:
                Level = MenuLevel.More;
                return new MenuNavigated(Level);

            case MenuLevel.Confirm:
                // Backing out of confirm DISCARDS the reading and returns to the point level. It
                // used to keep the snapshot and pop to the branch, so the next pass skipped the
                // point level entirely and reused a coordinate the user had just backed away from.
                _modifiers.Clear();
                _snapshot = null;
                _digits.Clear();
                Level = MenuLevel.Coordinate;
                return new MenuNavigated(Level);

            case MenuLevel.Coordinate:
                Level = PopToBranch();
                return new MenuNavigated(Level);

            case MenuLevel.Branch:
                Level = PopToBranch();
                return new MenuNavigated(Level);

            // Everything one step below home climbs back to home, which is the list holding the
            // requests, the rows and MORE. There is no separate board level to return to.
            case MenuLevel.BoardAction:
                _selectedSlot = 0;
                Level = MenuLevel.Board;
                return new MenuNavigated(Level);

            // TOOLS is a surface of its own, reached by its own key from anywhere. There is no
            // level above it to climb to, so backing off it leaves the menu and re-arms the hold.
            case MenuLevel.More:
                return Close("back");

            default:
                return MenuOutcome.None;
        }
    }

    /// <summary>Cancels everything.</summary>
    public MenuOutcome Escape(DateTimeOffset now)
    {
        _ = now;
        return Level == MenuLevel.Closed ? MenuOutcome.None : Close("escape");
    }

    /// <summary>Losing the game window closes the menu.</summary>
    public MenuOutcome FocusLost(DateTimeOffset now)
    {
        _ = now;
        return Level == MenuLevel.Closed ? MenuOutcome.None : Close("focus_lost");
    }

    /// <summary>Releasing the key. Only the confirm level commits; every other level discards.</summary>
    /// <remarks>
    /// The menu lives only while the key is down, and that is a safety property, not a limitation.
    /// A latch briefly existed here so the menu could be driven with the key released: it also
    /// meant the digit row stayed armed after the user let go, so every keystroke afterwards was
    /// being taken from whatever window they were actually typing in. Held means the user always
    /// knows, from their own hand, whether this thing is listening.
    /// </remarks>
    public MenuOutcome KeyUp(DateTimeOffset now)
    {
        _ = now;
        if (Level == MenuLevel.Closed)
        {
            return MenuOutcome.None;
        }

        if (Level != MenuLevel.Confirm)
        {
            return Close("released_before_confirm");
        }

        var type = SelectedTypeId;
        var point = CurrentPoint();
        if (type is null || point is null)
        {
            // Named so the surface can say WHY nothing was sent. A request with no point is the one
            // failure a user cannot see: the menu closes on release and looks like it worked.
            return Close(point is null ? "no_coordinate" : "incomplete");
        }

        // Every point the type asked for, in ordinal order. Sending only the first is a
        // point_count_mismatch on any two-point type, which is every transport and escort request.
        if (PointsWanted > 0)
        {
            return Close("no_coordinate");
        }

        var selection = Selection;
        var outcome = new MenuRequestReady(
            type,
            selection?.SupplyKindId ?? DefaultSupplyKind(type),
            selection?.StructureKindId,
            point,
            [.. _modifiers])
        {
            Points = _points.Count > 0 ? [.. _points] : [point],
        };

        Reset();
        return outcome;
    }

    /// <summary>
    /// Closes a menu left open with the hold key released. A menu whose key is still down never
    /// times out, however long it sits there.
    /// </summary>
    /// <param name="now">The clock, injected so the guard is testable.</param>
    /// <param name="holdKeyDown">The hold key's state as the bridge last reported it.</param>
    public MenuOutcome Tick(DateTimeOffset now, bool holdKeyDown)
    {
        if (Level == MenuLevel.Closed)
        {
            return MenuOutcome.None;
        }

        if (holdKeyDown)
        {
            // Held is working, so the clock restarts and the orphan guard can never fire mid-use.
            _lastInput = now;
            return MenuOutcome.None;
        }

        return (now - _lastInput).TotalMilliseconds >= _options.OrphanTimeoutMs
            ? Close("orphaned")
            : MenuOutcome.None;
    }

    private IReadOnlyList<MenuEntry> CurrentOptions() => Level switch
    {
        MenuLevel.Root => RootEntries(),
        MenuLevel.Board => BoardRowEntries(),
        MenuLevel.Branch => Selection?.Children ?? [],
        MenuLevel.Confirm => ModifierEntries(),
        MenuLevel.BoardAction => BoardActionEntries(),
        MenuLevel.More => MoreOptions(),
        MenuLevel.RangeTool => RangeToolEntries(),
        MenuLevel.Help => HelpEntries(),
        MenuLevel.Roles => RoleEntries(),
        MenuLevel.Match => MatchEntries(),
        MenuLevel.People => PeopleEntries(),
        _ => [],
    };

    /// <summary>The group's roles, marked with the ones this participant receives.</summary>
    private IReadOnlyList<MenuEntry> RoleEntries() =>
    [
        .. _context.EnabledRoleIds
            .Take(MaxPageEntries)
            .Select((id, i) => new MenuEntry
            {
                Digit = i + 1,
                Path = $"roles.{id}",
                Label = _catalog.Role(id)?.Display.ToUpperInvariant() ?? ModifierLabels.Of(id),
                VerbId = id,
                IsChosen = _subscribed.Contains(id),
            }),
    ];

    /// <summary>Read only. Digit 0 is not offered, so nothing here can be pressed by accident.</summary>
    /// <summary>
    /// The match: what it is, then what you can do to it. The information lines carry no digit and
    /// the highlight skips them; only the controls at the foot are selectable.
    /// </summary>
    /// <remarks>
    /// It used to be four read-only lines, every one drawn with a 0 in front of it as though it
    /// were pressable, and the headcount rendered as "2 ON THE MATCH" so the count read as a key.
    /// A page that shows a match and offers no way to act on it also sent you back out to MORE to
    /// find RESTART, which is the one thing you open the match page to do.
    /// </remarks>
    /// <summary>
    /// The artillery tool: where your gun is, where the target is, and both are re-settable.
    /// </summary>
    /// <remarks>
    /// GUN HERE was a one-shot buried in MORE, so moving the gun or ranging a second target meant
    /// walking the whole menu again. A gun crew re-ranges constantly, so both ends stay one press
    /// away and the page never closes itself after a read.
    /// </remarks>
    private List<MenuEntry> RangeToolEntries()
    {
        var entries = new List<MenuEntry>(4)
        {
            new()
            {
                Digit = 1,
                Path = "range.origin",
                Label = Awaiting("range.origin") ? "ORIGIN  POINT AND READ" : _toolGun is { } gun
                    ? FormattableString.Invariant($"ORIGIN  x{gun.X:0.00} y{gun.Y:0.00}")
                    : "ORIGIN  NOT SET",
            },
            new()
            {
                Digit = 2,
                Path = "range.target",
                Label = Awaiting("range.target") ? "TARGET  POINT AND READ" : _toolTarget is { } target
                    ? FormattableString.Invariant($"TARGET  x{target.X:0.00} y{target.Y:0.00}")
                    : "TARGET  NOT SET",
            },
        };

        // Only once there is something to clear. An origin read is what puts the range section on
        // the board and there was no way to take it off again: the section, and every bracket it
        // drew, was permanent for the rest of the session.
        if (_toolGun is not null || _toolTarget is not null)
        {
            entries.Add(new MenuEntry { Digit = 3, Path = "range.clear", Label = "CLEAR" });
        }

        return entries;
    }

    private bool Awaiting(string path) =>
        string.Equals(_rangeAwaiting, path, StringComparison.Ordinal);

    /// <summary>Presses one of the range page's digits, exactly as selecting it would.</summary>
    private MenuOutcome SelectRangeEntry(int digit, DateTimeOffset now)
    {
        var entry = RangeToolEntries().Find(e => e.Digit == digit);
        return entry is null ? MenuOutcome.None : PressRangeEntry(entry, now);
    }

    /// <summary>
    /// Pressing an end READS THE MAP where it stands, and the page never changes.
    /// </summary>
    /// <remarks>
    /// Each end used to be a level of its own, so setting both meant four presses through two pages
    /// that drew nothing but a prompt. A gun crew re-ranges constantly; the page stays put and the
    /// row it filled says so.
    /// </remarks>
    private MenuOutcome PressRangeEntry(MenuEntry entry, DateTimeOffset now)
    {
        if (entry.Path == "range.clear")
        {
            return ClearRangeTool(now);
        }

        _lastInput = now;
        _rangeAwaiting = entry.Path;
        return new MenuCoordinateReadRequested();
    }

    /// <summary>The modes this page can be set to, from the served ballistics.</summary>
    public IReadOnlyList<RangeMode> RangeModes =>
        _context.RangeModes.Count > 0 ? _context.RangeModes : RangeMode.Fallback;

    /// <summary>The calculator the range is judged against right now.</summary>
    public RangeMode RangeMode
    {
        get
        {
            var modes = RangeModes;
            return modes[((_rangeMode % modes.Count) + modes.Count) % modes.Count];
        }
    }

    /// <summary>
    /// Steps the calculator: sniping, then one per weapon. Negative steps back.
    /// </summary>
    /// <remarks>
    /// A mode, not a page. The same two points mean a different answer to a sniper and to a gun
    /// crew, and walking a menu to say which is a menu walked between every shot.
    /// </remarks>
    public MenuOutcome CycleRangeMode(int step, DateTimeOffset now)
    {
        if (Level is not MenuLevel.RangeTool || step == 0)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;
        var modes = RangeModes;
        _rangeMode = (((_rangeMode + step) % modes.Count) + modes.Count) % modes.Count;
        return new MenuNavigated(Level);
    }

    /// <summary>
    /// Forgets the origin and the target, which takes the range section off the board.
    /// </summary>
    /// <remarks>
    /// Both ends together. Clearing only the target would leave a gun position that goes stale
    /// where it stands, and a bracket computed from where a gun used to be is worse than no bracket.
    /// </remarks>
    private MenuGunPositionCleared ClearRangeTool(DateTimeOffset now)
    {
        _lastInput = now;
        _toolGun = null;
        _toolTarget = null;
        _rangeAwaiting = null;
        Highlight = FirstSelectable();

        // The app hears it too: the gun that draws a bracket on every mortar row is the same gun,
        // so clearing here has to stop those as well or the rows keep ranging from a gun the tool
        // has forgotten.
        return new MenuGunPositionCleared();
    }

    /// <summary>Where the tool is ranging from. Null until a read sets it.</summary>
    public MapPoint? ToolGun => _toolGun;

    /// <summary>Where the tool is ranging to. Null until a read sets it.</summary>
    public MapPoint? ToolTarget => _toolTarget;

    private List<MenuEntry> MatchEntries()
    {
        var lines = new List<string>(4);
        if (_context.GroupName is { } group)
        {
            lines.Add(group.ToUpperInvariant());
        }

        if (_context.DeploymentLabel is { } label)
        {
            lines.Add(label.ToUpperInvariant());
        }

        lines.Add($"PLAYERS {_context.MemberCount.ToString(CultureInfo.InvariantCulture)}");

        var entries = new List<MenuEntry>(Lines("match", lines));

        if (_context.InviteCode is { } invite)
        {
            entries.Add(new MenuEntry
            {
                Digit = 1,
                Path = "match.invite",
                Label = $"INVITE {invite}   COPY",
            });
        }

        if (_context.CanRestart)
        {
            entries.Add(new MenuEntry { Digit = 2, Path = "match.restart", Label = "NEW MATCH" });
        }

        return entries;
    }

    private IReadOnlyList<MenuEntry> PeopleEntries() =>
        Lines("people", [.. _context.Roster.Take(MaxPageEntries).Select(c => c.ToUpperInvariant())]);

    /// <summary>A read-only page. Every entry carries digit 0, which selects nothing.</summary>
    private static IReadOnlyList<MenuEntry> Lines(string page, IReadOnlyList<string> labels) =>
    [
        .. labels.Select((text, i) => new MenuEntry
        {
            Digit = ZeroDigit,
            Path = $"{page}.{i.ToString(CultureInfo.InvariantCulture)}",
            Label = text,
            IsInfo = true,
        }),
    ];

    /// <summary>
    /// What the digits do, drawn rather than described.
    /// </summary>
    /// <remarks>
    /// HELP used to close the menu and hand the app a panel id nothing handled, so it did nothing
    /// at all. A level costs no new surface and obeys the same rule as the rest: it lives only
    /// while the key is held.
    /// <para>
    /// Then it listed the board verbs with their digits, which read as a page of controls where
    /// every key was dead, and it described the digit-driven flow that navigation replaced: it told
    /// the reader to press BOARD, which no longer exists. It is read-only text now, every line
    /// marked so nothing pretends to be pressable, and it names the keys THIS player has bound.
    /// </para>
    /// </remarks>
    private IReadOnlyList<MenuEntry> HelpEntries()
    {
        var keys = _context.Keys;
        var lines = new (string Id, string Label)[]
        {
            ("hold", $"HOLD {keys.Menu} WHILE YOU WORK"),
            ("surfaces", $"{keys.Up} REQUEST   {keys.Down} BOARD   {keys.Tools} TOOLS"),
            ("move", $"{keys.Up} {keys.Down}   MOVE THE HIGHLIGHT"),
            ("select", $"{keys.Select}     TAKE THE HIGHLIGHTED LINE"),
            ("back", $"{keys.Back}     BACK ONE LEVEL"),
            ("digits", "1-9   PICK BY NUMBER ON ANY LIST"),
            ("backtop", $"{keys.Back}     AT THE TOP, LEAVES THE SURFACE"),
            ("board", $"{keys.Down} OPENS THE BOARD, {keys.Select} FOR A ROW'S VERBS"),
            ("range", $"ON RANGE: {keys.Cycle} {keys.Tools} CHANGE THE CALCULATOR"),
            ("claim", "ACCEPTING STARTS IT. THERE IS NO SECOND STEP"),
            ("coord", $"ON A COORDINATE: POINT AT THE MAP, {keys.Select} READS IT"),
            ("release", $"LET GO OF {keys.Menu} TO CLOSE"),
        };

        return
        [
            .. lines.Select(line => new MenuEntry
            {
                Digit = NoDigit,
                Path = $"help.{line.Id}",
                Label = line.Label,
                IsInfo = true,
            }),
        ];
    }

    /// <summary>
    /// The request types, and the board underneath them.
    /// </summary>
    /// <remarks>
    /// 0 opened the board from the root the whole time and was never drawn, so the only route to
    /// accepting or closing a request was one you had to be told. A menu that draws its own digits
    /// is the reason the chord surface is small; a digit it does not draw is a chord with no key.
    /// </remarks>
    /// <summary>
    /// The request surface: the request categories, and nothing else.
    /// </summary>
    /// <remarks>
    /// Three surfaces, one key each: UP is what you ask a squadmate for, DOWN is the work in front
    /// of you, TOOLS is the machine itself. One merged list had all three on it, so DOWN into the
    /// board and then UP walked straight back into the requests, and ARTILLERY and TOOLS sat among
    /// the categories reading like things you could ask somebody for.
    /// </remarks>
    private List<MenuEntry> RootEntries() => [.. _tree.Root];

    /// <summary>The occupied rows, in slot order. The whole of the board surface.</summary>
    private List<MenuEntry> BoardRowEntries() =>
    [
        .. _context.OccupiedSlots
            .Where(s => s is >= 1 and <= 9)
            .OrderBy(s => s)
            .Select(s => new MenuEntry
            {
                Digit = s,
                Path = $"board.{s}",
                Label = s.ToString(CultureInfo.InvariantCulture),
            }),
    ];

    /// <summary>True while the highlight is on a request category.</summary>
    public bool HighlightIsARequest
    {
        get
        {
            if (Level is not MenuLevel.Root)
            {
                return false;
            }

            var options = Options;
            if (Highlight < 0 || Highlight >= options.Count)
            {
                return false;
            }

            return true;
        }
    }

    private IReadOnlyList<MenuEntry> ModifierEntries()
    {
        var type = SelectedTypeId is { } id ? _catalog.RequestType(id) : null;
        if (type is null)
        {
            return [];
        }

        // The chosen ones are marked, because choosing several and seeing one is the same as
        // seeing none: the line stops describing the request you are about to send.
        return [.. type.Modifiers.Select((m, i) => new MenuEntry
        {
            Digit = i + 1,
            Path = $"{type.Id}.modifier.{m}",
            Label = ModifierLabels.Of(m),
            IsChosen = _modifiers.Contains(m),
        })];
    }

    /// <summary>
    /// The verbs THIS row can honour, numbered from 1, with the rest absent.
    /// </summary>
    /// <remarks>
    /// Every verb used to be offered on every row: START, DONE and RELEASE on an open row nobody
    /// had claimed, and ACCEPT on a row already yours. The server refuses all of those, so the
    /// press did nothing and the surface reported nothing, which is the one thing this product
    /// refuses to do anywhere else.
    /// <para>
    /// Numbered from the filtered list rather than fixed, so every offer lands within a left hand's
    /// reach. See <see cref="BoardVerbs"/>.
    /// </para>
    /// </remarks>
    private List<MenuEntry> BoardActionEntries() =>
    [
        .. BoardVerbs
            .Where(v => _catalog.CommandVerb(v.VerbId) is not null)
            .Where(v => VerbApplies(v.VerbId))
            .Take(MaxRowVerbs)
            .Select((v, i) => new MenuEntry
            {
                Digit = i + 1,
                Path = $"board.action.{v.VerbId}",
                Label = v.Label,
                VerbId = v.VerbId,
            }),
    ];

    private bool VerbApplies(string verbId)
    {
        // With no state known, offer everything: that is the old behaviour and a caller who has
        // not filled Slots in should not lose the board.
        if (!_context.Slots.TryGetValue(_selectedSlot, out var slot))
        {
            return true;
        }

        var mine = slot.ClaimedByViewer;
        var asked = slot.RequestedByViewer;
        var open = slot.State == RequestState.Open;
        var working = slot.State is RequestState.Claimed or RequestState.InProgress;

        return verbId switch
        {
            // Only an unclaimed row somebody else raised can be taken.
            "accept" => open && !asked,

            // Only the person holding it can finish it, move it along, or give it back.
            "done" => mine && working,
            "release" => mine && working,
            "rounds_away" => mine && working,

            // Only the person who asked for it can call it off, and doing so takes it off whoever
            // accepted it so they can go and do something else. Parsed from voice and offered
            // nowhere, so a requester had no way to withdraw their own request at all.
            "cancel" => asked && (open || working),

            // Hiding a row you are not working, and hiding its requester, are for other people's.
            "pass" => open && !mine && !asked,
            "mute" => !asked && !mine,

            // The coordinate is worth copying whatever state the row is in.
            "copy" => true,
            _ => false,
        };
    }

    private List<MenuEntry> MoreOptions() =>
    [
        .. MoreEntries
            .Where(e => IsOffered(e.Id))
            .Select(e => new MenuEntry
            {
                Digit = e.Digit,
                Path = $"board.more.{e.Id}",
                Label = e.Label,
                VerbId = e.Id,
            }),
    ];

    /// <summary>
    /// Whether the MORE page draws this entry at all.
    /// </summary>
    /// <remarks>
    /// An entry the agent cannot honour is absent, never shown and dead. Roles, match, people,
    /// restart and link all returned a panel id nothing handles, so they closed the menu and did
    /// nothing, which is exactly the click this product refuses to offer anywhere else.
    /// </remarks>
    private bool IsOffered(string id) => id switch
    {
        "help" => true,
        "join" => true,
        "roles" => _context.EnabledRoleIds.Count > 0,
        "match" => _context.DeploymentLabel is not null,
        "people" => _context.Roster.Count > 0,
        "range" => true,
        // Always offered. It used to require a coordinate snapshot taken at key-down, and snapshots
        // are never taken at key-down any more, so GUN HERE silently vanished from MORE and the
        // whole fire-solution path became unreachable. Selecting it READS THE MAP, like the
        // coordinate level does.
        "gun" => true,
        "link" => _context.LinkPromptPending,
        _ => false,
    };

    private MenuOutcome Descend(IReadOnlyList<MenuEntry> level, int digit)
    {
        var entry = level.FirstOrDefault(e => e.Digit == digit);
        if (entry is null)
        {
            return MenuOutcome.None;
        }

        _path.Add(entry);

        if (!entry.IsLeaf)
        {
            Level = MenuLevel.Branch;
            return new MenuNavigated(Level);
        }

        // A leaf needs its type's arity in points. A key-down snapshot counts as the first of
        // them, so a two-point type still comes to the point level for its second end rather than
        // jumping to confirm with half a request.
        if (_snapshot is { } prefilled)
        {
            return AcceptPoint(prefilled);
        }

        Level = MenuLevel.Coordinate;
        return new MenuNavigated(Level);
    }

    private MenuOutcome TypeCoordinateDigit(int digit)
    {
        if (_digits.Count >= _options.DigitsPerAxis * 2)
        {
            return MenuOutcome.None;
        }

        _digits.Add(digit);
        if (_digits.Count < _options.DigitsPerAxis * 2)
        {
            return new MenuNavigated(Level);
        }

        return TypedPoint() is { } typed ? AcceptPoint(typed) : new MenuNavigated(Level);
    }

    private MenuOutcome TypeJoinDigit(int digit)
    {
        if (_digits.Count >= _options.InviteCodeDigits)
        {
            return MenuOutcome.None;
        }

        _digits.Add(digit);
        if (_digits.Count < _options.InviteCodeDigits)
        {
            return new MenuNavigated(Level);
        }

        var code = string.Concat(_digits.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        Reset();
        return new MenuJoinReady(code);
    }

    private MenuOutcome ToggleModifier(int digit)
    {
        var entries = ModifierEntries();
        var entry = entries.FirstOrDefault(e => e.Digit == digit);
        if (entry is null)
        {
            return MenuOutcome.None;
        }

        var id = entry.Path[(entry.Path.LastIndexOf('.') + 1)..];
        if (!_modifiers.Remove(id))
        {
            _modifiers.Add(id);
        }

        return new MenuNavigated(Level);
    }

    /// <summary>
    /// The TOOLS surface, from rest or from either other surface. Its own key, because it is not a
    /// request and putting it on the request list made it read like one.
    /// </summary>
    public MenuOutcome OpenTools(DateTimeOffset now, MenuContext? context = null, MapPoint? snapshot = null)
    {
        if (!IsOpen)
        {
            var opened = Open(now, snapshot, context);
            if (opened is MenuDiscarded)
            {
                return opened;
            }
        }

        _lastInput = now;
        Level = MenuLevel.More;
        return new MenuNavigated(Level);
    }

    /// <summary>
    /// Opens one MORE entry by id, which is how a spoken panel reaches the same surface its digit
    /// reaches.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="RunMore"/> rather than setting a level here, so voice and the
    /// keyboard cannot drift: a gated entry is refused for the speaker exactly as it is absent for
    /// the presser, and a panel that grows a precondition grows it for both at once.
    /// </remarks>
    public MenuOutcome OpenPanel(
        string panelId,
        DateTimeOffset now,
        MenuContext? context = null,
        MapPoint? snapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);

        if (!MoreDigits.TryGetValue(panelId, out var digit))
        {
            return MenuOutcome.None;
        }

        if (!IsOpen)
        {
            var opened = Open(now, snapshot, context);
            if (opened is MenuDiscarded)
            {
                return opened;
            }
        }

        _lastInput = now;
        return RunMore(digit);
    }

    /// <summary>Opens the verbs for one row. The navigation path reaches this by selecting it.</summary>
    private MenuOutcome SelectRow(int digit)
    {
        if (!_context.OccupiedSlots.Contains(digit))
        {
            return MenuOutcome.None;
        }

        _selectedSlot = digit;
        Level = MenuLevel.BoardAction;
        return new MenuNavigated(Level);
    }

    /// <summary>A role toggled from the overlay. The server owns the result; this only asks.</summary>
    /// <summary>
    /// Flips the role locally, then reports it. Optimistic on purpose: the row has to change under
    /// the finger that pressed it, and a toggle whose only feedback arrives with the server round
    /// trip reads as a dead key.
    /// </summary>
    private MenuOutcome ToggleRole(int digit)
    {
        var entry = RoleEntries().FirstOrDefault(e => e.Digit == digit);
        if (entry?.VerbId is not { } roleId)
        {
            return MenuOutcome.None;
        }

        if (!_subscribed.Remove(roleId))
        {
            _subscribed.Add(roleId);
        }

        return new MenuRoleToggled(roleId);
    }

    private MenuOutcome RunMore(int digit)
    {
        var entry = Array.Find(MoreEntries, e => e.Digit == digit);
        if (entry.Id is null || !IsOffered(entry.Id))
        {
            return MenuOutcome.None;
        }

        if (entry.Id == "join")
        {
            // Held, like every other level. This used to latch so six digits could be typed with
            // the key released, and that one exception was enough to leave the menu swallowing the
            // digit row, Escape and Backspace with nothing held down. Keep holding for six digits.
            _digits.Clear();
            Level = MenuLevel.Join;
            return new MenuNavigated(Level);
        }

        if (entry.Id is "restart" or "link")
        {
            Reset();
            return new MenuPanelRequested(entry.Id);
        }

        if (entry.Id is "help" or "roles" or "match" or "people")
        {
            Level = entry.Id switch
            {
                "roles" => MenuLevel.Roles,
                "match" => MenuLevel.Match,
                "people" => MenuLevel.People,
                _ => MenuLevel.Help,
            };
            return new MenuNavigated(Level);
        }

        if (entry.Id == "range")
        {
            Level = MenuLevel.RangeTool;
            return new MenuNavigated(Level);
        }

        if (entry.Id == "gun")
        {
            // The one route to an origin is the range page, which reads the map where it stands.
            Level = MenuLevel.RangeTool;
            _rangeAwaiting = "range.origin";
            return new MenuCoordinateReadRequested();
        }

        Reset();
        return new MenuPanelRequested(entry.Id);
    }

    private MenuOutcome RunBoardVerb(int digit)
    {
        // Off the entries this row actually offers, never off the whole table: the digit is a
        // position in the filtered list, so 1 is whatever this row's first verb is.
        var entry = BoardActionEntries().Find(e => e.Digit == digit);
        if (entry?.VerbId is not { } verbId || _selectedSlot == 0)
        {
            return MenuOutcome.None;
        }

        var slot = _selectedSlot;
        Reset();
        return new MenuBoardAction(verbId, slot);
    }

    private MenuLevel PopToBranch()
    {
        if (_path.Count > 0)
        {
            _path.RemoveAt(_path.Count - 1);
        }

        _digits.Clear();
        return _path.Count == 0 ? MenuLevel.Root : MenuLevel.Branch;
    }

    /// <summary>The point the typed digits spell, or null while the grid is incomplete.</summary>
    private MapPoint? TypedPoint()
    {
        var digitsPerAxis = _options.DigitsPerAxis;
        if (_digits.Count < digitsPerAxis * 2)
        {
            return null;
        }

        var typedX = Axis(_digits.Take(digitsPerAxis));
        var typedY = Axis(_digits.Skip(digitsPerAxis).Take(digitsPerAxis));
        var typedRaw = FormattableString.Invariant($"x{typedX:0.00} y{typedY:0.00}");
        return new MapPoint(typedX, typedY, _options.TypedGridSourceId, typedRaw, null);
    }

    private MapPoint? CurrentPoint()
    {
        if (_points.Count > 0)
        {
            return _points[0];
        }

        if (_snapshot is not null)
        {
            return _snapshot;
        }

        var per = _options.DigitsPerAxis;
        if (_digits.Count < per * 2)
        {
            return null;
        }

        var x = Axis(_digits.Take(per));
        var y = Axis(_digits.Skip(per).Take(per));
        var raw = $"x{x.ToString("0.00", CultureInfo.InvariantCulture)} y{y.ToString("0.00", CultureInfo.InvariantCulture)}";
        return new MapPoint(x, y, _options.TypedGridSourceId, raw, null);
    }

    private static decimal Axis(IEnumerable<int> digits)
    {
        var text = string.Concat(digits.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        var whole = int.Parse(text[..^2], CultureInfo.InvariantCulture);
        var fraction = int.Parse(text[^2..], CultureInfo.InvariantCulture);
        return whole + (fraction / 100m);
    }

    private string? DefaultSupplyKind(string typeId)
    {
        var type = _catalog.RequestType(typeId);
        return type?.RequiresSupplyKind == true ? type.DefaultSupplyKind : null;
    }

    private MenuDiscarded Close(string reason)
    {
        Reset();
        return new MenuDiscarded(reason);
    }

    private void Reset()
    {
        _path.Clear();
        _modifiers.Clear();
        _digits.Clear();
        _points.Clear();
        _selectedSlot = 0;
        _snapshot = null;
        _context = new MenuContext();
        Level = MenuLevel.Closed;
    }
}
