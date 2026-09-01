using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Tray;
using WarCommand.Agent.Input;

namespace WarCommand.Agent.Tray;

/// <summary>
/// The tray icon and its menu. The icon's colour is the connection state and it is the only
/// always-visible health signal, per 10-agent-spec.md: green connected, amber reconnecting, grey
/// panicked or unpaired. The field colour carries the state; the shield mark itself is never
/// recoloured.
/// </summary>
/// <remarks>
/// <para>
/// Registers as <see cref="PanicSubsystem.TrayIndicator"/>. Panic always wins: while suspended the
/// icon is forced grey regardless of the last reported connection state, and resuming re-derives
/// it rather than trusting what was showing before.
/// </para>
/// <para>
/// The menu holds no rules. <see cref="TrayMenu.Build"/> decides the rows from a
/// <see cref="TrayMenuState"/> snapshot taken every time the menu opens, so this class is a
/// renderer and the whole tree is unit-tested with no message loop. Supply the snapshot through
/// <see cref="StateProvider"/>; whatever it leaves null simply does not render.
/// </para>
/// </remarks>
public sealed class TrayIconController : ISuspendable, IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _connectedIcon;
    private readonly Icon _reconnectingIcon;
    private readonly Icon _panickedIcon;

    private RealtimeConnectionState _lastState = RealtimeConnectionState.Idle;
    private bool _suspended;

    public TrayIconController()
    {
        _connectedIcon = LoadIcon("tray-connected.ico");
        _reconnectingIcon = LoadIcon("tray-reconnecting.ico");
        _panickedIcon = LoadIcon("tray-panicked.ico");

        // Windows 11 menu metrics from the mock: 14px Segoe UI, 300 wide, no image gutter.
        _menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            Font = new Font("Segoe UI Variable Text", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            // The margin stays, painted the surface colour: it is the indent every row in the mock
            // shares, and it is where the title row's state dot sits.
            ShowImageMargin = true,
            MinimumSize = new Size(300, 0),
            Padding = new Padding(0, 4, 0, 4),
            BackColor = TrayMenuRenderer.Surface,
            ForeColor = TrayMenuRenderer.Text,
        };
        _menu.Opening += OnMenuOpening;

        // Built once here as well as on every open. ContextMenuStrip refuses to show while it holds
        // no items, and it decides that before Opening can fill it, so an empty menu never opens.
        Rebuild();

        _notifyIcon = new NotifyIcon
        {
            Icon = _panickedIcon,
            Text = "WarCommand",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => Raise(TrayCommand.ToggleSecondScreen);
    }

    /// <summary>Raised when a menu row is clicked. Dev force-state rows are applied here first.</summary>
    public event EventHandler<TrayCommand>? CommandInvoked;

    /// <summary>The live menu. Exposed so a test can assert it is never empty before it opens.</summary>
    internal ContextMenuStrip Menu => _menu;

    /// <summary>
    /// Supplies the menu snapshot. Called on every open, so it must be cheap and must not block:
    /// read fields the composition root already holds rather than calling the API.
    /// </summary>
    public Func<TrayMenuState> StateProvider { get; set; } = static () => new TrayMenuState();

    /// <summary>
    /// Drives the icon from the realtime socket's state. Idle, Connecting and Stopped all read as
    /// the grey "not connected" field: to a user there is no useful difference between "never
    /// paired" and "the socket gave up", both mean the same thing is not happening right now.
    /// </summary>
    public void SetConnectionState(RealtimeConnectionState state)
    {
        _lastState = state;
        if (!_suspended)
        {
            Apply(state);
        }
    }

    /// <summary>
    /// A balloon naming where the icon is. Windows 11 files a new tray icon into the overflow
    /// flyout by default, so a first run otherwise looks like nothing launched at all.
    /// </summary>
    public void ShowLocationHint()
    {
        _notifyIcon.BalloonTipTitle = "WarCommand";
        _notifyIcon.BalloonTipText = "running, in the notification area under ^";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(10_000);
    }

    /// <summary>The hover tooltip. Windows caps it at 63 characters, so it is truncated here.</summary>
    public void SetTooltip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>Panic engaged. Forces grey no matter what the socket is doing.</summary>
    public void Suspend()
    {
        _suspended = true;
        _notifyIcon.Icon = _panickedIcon;
    }

    /// <summary>Panic released. Re-derives the icon from the last known connection state.</summary>
    public void Resume()
    {
        _suspended = false;
        Apply(_lastState);
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Rebuild();

        // ContextMenuStrip sets Cancel before this handler runs whenever it was empty at the time,
        // so a menu populated here would never appear. Clearing it is what makes the open stick.
        e.Cancel = _menu.Items.Count == 0;
    }

    /// <summary>Rebuilds the rows from a fresh snapshot. Disposes what it replaces.</summary>
    private void Rebuild()
    {
        var state = StateProvider() with
        {
            // The icon owns these two, so the snapshot never gets to disagree with what is showing.
            Indicator = ToIndicator(_lastState),
            PanicEngaged = _suspended,
        };

        var previous = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in previous)
        {
            item.Dispose();
        }

        foreach (var item in TrayMenu.Build(state))
        {
            _ = _menu.Items.Add(Render(item));
        }
    }

    private ToolStripItem Render(TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new ToolStripSeparator();
        }

        var rendered = new ToolStripMenuItem(item.Text)
        {
            Enabled = item.IsEnabled,
            CheckState = CheckState.Unchecked,
            Padding = new Padding(0, 4, 0, 4),
        };

        // The right-aligned dim value the mock draws: '31 people', 'off', 'admin+'. The shortcut
        // slot is exactly that column, already right-aligned and already the correct grey.
        if (item.Value is { } value)
        {
            rendered.ShowShortcutKeys = true;
            rendered.ShortcutKeyDisplayString = value;
        }

        if (item.IsTitle)
        {
            rendered.Font = new Font(_menu.Font, FontStyle.Bold);
            rendered.Image = StateDot();
            rendered.ForeColor = TrayMenuRenderer.Text;
        }
        else if (item.IsHeading)
        {
            rendered.Font = new Font(_menu.Font.FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);
            rendered.ForeColor = TrayMenuRenderer.TextFaint;
        }
        else if (!item.IsEnabled)
        {
            rendered.ForeColor = TrayMenuRenderer.TextFaint;
        }

        foreach (var child in item.Children)
        {
            _ = rendered.DropDownItems.Add(Render(child));
        }

        if (item.Command is not TrayCommand.None)
        {
            var command = item.Command;
            rendered.Click += (_, _) => Raise(command);
        }

        return rendered;
    }

    /// <summary>
    /// The 14px rounded square beside the product name, in the mock's own three colours. Same
    /// meaning as the icon's field: green connected, amber reconnecting, grey panicked or unpaired.
    /// </summary>
    private Bitmap StateDot()
    {
        var colour = _suspended
            ? TrayMenuRenderer.Grey
            : ToIndicator(_lastState) switch
            {
                TrayIndicator.Connected => TrayMenuRenderer.Ok,
                TrayIndicator.Reconnecting => TrayMenuRenderer.Warn,
                _ => TrayMenuRenderer.Grey,
            };

        var dot = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(dot);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(colour);
        graphics.FillEllipse(brush, 2, 2, 12, 12);
        return dot;
    }

    /// <summary>
    /// Dev force-state rows are handled here rather than in the composition root, so iterating on
    /// the three icons needs no API, no socket and no wiring. See DEVELOPING.md.
    /// </summary>
    private void Raise(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.DevForceConnected:
                SetConnectionState(RealtimeConnectionState.Connected);
                break;
            case TrayCommand.DevForceReconnecting:
                SetConnectionState(RealtimeConnectionState.Reconnecting);
                break;
            case TrayCommand.DevForceOffline:
                SetConnectionState(RealtimeConnectionState.Idle);
                break;
            default:
                break;
        }

        CommandInvoked?.Invoke(this, command);
    }

    private void Apply(RealtimeConnectionState state) => _notifyIcon.Icon = ToIndicator(state) switch
    {
        TrayIndicator.Connected => _connectedIcon,
        TrayIndicator.Reconnecting => _reconnectingIcon,
        _ => _panickedIcon,
    };

    private static TrayIndicator ToIndicator(RealtimeConnectionState state) => state switch
    {
        RealtimeConnectionState.Connected => TrayIndicator.Connected,
        RealtimeConnectionState.Reconnecting => TrayIndicator.Reconnecting,
        _ => TrayIndicator.Offline,
    };

    private static Icon LoadIcon(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", fileName);
        return new Icon(path);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _connectedIcon.Dispose();
        _reconnectingIcon.Dispose();
        _panickedIcon.Dispose();
    }
}
