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

    /// <summary>The nine request categories, plus 0 BOARD.</summary>
    Root,

    /// <summary>A compiled category or sub-category.</summary>
    Branch,

    /// <summary>Digit entry for the point. M1's only key-driven coordinate source.</summary>
    Coordinate,

    /// <summary>Modifiers, and the only level a release commits from.</summary>
    Confirm,

    /// <summary>0 BOARD. Hand authored: its entries are slots, and 0 for everything else.</summary>
    Board,

    /// <summary>The verbs available against one selected row.</summary>
    BoardAction,

    /// <summary>0 BOARD > 0. Every panel, plus join and gun position. Hand authored.</summary>
    More,

    /// <summary>Six digits, then commit. No confirm step.</summary>
    Join,
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
                node.Label ??= type.OverlayLabel;
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
    IReadOnlyList<string> Modifiers) : MenuOutcome;

/// <summary>Six digits off the keypad. The cold-start path for anybody with no microphone.</summary>
public sealed record MenuJoinReady(string InviteCode) : MenuOutcome;

/// <summary>A verb against one slot, executed immediately. Board actions never preview.</summary>
public sealed record MenuBoardAction(string VerbId, int Slot) : MenuOutcome;

/// <summary>
/// A panel off the MORE page: help, roles, match, people, restart, link. The panel owns the digits
/// from here and outlives the held key, which is why the menu closes as it hands one over.
/// </summary>
public sealed record MenuPanelRequested(string PanelId) : MenuOutcome;

/// <summary>MORE > GUN HERE. The key-down snapshot, committed with no confirm step.</summary>
public sealed record MenuGunPositionSet(MapPoint Point) : MenuOutcome;

/// <summary>
/// What the MORE page is allowed to offer this person right now. Its digits are fixed: an entry
/// nobody can use is absent, never renumbered, so a digit learned once stays learned.
/// </summary>
public sealed record MenuContext
{
    /// <summary>Slots holding a row. Only these are selectable at the board level.</summary>
    public IReadOnlyCollection<int> OccupiedSlots { get; init; } = [];

    /// <summary>Admin and owner only. Restart is board-wide and destructive.</summary>
    public bool CanRestart { get; init; }

    /// <summary>True only while the one-time link prompt is showing.</summary>
    public bool LinkPromptPending { get; init; }
}

/// <summary>Timings and sizes for the menu. None of them is a fact about the game.</summary>
public sealed record MenuOptions
{
    /// <summary>The menu closes on its own after this long with no input.</summary>
    public int IdleTimeoutMs { get; init; } = 6000;

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

    private static readonly (int Digit, string VerbId, string Label)[] BoardVerbs =
    [
        (1, "accept", "ACCEPT"),
        (2, "start", "START"),
        (3, "done", "DONE"),
        (4, "pass", "PASS"),
        (5, "release", "RELEASE"),
        (6, "mute", "MUTE"),
        (7, "copy", "COPY"),
    ];

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
        (5, "gun", "GUN HERE"),
        (6, "join", "JOIN CODE"),
        (7, "restart", "RESTART"),
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
    private int _selectedSlot;
    private bool _latched;

    public MenuStateMachine(MenuTree tree, Catalog catalog, MenuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(catalog);
        _tree = tree;
        _catalog = catalog;
        _options = options ?? new MenuOptions { InviteCodeDigits = catalog.GrammarRules.InviteCodeDigits };
    }

    public MenuLevel Level { get; private set; } = MenuLevel.Closed;

    /// <summary>The entry currently drilled into, or null at the root.</summary>
    public MenuEntry? Selection => _path.Count > 0 ? _path[^1] : null;

    /// <summary>What the overlay draws at this level.</summary>
    public IReadOnlyList<MenuEntry> Options => CurrentOptions();

    /// <summary>Digits typed at the coordinate or join level.</summary>
    public IReadOnlyList<int> Digits => _digits;

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

    public bool IsOpen => Level != MenuLevel.Closed;

