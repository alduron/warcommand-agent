using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// Foreground gating, menu swallowing and the sink routing. Panic's exemption has its own file
/// beside the architecture tests.
/// </summary>
public class InputBridgeTests
{
    [Fact]
    public void Every_binding_but_panic_is_inert_when_the_game_is_not_foreground()
    {
        var harness = new Harness(gameForeground: false, gameRunning: true);

        foreach (var (action, chord) in harness.Bindings.All)
        {
            if (action == BindingAction.Panic || !chord.IsBound)
            {
                continue;
            }

            var dispatch = harness.Bridge.Handle(chord);

            Assert.False(dispatch.Dispatched);
            Assert.False(dispatch.Swallow);
            Assert.Equal(DispatchOutcome.GameNotForeground, dispatch.Outcome);
        }

        Assert.Empty(harness.Chords.Invoked);
        Assert.Equal(0, harness.Ptt.Downs);
    }

    [Fact]
    public void Every_binding_but_panic_is_inert_when_the_game_is_not_running()
    {
        var harness = new Harness(gameForeground: false, gameRunning: false);

        foreach (var (action, chord) in harness.Bindings.All)
        {
            if (action == BindingAction.Panic || !chord.IsBound)
            {
                continue;
            }

            Assert.Equal(DispatchOutcome.GameNotRunning, harness.Bridge.Handle(chord).Outcome);
        }
    }

