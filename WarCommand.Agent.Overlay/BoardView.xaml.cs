using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The panel header, as drawn in docs/design/mocks/OverlayHeader.dc.html. Two lines: who and where
/// on the first, roles and the contextual key hint on the second, with an amber dot on a fault.
/// </summary>
/// <remarks>
/// The four faults are not the same amber word: each one names itself. A null field renders
/// nothing rather than a placeholder.
/// </remarks>
/// <summary>
/// One subscribed role on the header line: the served glyph, its hue, and the display name.
/// </summary>
/// <remarks>
/// Role owns the glyph and nothing else, per Decision_WarCommandOverlayColorIsStateOnlyWebOwnsRoleHue,
/// so the hue strokes the paths and the label stays the header's dim grey.
/// </remarks>
public sealed record HeaderRole
{
    public required string RoleId { get; init; }

    public string Display { get; init; } = string.Empty;

    public System.Windows.Media.Geometry? RoleGlyphFirst { get; init; }

    public System.Windows.Media.Geometry? RoleGlyphSecond { get; init; }

    public string RoleBrushKey { get; init; } = "RoleCommand";
}

public sealed record BoardHeader
{
    /// <summary>'61ST / ALPHA'. Group first, then the deployment.</summary>
    public required string Title { get; init; }

    /// <summary>People on the deployment. Null hides the count.</summary>
    public int? PeopleCount { get; init; }

    /// <summary>Map name, or VISITOR. Null hides it.</summary>
    public string? Where { get; init; }

    /// <summary>'INVITE 921585', or a gun position. Null hides it.</summary>
    public string? Right { get; init; }

    /// <summary>The viewer's subscribed role ids. Raw until WithGlyph resolves them.</summary>
    public IReadOnlyList<string> RoleIds { get; init; } = [];

    /// <summary>
    /// The same roles carrying their glyph and hue. Empty until WithGlyph runs, which is what makes
    /// a header that skipped it render nothing rather than a line of colourless ids.
    /// </summary>
    public IReadOnlyList<HeaderRole> Roles { get; init; } = [];

    /// <summary>
    /// Resolves every subscribed role against the served catalog. The header is a surface, so it is
    /// bound by the same rule the rows are: nothing reaches it without its glyph.
    /// </summary>
    public BoardHeader WithGlyph(RoleGlyphSource glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);

        return this with
        {
            Roles = RoleIds.Select(id =>
            {
                var (first, second) = glyphs.Geometry(id);
                return new HeaderRole
                {
                    RoleId = id,
                    Display = glyphs.Display(id),
                    RoleGlyphFirst = first,
                    RoleGlyphSecond = second,
                    RoleBrushKey = glyphs.BrushKey(id),
                };
            }).ToList(),
        };
    }

    /// <summary>The contextual key hint, from OverlayHint.Resolve. Null draws nothing.</summary>
    public string? Hint { get; init; }

    /// <summary>Names itself: REQUESTS MAY BE STALE, NO MICROPHONE, and so on. Null when healthy.</summary>
    public string? Fault { get; init; }
}

