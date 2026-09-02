
using System.Windows.Threading;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Game;

/// <summary>
/// Owns the in-game surface: when it draws, where, how bright, and what the tray says about it.
/// </summary>
/// <remarks>
/// <see cref="GameWindowTracker"/> decides show, hide or dim and this obeys; the two are kept apart
/// because the decision is pure and testable and the window is neither. Every sink call arrives on
/// the watcher's timer thread and is marshalled onto the UI dispatcher here rather than at each
/// call site.
/// </remarks>
public sealed class OverlayController : IGameWindowSink, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<OverlayWindow> _factory;
    private readonly Action<string, string>? _notify;

    private OverlayWindow? _window;
    private AgentSettings _settings;
    private ScreenRect _gameRect = ScreenRect.Empty;
    private OverlayVisibility _visibility = OverlayVisibility.Hide;
    private bool _gameRunning;
    private bool _warnedAboutFullscreen;
    private bool _disposed;

    /// <summary>Creates the controller. The window is not built until it first has to draw.</summary>
    /// <param name="dispatcher">The UI dispatcher. Every sink call hops onto it.</param>
    /// <param name="settings">The current settings. Anchor, width, opacity and the master switch.</param>
    /// <param name="factory">Builds the surface. Injected so a test can supply nothing at all.</param>
    /// <param name="notify">Title and body for a tray balloon. Null in a headless test.</param>
    public OverlayController(
        Dispatcher dispatcher,
        AgentSettings settings,
        Func<OverlayWindow>? factory = null,
        Action<string, string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(settings);

        _dispatcher = dispatcher;
        _settings = settings;
        _factory = factory ?? (() => new OverlayWindow());
        _notify = notify;
    }

    /// <summary>Raised whenever <see cref="Hint"/> changes, so the tray row can be re-read.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The surface, once it has been built. Null before the first draw.</summary>
    public OverlayWindow? Window => _window;

    /// <summary>The master switch, as the tray row renders it.</summary>
    public bool IsEnabled => _settings.OverlayEnabled;

    /// <summary>True while the surface is actually on screen.</summary>
    public bool IsDrawing => _window is { IsVisible: true };

    /// <summary>
    /// What the tray row says beside "Overlay". Null when it is drawing and the row reads a plain
    /// on; otherwise the reason it is not, which is nearly always that the game is not up.
    /// </summary>
    public string? Hint
    {
        get
        {
            if (!_settings.OverlayEnabled)
            {
                return null;
            }

            if (IsDrawing)
            {
                return null;
            }

            return _gameRunning ? "game not focused" : "waiting for game";
        }
    }

    /// <summary>
    /// Adopts a settings change: the master switch, the anchor, the width, the opacity and the
    /// unfocused behaviour, applied in one pass so a save can never half-land.
    /// </summary>
    public void ApplySettings(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        OnUi(Apply);
    }

    /// <inheritdoc />
    public void GameWindowFound(GameWindowScan window)
    {
        _gameRunning = true;
        _gameRect = window.ClientRect;
        OnUi(Apply);
    }

    /// <inheritdoc />
    public void ClientRectChanged(ScreenRect clientRect)
    {
        _gameRect = clientRect;
        OnUi(Apply);
    }

    /// <inheritdoc />
    public void GameWindowLost()
    {
        _gameRunning = false;
        _gameRect = ScreenRect.Empty;
        _warnedAboutFullscreen = false;
        OnUi(Apply);
    }

    /// <inheritdoc />
    public void OverlayVisibilityChanged(OverlayVisibility visibility)
    {
        _visibility = visibility;
        OnUi(Apply);
    }

    /// <summary>
    /// A topmost layered window cannot draw over exclusive fullscreen. Said once per game launch,
    /// naming the setting to change, because silence here reads as a broken overlay.
    /// </summary>
    public void ExclusiveFullscreenDetected()
    {
        if (_warnedAboutFullscreen)
        {
            return;
        }

        _warnedAboutFullscreen = true;
        _notify?.Invoke(
            "Overlay needs Borderless Windowed",
            "Wardogs > Settings > Display. Until then, the board is in second-screen mode.");
    }

    /// <summary>Not this subsystem's concern. Hotkeys are gated in the input bridge.</summary>
    public void HotkeysEnabled(bool enabled)
    {
    }

    /// <summary>Not this subsystem's concern. Presence is reported by the realtime client.</summary>
    public void PresenceStateChanged(PresenceState state)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OnUi(() =>
        {
            _window?.Close();
            _window = null;
        });
    }

    /// <summary>
    /// The one place the surface is shown, hidden, moved or dimmed. Every input above funnels
    /// here so there is a single answer to "why is it doing that".
    /// </summary>
    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var wanted = _settings.OverlayEnabled && _visibility != OverlayVisibility.Hide;

        if (!wanted)
        {
            _window?.Hide();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var window = _window ??= _factory();

        // With no game window there is no client rect to anchor to. The primary monitor's work
        // area is the only honest fallback, and it is only ever reached in Dim, which is the
        // deliberate second-monitor mode.
        var target = _gameRect.IsEmpty ? PrimaryWorkArea() : _gameRect;
        var bounds = OverlayLayout.Place(target, _settings.Anchor, _settings.ClampedWidth);

        if (!bounds.IsEmpty)
        {
            window.ApplyAnchor(_settings.Anchor);
            window.ApplyBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        var opacity = OverlayWindow.OpacityFor(_settings.Opacity);
        window.Opacity = _visibility == OverlayVisibility.Dim ? opacity * 0.5 : opacity;

        if (!window.IsVisible)
        {
            window.Show();
        }

        // Re-asserted on every apply: a game that goes fullscreen-borderless re-creates its window
        // above ours, and Topmost alone does not win that race.
        window.Topmost = true;

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ScreenRect PrimaryWorkArea()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return ScreenRect.Empty;
        }

        var area = screen.WorkingArea;
        return new ScreenRect(area.Left, area.Top, area.Width, area.Height);
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = _dispatcher.BeginInvoke(action);
    }
}
