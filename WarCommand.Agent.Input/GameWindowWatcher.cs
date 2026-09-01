using System.Diagnostics;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Input.Hooks;

namespace WarCommand.Agent.Input;

/// <summary>
/// Polls for a window whose process matches any name in <c>game.process_names</c>, at
/// <c>game.window_poll_ms</c>. A list and an interval, both from
/// <c>contracts/game-profile.json</c> and never a literal: UE5 shipping builds get renamed between
/// Early Access and release, and a launcher may sit in front of the executable.
/// </summary>
/// <remarks>
/// Reading the foreground window handle and the owning process id is a public shell API. Nothing is
/// opened, read, written or injected inside the game process.
/// </remarks>
public sealed class GameWindowWatcher : IForegroundProbe, IDisposable
{
    private readonly GameWindowTracker _tracker;
    private readonly IInputLog _log;
    private readonly object _gate = new();

    private string[] _processNames;
    private int _pollMs;
    private uint[] _gamePids = [];
    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    /// <summary>Creates the watcher. Nothing is polled until <see cref="Start"/> is called.</summary>
    public GameWindowWatcher(
        GameProfile profile,
        IGameWindowSink sink,
        OverlayFocusBehavior behavior = OverlayFocusBehavior.Hide,
        IInputLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sink);

        _tracker = new GameWindowTracker(sink, behavior);
        _log = log ?? NullInputLog.Instance;
        _processNames = [.. profile.Game.ProcessNames];
        _pollMs = profile.Game.WindowPollMs;
    }

    /// <summary>The transitions this watcher drives. Exposed so the overlay setting can be changed.</summary>
    public GameWindowTracker Tracker => _tracker;

    /// <summary>
    /// True when the foreground window belongs to a game process. Read live, not from the poll, so a
    /// chord pressed one millisecond after alt-tab is already inert.
    /// </summary>
    public bool GameIsForeground
    {
        get
        {
            uint[] pids;
            lock (_gate)
            {
                pids = _gamePids;
            }

            if (pids.Length == 0)
            {
                return false;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return false;
            }

            _ = NativeMethods.GetWindowThreadProcessId(foreground, out var owner);
            return Array.IndexOf(pids, owner) >= 0;
        }
    }

    /// <summary>True while a game window exists.</summary>
    public bool GameIsRunning => _tracker.GameIsRunning;

    /// <summary>Starts polling, after one immediate scan.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_timer is not null)
        {
            return;
        }

        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(_pollMs));
    }

    /// <summary>Stops polling. The last known state is left as it was.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Adopts a new profile, on startup or on a <c>config.changed</c> frame. The process list and the
    /// interval both come from it.
    /// </summary>
    public void AdoptProfile(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_gate)
        {
            _processNames = [.. profile.Game.ProcessNames];
            _pollMs = profile.Game.WindowPollMs;
        }

        _timer?.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(_pollMs));
    }

    /// <summary>
    /// One scan, applied to the tracker. A slow scan never overlaps itself: the tick that arrives
    /// while one is running is dropped rather than queued.
    /// </summary>
    public void Poll()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var wasRunning = _tracker.GameIsRunning;
            _tracker.Observe(Scan());

            if (_tracker.GameIsRunning != wasRunning)
            {
                _log.Note(_tracker.GameIsRunning ? InputEvent.GameWindowFound : InputEvent.GameWindowLost);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private static bool IsExclusiveFullscreen() =>
        NativeMethods.SHQueryUserNotificationState(out var state) == 0
        && state == NativeMethods.QunsRunningD3DFullScreen;

    private static ScreenRect ClientRectOf(IntPtr window)
    {
        if (!NativeMethods.GetClientRect(window, out var rect))
        {
            return ScreenRect.Empty;
        }

        var origin = new NativeMethods.Point { X = rect.Left, Y = rect.Top };
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return ScreenRect.Empty;
        }

        return new ScreenRect(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private GameWindowScan? Scan()
    {
        string[] names;
        lock (_gate)
        {
            names = _processNames;
        }

        IntPtr window = IntPtr.Zero;
        var pids = new List<uint>();

        foreach (var name in names)
        {
            Process[] found;
            try
            {
                found = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var process in found)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
                    {
                        pids.Add((uint)process.Id);
                        if (window == IntPtr.Zero)
                        {
                            window = handle;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and the read. Not a game window.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        lock (_gate)
        {
            _gamePids = [.. pids];
        }

        if (window == IntPtr.Zero)
        {
            return null;
        }

        var foreground = NativeMethods.GetForegroundWindow() == window;
        var rect = NativeMethods.IsIconic(window) ? ScreenRect.Empty : ClientRectOf(window);
        var exclusive = foreground && IsExclusiveFullscreen();

        return new GameWindowScan(window, rect, foreground, exclusive);
    }
}
