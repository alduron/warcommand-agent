using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Input;

/// <summary>Why a press did or did not reach a sink.</summary>
public enum DispatchOutcome
{
    /// <summary>It reached a sink.</summary>
    Dispatched = 0,

    /// <summary>WarCommand holds no binding for it, and no menu wanted it.</summary>
    NotBound,

    /// <summary>No process in <c>game.process_names</c> has a window.</summary>
    GameNotRunning,

    /// <summary>The game is running but is not the foreground window.</summary>
    GameNotForeground,

    /// <summary>Panic is engaged. Only Panic itself gets through.</summary>
    Suspended,

    /// <summary>The composition root has not connected a sink for this class of press.</summary>
    NoSink,
}

/// <summary>What the hook should do with a press.</summary>
public readonly record struct InputDispatch(bool Dispatched, bool Swallow, BindingAction Action, DispatchOutcome Outcome)
{
    internal static InputDispatch Ignored(DispatchOutcome outcome, BindingAction action = BindingAction.None) =>
        new(false, false, action, outcome);

    internal static InputDispatch Sent(BindingAction action, bool swallow) =>
        new(true, swallow, action, DispatchOutcome.Dispatched);
}

/// <summary>
/// The dispatcher between the Win32 hooks and the pure machines. It decides three things and nothing
/// else: which action a chord is, whether that action is live right now, and whether the press is
/// swallowed or passed to the game.
/// </summary>
/// <remarks>
/// Panic is exempt from every gate. It fires when Wardogs is not the foreground window, when Wardogs
/// is not running, and while the overlay is stuck drawing over the desktop, because those are the
/// moments a kill switch exists for. Every other binding is inert unless the game has focus.
/// </remarks>
public sealed class InputBridge
{
    private readonly BindingSet _bindings;
    private readonly PanicSwitch _panic;
    private readonly IForegroundProbe _foreground;
    private readonly IInputLog _log;

    private IPttSink? _ptt;
    private IMenuKeySink? _menu;
    private IChordSink? _chords;
    private IMenuGate? _menuGate;

    /// <summary>The composition root's constructor.</summary>
    public InputBridge(BindingSet bindings, PanicSwitch panic, IForegroundProbe foreground, IInputLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(panic);
        ArgumentNullException.ThrowIfNull(foreground);

        _bindings = bindings;
        _panic = panic;
        _foreground = foreground;
        _log = log ?? NullInputLog.Instance;

        _bindings.Changed += (_, _) => Rearm();
        _panic.Toggled += (_, _) => Rearm();
        Armed = ArmedKeys.Build(_bindings, _panic.IsSuspended, MenuIsOpen);
    }

    /// <summary>
    /// The invariant-test and diagnostic constructor named in <c>docs/design/14-testing.md</c>. The
    /// default chord set plus the suggested Mouse5 push-to-talk, so the gate is exercised against a
    /// binding that exists. Nothing ships with a PTT chosen this way.
    /// </summary>
    public InputBridge(bool gameForeground, bool gameRunning)
        : this(SeededBindings(), ArmedNoOpPanic(), new FixedForegroundProbe(gameForeground, gameRunning))
    {
    }

    /// <summary>Raised when the arming table changes, so the hooks can pick up the new one.</summary>
    public event EventHandler? ArmedChanged;

    /// <summary>The hook's first test. Anything not in here returns immediately.</summary>
    public ArmedKeys Armed { get; private set; }

    /// <summary>The chords in force.</summary>
    public BindingSet Bindings => _bindings;

    /// <summary>The one Panic signal.</summary>
    public PanicSwitch Panic => _panic;

    /// <summary>True while a WarCommand menu or panel owns the digits, Escape and Backspace.</summary>
    public bool MenuIsOpen => _menuGate?.MenuIsOpen ?? false;

    /// <summary>Wires the pure machines. Any of them may be null while a subsystem is off.</summary>
    public void Connect(IPttSink? ptt, IMenuKeySink? menu, IChordSink? chords, IMenuGate? menuGate)
    {
        _ptt = ptt;
        _menu = menu;
        _chords = chords;
        _menuGate = menuGate;
        Rearm();
    }

