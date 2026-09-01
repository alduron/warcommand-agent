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
            if (action == BindingAction.Panic || !chord.IsBound)
            {
                continue;
            }

            Assert.True(harness.Bridge.Handle(chord).Dispatched, action.ToString());
        }

        Assert.Equal(1, harness.Ptt.Downs);
        Assert.Contains(BindingAction.ToggleBoard, harness.Chords.Invoked);
        Assert.Contains(BindingAction.Escape, harness.Chords.Invoked);
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
    public void A_menu_swallows_only_digits_escape_backspace_and_ptt()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);
        harness.Menu.MenuIsOpen = true;
        harness.Bridge.Rearm();

        Assert.True(harness.Bridge.Handle(Chord.Bare("4")).Swallow);
        Assert.True(harness.Bridge.Handle(Chord.Bare("Escape")).Swallow);
        Assert.True(harness.Bridge.Handle(Chord.Bare("Backspace")).Swallow);
        Assert.True(harness.Bridge.Handle(BindingSet.SuggestedPtt).Swallow);

        // W would get somebody killed.
        var w = harness.Bridge.Handle(Chord.Bare("W"));
        Assert.False(w.Swallow);
        Assert.False(w.Dispatched);

        Assert.Equal([4], harness.MenuKeys.Digits);
        Assert.Equal(1, harness.MenuKeys.Escapes);
        Assert.Equal(1, harness.MenuKeys.Backspaces);
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
    public void Escape_routes_to_the_menu_while_one_is_open_and_to_the_chord_sink_otherwise()
    {
        var harness = new Harness(gameForeground: true, gameRunning: true);

        harness.Bridge.Handle(Chord.Bare("Escape"));
        Assert.Contains(BindingAction.Escape, harness.Chords.Invoked);
        Assert.Equal(0, harness.MenuKeys.Escapes);

        harness.Menu.MenuIsOpen = true;
        harness.Bridge.Rearm();
        harness.Bridge.Handle(Chord.Bare("Escape"));
        Assert.Equal(1, harness.MenuKeys.Escapes);
    }

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
