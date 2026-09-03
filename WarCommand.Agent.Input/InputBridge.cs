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
    private IMenuNavSink? _menuNav;
    private DateTimeOffset? _holdSince;

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
        Armed = ArmedKeys.Build(_bindings, _panic.IsSuspended, MenuIsOpen, _holdSince is not null);
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

    /// <summary>
    /// Which hold key produced the edge the sink is being told about: Ptt or Menu.
    /// </summary>
    /// <remarks>
    /// Both land on one <see cref="IPttSink"/>, and only one of them may open a microphone.
    /// </remarks>
    public BindingAction LastHoldAction { get; private set; } = BindingAction.None;

    /// <summary>
    /// True while a hold key is genuinely down, the game is foreground and Panic is off. Only then
    /// may the wheel and the left and right buttons be taken from the game.
    /// </summary>
    public bool MouseNavActive => NavRefusal() is null;

    /// <summary>
    /// A diagnostic hook for the navigation path, off by default and deliberately unwired.
    /// </summary>
    /// <remarks>
    /// NOTHING SLOW MAY BE ATTACHED HERE. It is called on the hook thread, once per key, inside the
    /// low-level hook callback. Pointing it at the file log cost two synchronous disk writes per
    /// keystroke and Windows started dropping events, which reads as the navigation keys lagging.
    /// Attach it to an in-memory sink for a session, never to anything that touches IO.
    /// </remarks>
    public Action<string>? NavTrace { get; set; }

    /// <summary>Why the mouse may not drive the menu right now, or null when it may.</summary>
    private string? NavRefusal()
    {
        if (_holdSince is not { } since)
        {
            return "no hold key down";
        }

        if (_panic.IsSuspended)
        {
            return "panic";
        }

        if (!_foreground.GameIsForeground)
        {
            return "game not foreground";
        }

        // Deliberately no time limit. A hold ends when the key comes up, Panic fires, or the game
        // stops being foreground, and at no other moment: a hold that expired under the user's
        // finger took the menu away mid-action, which is worse than any state it was guarding.
        _ = since;
        return null;
    }

    /// <summary>Wires the pure machines. Any of them may be null while a subsystem is off.</summary>
    public void Connect(
        IPttSink? ptt,
        IMenuKeySink? menu,
        IChordSink? chords,
        IMenuGate? menuGate,
        IMenuNavSink? menuNav = null)
    {
        _ptt = ptt;
        _menu = menu;
        _chords = chords;
        _menuGate = menuGate;
        _menuNav = menuNav;
        Rearm();
    }

    /// <summary>
    /// Ends the hold when no key-up will arrive. Called on Panic and on losing the game window,
    /// both of which end the interaction whether or not the button is physically still down.
    /// </summary>
    /// <remarks>
    /// The sink is told, exactly as a real key-up tells it. Clearing the state silently left the
    /// microphone open with nothing to close it: VoiceDriver stayed holding, every later press
    /// early-returned, and the menu's orphan guard could never fire because the hold still read as
    /// down. A hold that ends must end everywhere.
    /// </remarks>
    public void ReleaseHold() => ReleaseHold(DateTimeOffset.UtcNow);

    /// <summary>Ends the hold, timestamped by the caller.</summary>
    public void ReleaseHold(DateTimeOffset at)
    {
        var wasHolding = _holdSince is not null;
        _holdSince = null;
        Rearm();

        if (wasHolding)
        {
            _ptt?.PttUp(at);
        }
    }

    /// <summary>
    /// The watchdog, driven by the same tick that redraws the board. Releases a hold that has lost
    /// the game window, so the navigation keys cannot stay armed after an alt-tab.
    /// </summary>
    /// <remarks>
    /// It never releases on elapsed time. A hold ends when the user ends it.
    /// </remarks>
    public void EnforceHoldLimit()
    {
        if (_holdSince is null)
        {
            return;
        }

        if (NavRefusal() is not null)
        {
            ReleaseHold();
        }
    }

    /// <summary>Rebuilds the arming table. Call after the menu opens or closes.</summary>
    public void Rearm()
    {
        Armed = ArmedKeys.Build(_bindings, _panic.IsSuspended, MenuIsOpen, _holdSince is not null);
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

        // Navigation, armed only while a hold is down and ALWAYS swallowed. This is the whole
        // reason the control surface moved off the mouse: the keyboard hook's swallow is honoured
        // by the game and the mouse hook's is not. See Insight_WardogsReadsRawInputSoHooksCannotBlockIt.
        if (BindingActions.IsNavigation(action))
        {
            if (_holdSince is null)
            {
                return InputDispatch.Ignored(DispatchOutcome.NotBound, action);
            }

            if (_menuNav is null)
            {
                return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
            }

            switch (action)
            {
                case BindingAction.NavUp:
                    _menuNav.Scroll(-1);
                    break;
                case BindingAction.NavDown:
                    _menuNav.Scroll(1);
                    break;
                case BindingAction.NavSelect:
                    _menuNav.Commit();
                    break;
                default:
                    _menuNav.Back();
                    break;
            }

            return InputDispatch.Sent(action, swallow: true);
        }

        // Two hold keys. Menu opens the overlay's keyboard surface; PTT is voice. Either one
        // opens the menu, because speaking and pressing digits are two ways through the same tree,
        // and neither does anything at all once released.
        if (action is BindingAction.Ptt or BindingAction.Menu)
        {
            if (_ptt is null)
            {
                return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
            }

            // Never swallowed. Opening the menu is additive: the key still reaches whatever else
            // wants it, exactly as a modifier does. Swallowing it meant the menu opened on the V
            // key-down and then ate the V, so the letter could not be typed anywhere on the
            // machine while the agent ran.
            LastHoldAction = action;
            _holdSince = at;

            // WASD is hooked only while the hold is down, so the table has to be rebuilt on both
            // edges, synchronously, before the next key lands.
            Rearm();

            _ptt.PttDown(at);

            // A toggle key is swallowed; every other hold key is not. Holding CapsLock must not
            // leave caps on, and it carries no character anybody could want typed. A letter or a
            // mouse button still passes through, because the key belongs to the user's machine too.
            return InputDispatch.Sent(action, swallow: IsStatefulToggle(chord));
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
        if (action is not (BindingAction.Ptt or BindingAction.Menu))
        {
            return InputDispatch.Ignored(DispatchOutcome.NotBound, action);
        }

        if (_ptt is null)
        {
            _holdSince = null;
            Rearm();
            return InputDispatch.Ignored(DispatchOutcome.NoSink, action);
        }

        // A release is delivered whatever the foreground is, and under Panic too. Swallowing the
        // key-down and dropping the key-up leaves the machine holding a key nobody is pressing:
        // Panic used to return here, so panicking mid-hold left the microphone open with no edge
        // left to close it. Never swallowed, for the same reason the key-down is not: the hold key
        // belongs to the user's machine too.
        LastHoldAction = action;
        _holdSince = null;
        Rearm();
        _ptt.PttUp(at);

        // BOTH edges, or the toggle still fires. Swallowing only the key-down left CapsLock latched
        // on after every hold, which locks the user into capitals until they press it again with
        // the agent stopped.
        return InputDispatch.Sent(action, swallow: IsStatefulToggle(chord));
    }

    /// <summary>Opens a rebind capture with a five second abort.</summary>
    public RebindSession BeginRebind(BindingAction action, DateTimeOffset startedAt)
    {
        _log.Note(InputEvent.RebindStarted);
        return new RebindSession(_bindings, action, startedAt);
    }

    /// <summary>
    /// True for a key whose press changes machine state rather than typing something: CapsLock,
    /// NumLock, ScrollLock. Held as a hold key, these must be swallowed or the state flips.
    /// </summary>
    private static bool IsStatefulToggle(Chord chord)
    {
        foreach (var label in (string[])["CapsLock", "NumLock", "ScrollLock"])
        {
            if (BindingKey.TryFromLabel(label, out var key) && chord.Key.Equals(key))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMenuKey(Chord chord, BindingAction action)
    {
        // Modifiers are ignored here on purpose. The hold key that opened the menu may itself be a
        // modifier, RightAlt by default, so every digit pressed while it is down arrives carrying
        // RightAlt. Demanding a bare chord meant the menu could never be driven by its own key.
        if (_menu is null)
        {
            return false;
        }

        // Keyed on the KEY, never the whole chord. The hold key may be a modifier, so while it is
        // down every one of these arrives as the hold key plus the digit.
        // Chord.TryDigit refuses outright once any modifier is set, which is correct for deciding
        // whether a BINDING is a bare digit and wrong for reading a key the menu is waiting on.
        if (chord.Key.TryDigit(out var digit))
        {
            _menu.Digit(digit);
            return true;
        }

        // Escape and Backspace are deliberately absent. Escape discarded and closed, which is what
        // letting go of the hold key already does, so it bought nothing and took the game's own
        // Escape key for as long as the menu was open. Backspace deleted one typed digit, which the
        // back key does, and it is out of reach of a hand holding CapsLock anyway.
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
