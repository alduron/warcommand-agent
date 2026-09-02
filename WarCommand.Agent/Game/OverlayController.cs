
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
public sealed class OverlayController : IGameWindowSink, ISuspendable, IDisposable
{
    /// <summary>
    /// What Dim multiplies the chosen opacity by. Dim is still readable at a glance from a second
    /// monitor; it is not "nearly gone", which would make the mode useless to the people who ask
    /// for it.
    /// </summary>
    private const double DimFactor = 0.5;

    private readonly Dispatcher _dispatcher;
    private readonly Func<OverlayWindow> _factory;
    private readonly Action<string, string>? _notify;

    private OverlayWindow? _window;
    private AgentSettings _settings;
    private ScreenRect _gameRect = ScreenRect.Empty;
    private OverlayVisibility _visibility = OverlayVisibility.Hide;
    private bool _gameRunning;
    private bool _warnedAboutFullscreen;
    private bool _suspended;
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

    /// <summary>True while Panic is engaged. Nothing draws.</summary>
    public bool IsSuspended => _suspended;

    /// <summary>True when the surface draws with no game window at all.</summary>
    public bool DrawsWithNoGame => !_gameRunning && _settings.ShowWithoutGame;

    /// <summary>The monitor the overlay is anchored to right now, for the tray row.</summary>
    public string DisplayLabel()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var chosen = _settings.DisplayDeviceName is { } name
            ? Array.FindIndex(screens, s => string.Equals(s.DeviceName, name, StringComparison.Ordinal))
            : Array.FindIndex(screens, s => s.Primary);

        return chosen >= 0
            ? DisplayName(screens[chosen], chosen)
            : "display 1";
    }

    /// <summary>
    /// "display 2 (2560x1440)". The device name is what is persisted, but nobody recognises
    /// \.\DISPLAY2, and every monitor is called Generic PnP Monitor.
    /// </summary>
    public static string DisplayName(System.Windows.Forms.Screen screen, int index)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var bounds = screen.Bounds;
        var label = FormattableString.Invariant(
            $"display {index + 1} ({bounds.Width}x{bounds.Height})");

        return screen.Primary ? label + ", primary" : label;
    }

    /// <summary>
    /// What the tray row says beside "Overlay". Null when it is drawing and the row reads a plain
    /// on; otherwise the reason it is not, which is nearly always that the game is not up.
    /// </summary>
    public string? Hint
    {
        get
        {
            if (_suspended)
            {
                return "panic";
            }

            if (!_settings.OverlayEnabled)
            {
                return null;
            }

            if (IsDrawing)
            {
                return _gameRunning ? null : "no game, on " + DisplayLabel();
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

    /// <summary>
    /// Panic. The surface stops drawing on the same press as every other subsystem, and it goes
    /// straight to hidden rather than fading: a kill switch that takes 180 ms to finish is not one.
    /// </summary>
    /// <remarks>
    /// Binding rule 7. <see cref="WarCommand.Agent.Input.PanicSubsystem.OverlayDrawing"/> is what
    /// this registers as, and <c>PanicSwitch.Arm</c> refuses to arm until it does.
    /// </remarks>
    public void Suspend()
    {
        _suspended = true;
        OnUi(() => _window?.HideNow());
    }

    /// <summary>
    /// Panic released. The visibility is re-derived from the tracker rather than restored from
    /// whatever was held before, so a game that closed while suspended does not come back drawing.
    /// </summary>
    public void Resume()
    {
        _suspended = false;
        OnUi(Apply);
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

        // Panic outranks every other input. Suspended means nothing draws, whatever the game,
        // the settings or the tracker say.
        var wanted = !_suspended
            && _settings.OverlayEnabled
            && (_visibility != OverlayVisibility.Hide || DrawsWithNoGame);

        if (!wanted)
        {
            // Faded out, not switched off. An alt-tab to Discord should not read as a crash.
            _window?.FadeOutAndHide();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var window = _window ??= _factory();

        // With a game up the overlay follows its client rect. With no game it falls back to the
        // chosen monitor, because a board anchored to a monitor the game is not on is a board
        // nobody can see, and a board anchored to nothing is a board nobody can see either.
        var target = _gameRect.IsEmpty ? ChosenWorkArea() : _gameRect;
        var bounds = OverlayLayout.Place(target, _settings.Anchor, _settings.ClampedWidth);

        if (!bounds.IsEmpty)
        {
            window.ApplyAnchor(_settings.Anchor);
            window.ApplyBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        // Dim is for a game that is up and not focused. Drawing with no game at all is not dim:
        // there is nothing to be behind, and a half-faded board on an empty desktop reads as a
        // rendering fault rather than a mode.
        var opacity = OverlayWindow.OpacityFor(_settings.Opacity);
        var dimmed = _visibility == OverlayVisibility.Dim && _gameRunning;
        window.FadeTo(dimmed ? opacity * DimFactor : opacity);

        // Re-asserted on every apply: a game that goes fullscreen-borderless re-creates its window
        // above ours, and Topmost alone does not win that race.
        window.Topmost = true;

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The monitor the user picked, by Windows device name, or the primary. A name that no longer
    /// resolves falls back to the primary rather than to nothing: unplugging a monitor must not
    /// make the overlay disappear with no way to find it again.
    /// </summary>
    private ScreenRect ChosenWorkArea()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;

        var screen = _settings.DisplayDeviceName is { } name
            ? Array.Find(screens, s => string.Equals(s.DeviceName, name, StringComparison.Ordinal))
            : null;

        screen ??= System.Windows.Forms.Screen.PrimaryScreen;
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