    /// <summary>Rebuilds the arming table. Call after the menu opens or closes.</summary>
    public void Rearm()
    {
        Armed = ArmedKeys.Build(_bindings, _panic.IsSuspended, MenuIsOpen);
        ArmedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A press, timestamped by the caller.</summary>
    public InputDispatch Handle(Chord chord) => Handle(chord, DateTimeOffset.UtcNow);

    /// <summary>A press.</summary>
    public InputDispatch Handle(Chord chord, DateTimeOffset at)
    {
        var action = _bindings.Resolve(chord);

        // Panic first, and gated by nothing. A kill switch that only works while the thing you are
        // killing is on top is not a kill switch.
        if (action == BindingAction.Panic)
        {
            _panic.Toggle();
            return InputDispatch.Sent(BindingAction.Panic, swallow: true);
        }

        if (_panic.IsSuspended)
        {
            return InputDispatch.Ignored(DispatchOutcome.Suspended, action);
        }

        if (!_foreground.GameIsRunning)
        {
            return InputDispatch.Ignored(DispatchOutcome.GameNotRunning, action);
        }

        if (!_foreground.GameIsForeground)
        {
            return InputDispatch.Ignored(DispatchOutcome.GameNotForeground, action);
        }

        if (action == BindingAction.Ptt)
        {
            if (_ptt is null)
            {
                return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
            }

            _ptt.PttDown(at);
            return InputDispatch.Sent(action, swallow: MenuIsOpen);
        }

        if (MenuIsOpen && TryMenuKey(chord, action))
        {
            return InputDispatch.Sent(action, swallow: true);
        }

        if (action == BindingAction.None)
        {
            return InputDispatch.Ignored(DispatchOutcome.NotBound);
        }

        if (_chords is null)
        {
            return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
        }

        _chords.Invoke(action);
        return InputDispatch.Sent(action, swallow: true);
    }

    /// <summary>A release. Only push-to-talk cares.</summary>
    public InputDispatch HandleUp(Chord chord) => HandleUp(chord, DateTimeOffset.UtcNow);

    /// <summary>A release.</summary>
    public InputDispatch HandleUp(Chord chord, DateTimeOffset at)
    {
        var action = _bindings.Resolve(chord);
        if (action != BindingAction.Ptt)
        {
            return InputDispatch.Ignored(DispatchOutcome.NotBound, action);
        }

        if (_panic.IsSuspended)
        {
            return InputDispatch.Ignored(DispatchOutcome.Suspended, action);
        }

        if (_ptt is null)
        {
            return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
        }

        // A release is delivered whatever the foreground is. Swallowing the key-down and dropping the
        // key-up leaves the machine holding a key nobody is pressing.
        _ptt.PttUp(at);
        return InputDispatch.Sent(action, swallow: MenuIsOpen);
    }

    /// <summary>Opens a rebind capture with a five second abort.</summary>
    public RebindSession BeginRebind(BindingAction action, DateTimeOffset startedAt)
    {
        _log.Note(InputEvent.RebindStarted);
        return new RebindSession(_bindings, action, startedAt);
    }

    private bool TryMenuKey(Chord chord, BindingAction action)
    {
        if (_menu is null || chord.Modifiers != BindingModifiers.None)
        {
            return false;
        }

        if (chord.TryDigit(out var digit))
        {
            _menu.Digit(digit);
            return true;
        }

        if (action == BindingAction.Escape || chord == Chord.Bare("Escape"))
        {
            _menu.Escape();
            return true;
        }

        if (chord == Chord.Bare("Backspace"))
        {
            _menu.Backspace();
            return true;
        }

        return false;
    }

    private static BindingSet SeededBindings()
    {
        var bindings = BindingSet.Defaults();
        bindings.Rebind(BindingAction.Ptt, BindingSet.SuggestedPtt);
        return bindings;
    }

    private static PanicSwitch ArmedNoOpPanic()
    {
        var panic = new PanicSwitch();
        foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
        {
            panic.Register(subsystem, NoOpSuspendable.Instance);
        }

        panic.Arm();
        return panic;
    }

    private sealed class NoOpSuspendable : ISuspendable
    {
        internal static NoOpSuspendable Instance { get; } = new();

        public void Suspend()
        {
            // Nothing to suspend.
        }

        public void Resume()
        {
            // Nothing to resume.
        }
    }
}

/// <summary>A foreground answer that does not move. The test and first-run seam.</summary>
public sealed class FixedForegroundProbe : IForegroundProbe
{
    /// <summary>Creates a probe that always gives these answers.</summary>
    public FixedForegroundProbe(bool gameForeground, bool gameRunning)
    {
        GameIsForeground = gameForeground;
        GameIsRunning = gameRunning;
    }

    /// <inheritdoc />
    public bool GameIsForeground { get; set; }

    /// <inheritdoc />
    public bool GameIsRunning { get; set; }
}
