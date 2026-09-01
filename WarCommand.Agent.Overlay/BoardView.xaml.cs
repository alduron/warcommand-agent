using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The panel header, as drawn in docs/design/mocks/OverlayHeader.dc.html. Two lines: who and where
/// on the first, roles and the help chord on the second, with an amber dot when a fault is showing.
/// </summary>
/// <remarks>
/// The four faults are not the same amber word: each one names itself. A null field renders
/// nothing rather than a placeholder.
/// </remarks>
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

    /// <summary>The viewer's subscribed roles, lowercase, space separated.</summary>
    public string? Roles { get; init; }

    /// <summary>'RightAlt+H ?'. The one chord that is always true.</summary>
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
    public BoardView()
    {
        InitializeComponent();
    }

    /// <summary>Raised by the dev-only "Simulate PTT" button. Null in a production build's flow:
    /// nothing wires this event unless the composition root is running under the dev profile.</summary>
    public event EventHandler? SimulatePttRequested;

    /// <summary>Shows or hides the dev-only panel. Never true outside the dev profile.</summary>
    public void SetDevControlsVisible(bool visible) =>
        DevPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

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
        HeaderRoles.Text = header.Roles ?? string.Empty;
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
        RowsList.ItemsSource = Array.Empty<BoardRowViewModel>();
        SecondaryList.ItemsSource = Array.Empty<BoardRowViewModel>();
        OverflowRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>Renders one snapshot of the board. Called on every poll; there is no delta path here.</summary>
    public void RenderBoard(
        IReadOnlyList<BoardRowViewModel> rows,
        IReadOnlyList<BoardRowViewModel> secondaryStrip,
        int overflowCount,
        int overflowUrgentCount)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        RowsList.ItemsSource = rows;
        SecondaryList.ItemsSource = secondaryStrip;

        OverflowRow.Visibility = overflowCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverflowText.Text = overflowCount > 0
            ? $"...{overflowCount.ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;
        OverflowUrgentText.Text = overflowUrgentCount > 0
            ? $"{overflowUrgentCount.ToString(CultureInfo.InvariantCulture)} URGENT"
            : string.Empty;
    }

    private void OnSimulatePttClick(object sender, RoutedEventArgs e) =>
        SimulatePttRequested?.Invoke(this, EventArgs.Empty);
}