    [Fact]
    public void Every_binding_fires_when_the_game_has_focus()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);

        foreach (var (action, chord) in harness.Bindings.All)
        {
            // Navigation is covered separately: it does nothing at all unless a hold is down, which
            // is the whole point of it living on the movement keys.
            if (action == BindingAction.Panic || BindingActions.IsNavigation(action) || !chord.IsBound)
            {
                continue;
            }

            Assert.True(harness.Bridge.Handle(chord).Dispatched, action.ToString());
        }

        // Two hold keys, one sink: voice and keyboard are two ways into the same menu.
        Assert.Equal(2, harness.Ptt.Downs);
        Assert.Contains(BindingAction.Board, harness.Chords.Invoked);
    }

    [Fact]
    public void A_navigation_key_is_inert_until_a_hold_key_is_down()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        var nav = new CountingNav();
        harness.Bridge.Connect(harness.Ptt, null, harness.Chords, null, nav);

        var up = harness.Bindings[BindingAction.NavUp];

        // W walks the player. Outside a hold it must reach the game untouched.
        var idle = harness.Bridge.Handle(up);
        Assert.False(idle.Dispatched);
        Assert.False(idle.Swallow);
        Assert.Equal(0, nav.Ups);

        harness.Bridge.Handle(harness.Bindings[BindingAction.Menu]);

        // Under a hold it drives the menu and is ALWAYS swallowed, or the player walks while
        // reading the board.
        var held = harness.Bridge.Handle(up);
        Assert.True(held.Dispatched);
        Assert.True(held.Swallow);
        Assert.Equal(1, nav.Ups);
    }

    private sealed class CountingNav : IMenuNavSink
    {
        public int Ups { get; private set; }

        public int Commits { get; private set; }

        public void Scroll(int notches)
        {
            if (notches < 0)
            {
                Ups++;
            }
        }

        public void Commit() => Commits++;

        public void Back()
        {
        }
    }

    [Fact]
    public void Ptt_down_and_up_reach_the_ptt_sink_and_nothing_else()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        var at = DateTimeOffset.UnixEpoch;

        harness.Bridge.Handle(BindingSet.SuggestedPtt, at);
        harness.Bridge.HandleUp(BindingSet.SuggestedPtt, at.AddMilliseconds(200));

        Assert.Equal(1, harness.Ptt.Downs);
        Assert.Equal(1, harness.Ptt.Ups);
        Assert.Empty(harness.Chords.Invoked);
    }

    [Fact]
    public void Ptt_passes_through_to_the_game_while_no_menu_is_open()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);

        var dispatch = harness.Bridge.Handle(BindingSet.SuggestedPtt);

        Assert.True(dispatch.Dispatched);
        Assert.False(dispatch.Swallow);
    }

    [Fact]
    public void A_menu_swallows_digits_and_nothing_else()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        harness.Menu.MenuIsOpen = true;
        harness.Bridge.Rearm();

        // Both rows. The numpad is where a hand goes to type an invite code.
        Assert.True(harness.Bridge.Handle(Chord.Bare("4")).Swallow);
        Assert.True(harness.Bridge.Handle(Chord.Bare("Numpad4")).Swallow);

        // The game keeps these. Escape closed the menu, which releasing the hold key already does,
        // and Backspace deleted a digit, which the back key does.
        Assert.False(harness.Bridge.Handle(Chord.Bare("Escape")).Swallow);
        Assert.False(harness.Bridge.Handle(Chord.Bare("Backspace")).Swallow);

        // The hold key never is. Opening the menu is additive, exactly like a modifier: swallowing
        // it meant the key that opens the menu could not be typed anywhere on the machine.
        var hold = harness.Bridge.Handle(BindingSet.SuggestedPtt);
        Assert.True(hold.Dispatched);
        Assert.False(hold.Swallow);

        // W would get somebody killed.
        var w = harness.Bridge.Handle(Chord.Bare("W"));
        Assert.False(w.Swallow);
        Assert.False(w.Dispatched);

        // Both fours reached the menu and nothing else did.
        Assert.Equal([4, 4], harness.MenuKeys.Digits);
        Assert.Equal(0, harness.MenuKeys.Escapes);
        Assert.Equal(0, harness.MenuKeys.Backspaces);
    }

    [Fact]
    public void Digits_are_not_armed_while_no_menu_is_open()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);

        Assert.False(harness.Bridge.Armed.IsArmed(0x34));

        harness.Menu.MenuIsOpen = true;
        harness.Bridge.Rearm();

        Assert.True(harness.Bridge.Armed.IsArmed(0x34));
    }

    [Fact]
    public void An_unbound_chord_reaches_no_sink()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);

        var dispatch = harness.Bridge.Handle(Chord.RightAlt("Z"));

        Assert.False(dispatch.Dispatched);
        Assert.Equal(DispatchOutcome.NotBound, dispatch.Outcome);
        Assert.Empty(harness.Chords.Invoked);
    }

    [Fact]
    public void The_game_keeps_its_own_escape_key_even_with_a_menu_open()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        harness.Menu.MenuIsOpen = true;
        harness.Bridge.Rearm();

        var dispatch = harness.Bridge.Handle(Chord.Bare("Escape"));

        // Escape discarded and closed, which is exactly what letting go of the hold key does. It
        // bought nothing and it took the game's own Escape for as long as the menu was open.
        Assert.False(dispatch.Swallow);
        Assert.Equal(0, harness.MenuKeys.Escapes);
    }

    [Fact]
    public void A_digit_reaches_the_menu_while_a_modifier_hold_key_is_down()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        harness.Menu.MenuIsOpen = true;

        // RightAlt is the default menu hold key, so every key pressed while it is held arrives
        // carrying the RightAlt modifier. Reading these off the whole chord meant holding the menu
        // key open and then pressing 1 did nothing at all.
        harness.Bridge.Handle(new Chord(BindingModifiers.RightAlt, KeyOf("1")));
        harness.Bridge.Handle(new Chord(BindingModifiers.RightAlt, KeyOf("Numpad2")));

        Assert.Equal([1, 2], harness.MenuKeys.Digits);
    }

    private static BindingKey KeyOf(string label) =>
        BindingKey.TryFromLabel(label, out var key) ? key : throw new InvalidOperationException(label);

    private sealed class Harness
    {
        internal Harness(bool gameForeground, bool gameRunning)
        {
            Bindings = BindingSet.Defaults();
            Bindings.Rebind(BindingAction.Ptt, BindingSet.SuggestedPtt);

            Panic = new PanicSwitch();
            foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
            {
                Panic.Register(subsystem, new CountingSuspendable());
            }

            Panic.Arm();

            Bridge = new InputBridge(Bindings, Panic, new FixedForegroundProbe(gameForeground, gameRunning));
            Bridge.Connect(Ptt, MenuKeys, Chords, Menu);
        }

        internal BindingSet Bindings { get; }

        internal PanicSwitch Panic { get; }

        internal InputBridge Bridge { get; }

        internal RecordingPtt Ptt { get; } = new();

        internal RecordingMenuKeys MenuKeys { get; } = new();

        internal RecordingChords Chords { get; } = new();

        internal ToggleableMenu Menu { get; } = new();
    }

    private sealed class RecordingPtt : IPttSink
    {
        internal int Downs { get; private set; }

        internal int Ups { get; private set; }

        public void PttDown(DateTimeOffset at) => Downs++;

        public void PttUp(DateTimeOffset at) => Ups++;
    }

    private sealed class RecordingMenuKeys : IMenuKeySink
    {
        internal List<int> Digits { get; } = [];

        internal int Escapes { get; private set; }

        internal int Backspaces { get; private set; }

        public void Digit(int digit) => Digits.Add(digit);

        public void Escape() => Escapes++;

        public void Backspace() => Backspaces++;
    }

    private sealed class RecordingChords : IChordSink
    {
        internal List<BindingAction> Invoked { get; } = [];

        public void Invoke(BindingAction action) => Invoked.Add(action);
    }

    private sealed class ToggleableMenu : IMenuGate
    {
        public bool MenuIsOpen { get; set; }
    }

    private sealed class CountingSuspendable : ISuspendable
    {
        public void Suspend()
        {
            // Counted by PanicSwitchTests, not here.
        }

        public void Resume()
        {
            // Counted by PanicSwitchTests, not here.
        }
    }
}
