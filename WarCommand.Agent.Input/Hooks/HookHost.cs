namespace WarCommand.Agent.Input.Hooks;

/// <summary>
/// Owns both low-level hooks and the thread that pumps messages for them. Windows delivers a
/// WH_KEYBOARD_LL callback only to a thread with a message loop, so the hooks get their own.
/// </summary>
/// <remarks>
/// Registered with <see cref="PanicSwitch"/> as <see cref="PanicSubsystem.Hotkeys"/>. Suspending
/// narrows the arming table to Panic alone, which is the hook suspended as far as it can be and still
/// hear the press that resumes it; a hook that was removed outright could not be pressed again.
/// </remarks>
public sealed class HookHost : IDisposable, ISuspendable
{
    private readonly InputBridge _bridge;
    private readonly IInputLog _log;
    private readonly LowLevelKeyboardHook _keyboard;
    private readonly LowLevelMouseHook _mouse;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _pump;
    private uint _pumpThreadId;
    private Exception? _startFailure;

    /// <summary>Creates the host. Nothing is hooked until <see cref="Start"/> is called.</summary>
    public HookHost(InputBridge bridge, IInputLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        _log = log ?? NullInputLog.Instance;
        _keyboard = new LowLevelKeyboardHook(bridge, _log);
        _mouse = new LowLevelMouseHook(bridge, _log);
    }

    /// <summary>True while the pump thread is running with both hooks installed.</summary>
    public bool IsRunning => _pump is not null && _keyboard.IsInstalled;

    /// <summary>Starts the pump thread and installs both hooks on it.</summary>
    public void Start()
    {
        if (_pump is not null)
        {
            return;
        }

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "WarCommand input hooks",
        };

        _pump.Start();
        _ready.Wait();

        if (_startFailure is not null)
        {
            var failure = _startFailure;
            _startFailure = null;
            _pump = null;
            throw new InvalidOperationException("the input hooks could not be installed", failure);
        }

        _bridge.Rearm();
        _log.Note(InputEvent.HotkeysEnabled);
    }

    /// <summary>Removes both hooks and stops the pump thread.</summary>
    public void Stop()
    {
        var pump = _pump;
        if (pump is null)
        {
            return;
        }

        NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        pump.Join(TimeSpan.FromSeconds(2));
        _pump = null;
        _log.Note(InputEvent.HotkeysDisabled);
    }

    /// <summary>Narrows the arming table to Panic alone and forgets any held modifier.</summary>
    public void Suspend()
    {
        _keyboard.ForgetModifiers();
        _bridge.Rearm();
    }

    /// <summary>Restores the full arming table.</summary>
    public void Resume()
    {
        _keyboard.ForgetModifiers();
        _bridge.Rearm();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _keyboard.Dispose();
        _mouse.Dispose();
        _ready.Dispose();
    }

    private void Pump()
    {
        _pumpThreadId = NativeMethods.GetCurrentThreadId();

        try
        {
            _keyboard.Install();
            _mouse.Install();
        }
        catch (InvalidOperationException ex)
        {
            _startFailure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(in message);
            NativeMethods.DispatchMessage(in message);
        }

        _mouse.Uninstall();
        _keyboard.Uninstall();
    }
}