/// <summary>
/// The board, as a view rather than a window. Hosted in the agent's one window on its Board tab,
/// which is second-screen mode. Per 06-overlay-ux.md that is not a lesser fallback: it is the only
/// mode that works with exclusive fullscreen, and the first one built.
/// </summary>
/// <remarks>
/// Deliberately not a layered, click-through, topmost surface: those constraints exist only for
/// drawing over the game, and this is never doing that.
/// </remarks>
public partial class BoardView : UserControl
{
    /// <summary>
    /// How long a row takes to leave. Short enough that a filled request is gone before the next
    /// glance, long enough that the rows below settle rather than jump.
    /// </summary>
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(160));

    // Bound once and reconciled in place. Reassigning ItemsSource makes WPF tear down and rebuild
    // every item container, and the menu re-renders on EVERY navigation key: that rebuild is what
    // the input lag actually was. Same rule as the board rows.
    private readonly ObservableCollection<MenuOptionViewModel> _menuOptions = [];
    private readonly ObservableCollection<MenuOptionViewModel> _menuTrailing = [];

    private readonly ObservableCollection<BoardRowViewModel> _rows = [];
    private readonly ObservableCollection<BoardRowViewModel> _secondary = [];
    private readonly HashSet<BoardRowViewModel> _retiring = [];

    public BoardView()
    {
        InitializeComponent();

        // Bound once. RenderBoard reconciles these collections rather than replacing an ItemsSource,
        // which is the difference between a poll updating one age and a poll rebuilding the board.
        RowsList.ItemsSource = _rows;
        SecondaryList.ItemsSource = _secondary;
        MenuOptions.ItemsSource = _menuOptions;
        MenuTrailing.ItemsSource = _menuTrailing;

        // Establishes PanelGround and PanelTextEffect. Without it the DynamicResource lookups find
        // nothing and the panel paints transparent inside the window.
        SetOverlayMode(false);
    }

    /// <summary>Raised by the dev-only "Simulate PTT" button. Null in a production build's flow:
    /// nothing wires this event unless the composition root is running under the dev profile.</summary>
    public event EventHandler? SimulatePttRequested;

    /// <summary>
    /// Switches the panel between the two grounds it is drawn on. In the window it sits on Ground
    /// with an opaque Surface panel; over the game it sits on nothing with the mock's translucent
    /// Scrim, so the terrain showing through is the actual game.
    /// </summary>
    /// <remarks>
    /// Same rows, same header, same slot digits: 06-overlay-ux.md requires the two modes to be
    /// identical to read, so this changes the ground and the chrome and nothing else. The status
    /// line, the dev panel and the scrollbar are window furniture and have no place over a fight.
    /// </remarks>
    public void SetOverlayMode(bool overlay)
    {
        // Both keys are DynamicResource lookups from the panel and the strip template, so one
        // assignment repaints every surface that follows the mode.
        Resources["PanelGround"] = FindResource(overlay ? "Scrim" : "Surface");
        Resources["PanelTextEffect"] = overlay ? FindResource("TextScrim") : null;

        if (overlay)
        {
            Background = null;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.Margin = new Thickness(0);
            StatusText.Visibility = Visibility.Collapsed;
            DevPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "Ground");
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.Margin = new Thickness(0, 14, 0, 0);
            StatusText.Visibility = Visibility.Visible;
        }

        _isOverlay = overlay;
    }

    /// <summary>
    /// The rendered panel width. 380 in every mock; the Overlay tab lets it move because a 4K
    /// player reads 380 px as a stamp and a 1080p player reads 560 as half the screen.
    /// </summary>
    public void SetPanelWidth(double width) => PanelStack.Width = width;

    /// <summary>True while this view is drawing over the game rather than in the window.</summary>
    public bool IsOverlay => _isOverlay;

    private bool _isOverlay;

    /// <summary>Shows or hides the dev-only panel. Never true outside the dev profile.</summary>
    public void SetDevControlsVisible(bool visible) =>
        DevPanel.Visibility = visible && !_isOverlay ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Displays the coordinate the dev source (or any source) just answered with.</summary>
    public void ShowSimulatedPoint(string text) => SimulatedPointText.Text = text;

    /// <summary>The build line at the foot of the window, in the mock's watermark grey.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Renders the two header lines. Anything null collapses.</summary>
    public void SetHeader(BoardHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        HeaderTitle.Text = header.Title.ToUpperInvariant();
        HeaderCount.Text = header.PeopleCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        HeaderMap.Text = header.Where?.ToUpperInvariant() ?? string.Empty;
        HeaderRoles.ItemsSource = header.Roles;
        HeaderHint.Text = header.Hint ?? string.Empty;

        // A fault takes the right-hand slot and turns it amber: it outranks the invite code, which
        // is the one thing on that line somebody can look up later.
        if (header.Fault is { } fault)
        {
            HeaderRight.Text = fault.ToUpperInvariant();
            HeaderRight.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Warn");
            FaultDot.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderRight.Text = header.Right?.ToUpperInvariant() ?? string.Empty;
            HeaderRight.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Squad");
            FaultDot.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The cold-start state: paired but on no deployment. Not a fault; see AgentConfig.BelongsToNothing.</summary>
    public void ShowEmptyState(string title, string hint)
    {
        ArgumentNullException.ThrowIfNull(title);

        EmptyStateTitle.Text = title.ToUpperInvariant();
        EmptyStateHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
        _rows.Clear();
        _secondary.Clear();
        _retiring.Clear();
        OverflowRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Renders one snapshot of the board, as a reconcile against what is already on screen.
    /// </summary>
    /// <remarks>
    /// Keyed by ticket code, which is the row's identity everywhere else in the product. A row that
    /// is still there is updated in place, a row that moved slot is moved rather than rebuilt, a
    /// new row fades in, and a row that is gone fades and collapses out. Replacing the ItemsSource
    /// would rebuild every container on every five-second poll, which is the whole board flashing
    /// because one age went from 11s to 16s.
    /// </remarks>
    public void RenderBoard(
        IReadOnlyList<BoardRowViewModel> rows,
        IReadOnlyList<BoardRowViewModel> yours,
        int overflowCount,
        int overflowUrgentCount,
        int inProgressCount = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(yours);

        EmptyState.Visibility = Visibility.Collapsed;
        SpendAnimationBudget(rows);
        Reconcile(_rows, rows, RowsList);
        Reconcile(_secondary, yours, SecondaryList);
        YoursSection.Visibility = yours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InProgressText.Text = inProgressCount > 0
            ? $"{inProgressCount.ToString(CultureInfo.InvariantCulture)} IN PROGRESS"
            : string.Empty;

        OverflowRow.Visibility = overflowCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverflowText.Text = overflowCount > 0
            ? $"...{overflowCount.ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;
        OverflowUrgentText.Text = overflowUrgentCount > 0
            ? $"{overflowUrgentCount.ToString(CultureInfo.InvariantCulture)} URGENT"
            : string.Empty;
    }

    /// <summary>
    /// Spends the board-wide animation budget: at most two countdown bars, at most one pulsing
    /// slot digit, both on the soonest to expire.
    /// </summary>
    /// <remarks>
    /// 06-overlay-ux.md: "Animation is a budget, not a per-row property." Three or four digits
    /// pulsing turns the digit column into the moving thing, which destroys the one property that
    /// makes a slot findable inside the 400 ms glance. CountdownFraction is how much life is left,
    /// so the narrowest bar is the soonest to expire and no extra field is needed to rank them.
    /// </remarks>
    private static void SpendAnimationBudget(IReadOnlyList<BoardRowViewModel> rows)
    {
        var expiring = rows
            .Where(r => r.HasCountdown)
            .OrderBy(r => r.CountdownFraction)
            .ToList();

        foreach (var row in rows)
        {
            row.Pulses = false;
        }

        // Bars are no longer rationed. Every open row drains one over its 120 s, because the bar is
        // the only thing telling its reader the row will cancel itself, and a row without one would
        // read as staying put. The bar is a slow fill, not motion: nothing about it competes for
        // the eye the way a pulse does.
        //
        // The PULSE is still a budget of exactly one, on the soonest to expire. That is what the
        // rule was protecting: three or four digits pulsing turns the digit column into the moving
        // thing and destroys the one property that makes a slot findable inside the 400 ms glance.
        if (expiring.Count > 0)
        {
            expiring[0].Pulses = true;
        }
    }

    /// <summary>
    /// Brings <paramref name="live"/> in line with <paramref name="next"/>, touching only what
    /// actually changed. Rows already retiring are ignored as matches, so a fading row cannot be
    /// adopted as the container for a different ticket half way through its exit.
    /// </summary>
    /// <summary>Draws the menu, or takes it off the surface when it is closed.</summary>
    public void RenderMenu(MenuViewModel menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        foreach (var row in AllRows())
        {
            row.IsHighlighted = menu.HighlightedSlot is { } slot
                && row.SlotDisplay == slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        MenuPanel.Visibility = menu.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        Sync(_menuTrailing, menu.Trailing);
        MenuFooter.Visibility = _menuTrailing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!menu.IsVisible)
        {
            _menuOptions.Clear();
            _menuTrailing.Clear();
            MenuFooter.Visibility = Visibility.Collapsed;
            return;
        }

        // Armed but not open: the title and the one line telling you how to get in, and no list,
        // because there is nothing to choose from yet.
        if (!menu.IsOpen)
        {
            MenuTitle.Text = menu.Title;
            _menuOptions.Clear();
            MenuTyped.Visibility = Visibility.Collapsed;
            MenuLegend.Text = menu.Legend;
            MenuLegend.Visibility = Visibility.Visible;
            return;
        }

        MenuTitle.Text = menu.Title;

        Sync(_menuOptions, menu.Options);
        MenuTyped.Text = menu.Typed ?? string.Empty;
        MenuTyped.Visibility = menu.Typed is null ? Visibility.Collapsed : Visibility.Visible;

        // Remap aware: the legend is built from the live bindings, never written down here.
        MenuLegend.Text = menu.Legend;
        MenuLegend.Visibility = string.IsNullOrEmpty(menu.Legend) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Reconcile(
        ObservableCollection<BoardRowViewModel> live,
        IReadOnlyList<BoardRowViewModel> next,
        ItemsControl host)
    {
        var wanted = new Dictionary<string, BoardRowViewModel>(StringComparer.Ordinal);
        foreach (var row in next)
        {
            wanted[row.TicketCode] = row;
        }

        // Out first, so the indices below are placing into a list that holds only survivors.
        for (var i = live.Count - 1; i >= 0; i--)
        {
            var existing = live[i];
            if (wanted.ContainsKey(existing.TicketCode) && !_retiring.Contains(existing))
            {
                continue;
            }

            if (_retiring.Add(existing))
            {
                Retire(existing, live, host);
            }
        }

        for (var target = 0; target < next.Count; target++)
        {
            var incoming = next[target];
            var found = IndexOfLive(live, incoming.TicketCode);

            if (found < 0)
            {
                live.Insert(Math.Min(target, live.Count), incoming);
                continue;
            }

            live[found].CopyFrom(incoming);
            live[found].Pulses = incoming.Pulses;

            if (found != target && target < live.Count)
            {
                // Move rather than remove and re-insert: a move keeps the container, so a row that
                // changed slot slides into place instead of replaying its entrance.
                live.Move(found, target);
            }
        }
    }

    /// <summary>The index of a live row that is not on its way out, or -1.</summary>
    /// <summary>
    /// Brings a bound collection in line with the next one, touching only what changed.
    /// </summary>
    /// <remarks>
    /// The menu redraws on every navigation key. Replacing the collection each time rebuilds every
    /// container WPF has already realised, which is expensive enough to feel like the keys are
    /// lagging behind the hand.
    /// </remarks>
    private static void Sync(
        ObservableCollection<MenuOptionViewModel> live,
        IReadOnlyList<MenuOptionViewModel> next)
    {
        while (live.Count > next.Count)
        {
            live.RemoveAt(live.Count - 1);
        }

        for (var i = 0; i < next.Count; i++)
        {
            if (i >= live.Count)
            {
                live.Add(next[i]);
            }
            else if (!live[i].Equals(next[i]))
            {
                live[i] = next[i];
            }
        }
    }

    /// <summary>Every row on the surface, both lists, for anything that applies to all of them.</summary>
    private IEnumerable<BoardRowViewModel> AllRows() => _rows.Concat(_secondary);

    private int IndexOfLive(ObservableCollection<BoardRowViewModel> live, string ticketCode)
    {
        for (var i = 0; i < live.Count; i++)
        {
            if (!_retiring.Contains(live[i])
                && string.Equals(live[i].TicketCode, ticketCode, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Fades and collapses a row out, then removes it. The collapse is what stops everything below
    /// jumping up by one row height the instant a request is filled.
    /// </summary>
    private void Retire(
        BoardRowViewModel row,
        ObservableCollection<BoardRowViewModel> live,
        ItemsControl host)
    {
        void Drop()
        {
            _ = _retiring.Remove(row);
            _ = live.Remove(row);
        }

        if (host.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container
            || container.ActualHeight <= 0)
        {
            Drop();
            return;
        }

        var storyboard = new Storyboard();

        var fade = new DoubleAnimation(0, ExitDuration);
        Storyboard.SetTarget(fade, container);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(fade);

        var collapse = new DoubleAnimation(container.ActualHeight, 0, ExitDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(collapse, container);
        Storyboard.SetTargetProperty(collapse, new PropertyPath(HeightProperty));
        storyboard.Children.Add(collapse);

        storyboard.Completed += (_, _) =>
        {
            // Hand the container back to the generator in the state it was found in: it is pooled
            // and a leftover zero height would render the next row that lands in it invisible.
            container.BeginAnimation(HeightProperty, null);
            container.BeginAnimation(OpacityProperty, null);
            container.Height = double.NaN;
            container.Opacity = 1;
            Drop();
        };

        storyboard.Begin();
    }

    private void OnSimulatePttClick(object sender, RoutedEventArgs e) =>
        SimulatePttRequested?.Invoke(this, EventArgs.Empty);
}