    /// <summary>
    /// True once the menu has stopped following the held key. Typing a six digit code with a key
    /// held down is not an interaction anybody completes, so JOIN detaches and Escape closes it.
    /// </summary>
    public bool IsLatched => _latched;

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
        _selectedSlot = 0;
        _latched = false;
        _snapshot = snapshot;
        _context = context ?? new MenuContext();
        _lastInput = now;
        Level = MenuLevel.Root;
        return new MenuNavigated(Level);
    }

    /// <summary>A digit key. One with no entry at that position is ignored silently.</summary>
    public MenuOutcome Digit(int digit, DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed || digit is < 0 or > 9)
        {
            return MenuOutcome.None;
        }

        _lastInput = now;
        return Level switch
        {
            MenuLevel.Root => digit == ZeroDigit ? EnterBoard() : Descend(_tree.Root, digit),
            MenuLevel.Branch => Descend(Selection!.Children, digit),
            MenuLevel.Coordinate => TypeCoordinateDigit(digit),
            MenuLevel.Confirm => ToggleModifier(digit),
            MenuLevel.Board => SelectRow(digit),
            MenuLevel.BoardAction => RunBoardVerb(digit),
            MenuLevel.More => RunMore(digit),
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

            case MenuLevel.Join:
                Level = MenuLevel.More;
                return new MenuNavigated(Level);

            case MenuLevel.Confirm:
                _modifiers.Clear();
                Level = _snapshot is null ? MenuLevel.Coordinate : PopToBranch();
                return new MenuNavigated(Level);

            case MenuLevel.Coordinate:
                Level = PopToBranch();
                return new MenuNavigated(Level);

            case MenuLevel.Branch:
                Level = PopToBranch();
                return new MenuNavigated(Level);

            case MenuLevel.BoardAction:
                _selectedSlot = 0;
                Level = MenuLevel.Board;
                return new MenuNavigated(Level);

            case MenuLevel.More:
                Level = MenuLevel.Board;
                return new MenuNavigated(Level);

            case MenuLevel.Board:
                Level = MenuLevel.Root;
                return new MenuNavigated(Level);

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

    /// <summary>
    /// Releasing the key. Confirm commits, a tap latches the menu open, everything else discards.
    /// </summary>
    /// <remarks>
    /// A tap is a release still at the root with nothing chosen, and it latches so the whole menu
    /// can be driven with the key released. Holding is the voice path and is unchanged: you speak
    /// while it is down and let go on Confirm. Without the latch the only way to reach any of this
    /// was to hold a key down through every digit, which is not a keyboard control surface.
    /// </remarks>
    public MenuOutcome KeyUp(DateTimeOffset now)
    {
        _ = now;
        if (Level == MenuLevel.Closed || _latched)
        {
            return MenuOutcome.None;
        }

        if (Level == MenuLevel.Root && _path.Count == 0 && _digits.Count == 0)
        {
            _latched = true;
            return new MenuNavigated(Level);
        }

        if (Level != MenuLevel.Confirm)
        {
            return Close("released_before_confirm");
        }

        var type = SelectedTypeId;
        var point = CurrentPoint();
        if (type is null || point is null)
        {
            return Close("incomplete");
        }

        var selection = Selection;
        var outcome = new MenuRequestReady(
            type,
            selection?.SupplyKindId ?? DefaultSupplyKind(type),
            selection?.StructureKindId,
            point,
            [.. _modifiers]);

        Reset();
        return outcome;
    }

    /// <summary>The menu closes on its own after IdleTimeoutMs of no input.</summary>
    public MenuOutcome Tick(DateTimeOffset now)
    {
        if (Level == MenuLevel.Closed)
        {
            return MenuOutcome.None;
        }

        return (now - _lastInput).TotalMilliseconds >= _options.IdleTimeoutMs
            ? Close("idle")
            : MenuOutcome.None;
    }

    private IReadOnlyList<MenuEntry> CurrentOptions() => Level switch
    {
        MenuLevel.Root => _tree.Root,
        MenuLevel.Branch => Selection?.Children ?? [],
        MenuLevel.Confirm => ModifierEntries(),
        MenuLevel.Board => BoardEntries(),
        MenuLevel.BoardAction => BoardActionEntries(),
        MenuLevel.More => MoreOptions(),
        _ => [],
    };

    private IReadOnlyList<MenuEntry> ModifierEntries()
    {
        var type = SelectedTypeId is { } id ? _catalog.RequestType(id) : null;
        if (type is null)
        {
            return [];
        }

        return [.. type.Modifiers.Select((m, i) => new MenuEntry
        {
            Digit = i + 1,
            Path = $"{type.Id}.modifier.{m}",
            Label = m.Replace('_', ' ').ToUpperInvariant(),
        })];
    }

    private List<MenuEntry> BoardEntries()
    {
        var entries = _context.OccupiedSlots
            .Where(s => s is >= 1 and <= 9)
            .OrderBy(s => s)
            .Select(s => new MenuEntry
            {
                Digit = s,
                Path = $"board.{s}",
                Label = s.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        // 0 is never a slot, so it is free at every board level for the one thing that is not a
        // row. This is where the eight chords went.
        entries.Add(new MenuEntry { Digit = ZeroDigit, Path = "board.more", Label = "MORE" });
        return entries;
    }

    private List<MenuEntry> BoardActionEntries() =>
    [
        .. BoardVerbs
            .Where(v => _catalog.CommandVerb(v.VerbId) is not null)
            .Select(v => new MenuEntry
            {
                Digit = v.Digit,
                Path = $"board.action.{v.VerbId}",
                Label = v.Label,
                VerbId = v.VerbId,
            }),
    ];

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

    private bool IsOffered(string id) => id switch
    {
        "restart" => _context.CanRestart,
        "link" => _context.LinkPromptPending,
        "gun" => _snapshot is not null,
        _ => true,
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

        // A leaf needs a point. Capture pre-fills it; otherwise the digits do.
        Level = _snapshot is null ? MenuLevel.Coordinate : MenuLevel.Confirm;
        return new MenuNavigated(Level);
    }

    private MenuOutcome TypeCoordinateDigit(int digit)
    {
        if (_digits.Count >= _options.DigitsPerAxis * 2)
        {
            return MenuOutcome.None;
        }

        _digits.Add(digit);
        if (_digits.Count == _options.DigitsPerAxis * 2)
        {
            Level = MenuLevel.Confirm;
        }

        return new MenuNavigated(Level);
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

    private MenuNavigated EnterBoard()
    {
        Level = MenuLevel.Board;
        return new MenuNavigated(Level);
    }

    private MenuOutcome SelectRow(int digit)
    {
        if (digit == ZeroDigit)
        {
            Level = MenuLevel.More;
            return new MenuNavigated(Level);
        }

        if (_context.OccupiedSlots.Contains(digit))
        {
            _selectedSlot = digit;
            Level = MenuLevel.BoardAction;
            return new MenuNavigated(Level);
        }

        return MenuOutcome.None;
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
            // The only level that outlives the held key: six digits is not a hold.
            _digits.Clear();
            _latched = true;
            Level = MenuLevel.Join;
            return new MenuNavigated(Level);
        }

        if (entry.Id == "gun")
        {
            var point = _snapshot!;
            Reset();
            return new MenuGunPositionSet(point);
        }

        Reset();
        return new MenuPanelRequested(entry.Id);
    }

    private MenuOutcome RunBoardVerb(int digit)
    {
        var verb = Array.Find(BoardVerbs, v => v.Digit == digit);
        if (verb.VerbId is null || _catalog.CommandVerb(verb.VerbId) is null || _selectedSlot == 0)
        {
            return MenuOutcome.None;
        }

        var slot = _selectedSlot;
        Reset();
        return new MenuBoardAction(verb.VerbId, slot);
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

    private MapPoint? CurrentPoint()
    {
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
        _selectedSlot = 0;
        _latched = false;
        _snapshot = null;
        _context = new MenuContext();
        Level = MenuLevel.Closed;
    }
}
