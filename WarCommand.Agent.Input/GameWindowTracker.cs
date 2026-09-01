namespace WarCommand.Agent.Input;

/// <summary>A rectangle in screen coordinates. The overlay positions against this.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    /// <summary>Nothing.</summary>
    public static ScreenRect Empty => default;

    /// <summary>False for a minimized or unmapped window.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>One observation of the game window.</summary>
public readonly record struct GameWindowScan(
    nint Handle,
    ScreenRect ClientRect,
    bool IsForeground,
    bool ExclusiveFullscreen);

/// <summary>What the overlay should do.</summary>
public enum OverlayVisibility
{
    /// <summary>Draw.</summary>
    Show = 0,

    /// <summary>Stop drawing entirely.</summary>
    Hide,

    /// <summary>Keep drawing, dimmed. Second-screen users only.</summary>
    Dim,
}

/// <summary>
/// The <c>Overlay when the game is not focused</c> setting. <see cref="Hide"/> is the default: a
/// topmost layered window that keeps rendering coordinates, callsigns and ticket codes draws them
/// over Discord, a browser and a live stream.
/// </summary>
public enum OverlayFocusBehavior
{
    /// <summary>Stop drawing. The default.</summary>
    Hide = 0,

    /// <summary>Keep drawing, dimmed. The opt-in for people running a second monitor.</summary>
    Dim,
}

/// <summary>What the next presence heartbeat reports.</summary>
public enum PresenceState
{
    /// <summary>The game is running.</summary>
    Active = 0,

    /// <summary>
    /// The game is gone. Without this the game dies, the agent lives, heartbeats keep flowing, and
    /// providers read BEAR ACCEPTED on a mission nobody is running.
    /// </summary>
    Idle,
}

/// <summary>Everything the game window watcher raises.</summary>
public interface IGameWindowSink
{
    /// <summary>A window belonging to a process in <c>game.process_names</c> appeared.</summary>
    void GameWindowFound(GameWindowScan window);

    /// <summary>Its client rect moved or resized. The overlay repositions against this.</summary>
    void ClientRectChanged(ScreenRect clientRect);

    /// <summary>The game holds the display exclusively. Raise the borderless-windowed prompt.</summary>
    void ExclusiveFullscreenDetected();

    /// <summary>That window went away.</summary>
    void GameWindowLost();

    /// <summary>Hotkey processing. Panic is unaffected either way.</summary>
    void HotkeysEnabled(bool enabled);

    /// <summary>What the overlay should do now.</summary>
    void OverlayVisibilityChanged(OverlayVisibility visibility);

    /// <summary>What the next presence heartbeat should report.</summary>
    void PresenceStateChanged(PresenceState state);
}

/// <summary>
/// Turns a stream of observations into the signals the rest of the agent needs. Pure: no Win32, no
/// timer, no clock, so every transition is testable.
/// </summary>
public sealed class GameWindowTracker
{
    private readonly IGameWindowSink _sink;

    private bool _seen;
    private bool _running;
    private bool _foreground;
    private bool _exclusiveFullscreen;
    private ScreenRect _clientRect;

    /// <summary>Creates a tracker.</summary>
    public GameWindowTracker(IGameWindowSink sink, OverlayFocusBehavior behavior = OverlayFocusBehavior.Hide)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        Behavior = behavior;
    }

    /// <summary>The overlay setting. Changing it re-raises the current visibility.</summary>
    public OverlayFocusBehavior Behavior { get; private set; }

    /// <summary>True while a game window exists.</summary>
    public bool GameIsRunning => _running;

    /// <summary>True while that window is the foreground one.</summary>
    public bool GameIsForeground => _running && _foreground;

    /// <summary>The last client rect seen, or <see cref="ScreenRect.Empty"/>.</summary>
    public ScreenRect ClientRect => _clientRect;

    /// <summary>Applies a settings change and re-raises the visibility it implies.</summary>
    public void SetBehavior(OverlayFocusBehavior behavior)
    {
        Behavior = behavior;
        _sink.OverlayVisibilityChanged(CurrentVisibility());
    }

    /// <summary>One observation. Null means no window matched any name in <c>game.process_names</c>.</summary>
    public void Observe(GameWindowScan? scan)
    {
        var first = !_seen;
        _seen = true;

        if (scan is { } window)
        {
            OnPresent(window, first);
        }
        else
        {
            OnAbsent(first);
        }
    }

    private void OnPresent(GameWindowScan window, bool first)
    {
        var appeared = !_running;
        _running = true;

        if (appeared)
        {
            _clientRect = window.ClientRect;
            _foreground = window.IsForeground;
            _exclusiveFullscreen = false;
            _sink.GameWindowFound(window);
            _sink.HotkeysEnabled(true);
            _sink.PresenceStateChanged(PresenceState.Active);
            _sink.OverlayVisibilityChanged(CurrentVisibility());
        }
        else
        {
            if (!window.ClientRect.IsEmpty && window.ClientRect != _clientRect)
            {
                _clientRect = window.ClientRect;
                _sink.ClientRectChanged(_clientRect);
            }

            if (window.IsForeground != _foreground)
            {
                _foreground = window.IsForeground;
                _sink.OverlayVisibilityChanged(CurrentVisibility());
            }
            else if (first)
            {
                _sink.OverlayVisibilityChanged(CurrentVisibility());
            }
        }

        if (window.ExclusiveFullscreen && !_exclusiveFullscreen)
        {
            _exclusiveFullscreen = true;
            _sink.ExclusiveFullscreenDetected();
        }
        else if (!window.ExclusiveFullscreen)
        {
            _exclusiveFullscreen = false;
        }
    }

    private void OnAbsent(bool first)
    {
        if (!_running && !first)
        {
            return;
        }

        var wasRunning = _running;
        _running = false;
        _foreground = false;
        _exclusiveFullscreen = false;
        _clientRect = ScreenRect.Empty;

        if (wasRunning)
        {
            _sink.GameWindowLost();
        }

        // Panic is not covered by this. It stays armed with no game window at all.
        _sink.HotkeysEnabled(false);
        _sink.OverlayVisibilityChanged(CurrentVisibility());
        _sink.PresenceStateChanged(PresenceState.Idle);
    }

    private OverlayVisibility CurrentVisibility()
    {
        if (_running && _foreground)
        {
            return OverlayVisibility.Show;
        }

        return Behavior == OverlayFocusBehavior.Dim ? OverlayVisibility.Dim : OverlayVisibility.Hide;
    }
}
