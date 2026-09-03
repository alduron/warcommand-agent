using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Input.Hooks;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// The hook's first act. Return immediately for any key that is not a registered hotkey: no
/// processing, no allocation, nothing recorded. Evaluate is the whole decision and holds no Win32,
/// so it is testable without installing anything.
/// </summary>
public class HookGateTests
{
    [Fact]
    public void An_unregistered_key_is_passed_straight_through()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        var hook = new LowLevelKeyboardHook(bridge);

        // W, the key that would get somebody killed.
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0x57, KeyTransition.Down));
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0x57, KeyTransition.Up));
        Assert.False(bridge.Armed.IsArmed(0x57));
    }

    [Fact]
    public void The_unregistered_path_allocates_nothing()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        var hook = new LowLevelKeyboardHook(bridge);

        for (var i = 0; i < 10_000; i++)
        {
            hook.Evaluate(0x57, KeyTransition.Down);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            hook.Evaluate(0x57, KeyTransition.Down);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_registered_chord_is_swallowed_and_a_modifier_never_is()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        bridge.Connect(null, null, new NullChords(), null);
        var hook = new LowLevelKeyboardHook(bridge);

        // RightAlt is armed because the chord set needs it, and it always reaches the game.
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0xA5, KeyTransition.Down));
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(0x42, KeyTransition.Down));
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0xA5, KeyTransition.Up));

        // The same key with no modifier held is not a binding.
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0x42, KeyTransition.Down));
    }

    [Fact]
    public void The_menu_key_fires_even_though_it_is_a_modifier()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        bridge.Bindings.Rebind(BindingAction.Menu, Chord.Bare("RightAlt"));
        var ptt = new CountingPtt();
        bridge.Connect(ptt, null, new NullChords(), null);
        bridge.Rearm();
        var hook = new LowLevelKeyboardHook(bridge);

        // A modifier is a legal hold key. The hook used to record it as a modifier and return
        // before dispatching anything, so the key did nothing at all while a plain letter bound to
        // the same action worked.
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0xA5, KeyTransition.Down));
        Assert.Equal(1, ptt.Downs);

        // And it still reaches the game, which is the reason a non-toggle hold key is never
        // swallowed.
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0xA5, KeyTransition.Up));
    }

    [Fact]
    public void The_capslock_hold_key_is_swallowed_so_caps_never_toggles()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        var ptt = new CountingPtt();
        bridge.Connect(ptt, null, new NullChords(), null);
        var hook = new LowLevelKeyboardHook(bridge);

        // CapsLock is the default hold key, and BOTH edges have to be eaten. Swallowing only the
        // key-down still left caps latched on, which locks the user into capitals everywhere until
        // they press it again with the agent stopped.
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(0x14, KeyTransition.Down));
        Assert.Equal(1, ptt.Downs);
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(0x14, KeyTransition.Up));
    }

    [Fact]
    public void Nothing_but_panic_is_armed_once_panic_engages()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);

        Assert.True(bridge.Armed.IsArmed(0x42));

        bridge.Handle(Chord.RightAlt("P"));

        Assert.False(bridge.Armed.IsArmed(0x42));
        Assert.True(bridge.Armed.IsArmed(0x50));
        Assert.True(bridge.Armed.IsArmed(0xA5));
    }

    [Fact]
    public void The_mouse_hook_ignores_a_button_that_is_not_bound()
    {
        var bridge = Bridge(gameForeground: true, gameRunning: true);
        var ptt = new CountingPtt();
        bridge.Connect(ptt, null, null, null);
        var hook = new LowLevelMouseHook(bridge);

        // Middle button, unbound in the default set. Not armed, so nothing runs.
        Assert.False(bridge.Armed.IsArmed(0x04));
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0x04, KeyTransition.Down));
        Assert.Equal(0, ptt.Downs);

        // Mouse5 is the seeded push-to-talk: it dispatches, and still reaches the game, because
        // swallowing it would take the key out from under a voice client sharing it.
        Assert.True(bridge.Armed.IsArmed(0x06));
        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(0x06, KeyTransition.Down));
        Assert.Equal(1, ptt.Downs);
    }

    private static InputBridge Bridge(bool gameForeground, bool gameRunning) =>
        new(gameForeground, gameRunning);

    private sealed class NullChords : IChordSink
    {
        public void Invoke(BindingAction action)
        {
            // The verdict is what is under test.
        }
    }

    private sealed class CountingPtt : IPttSink
    {
        internal int Downs { get; private set; }

        public void PttDown(DateTimeOffset at) => Downs++;

        public void PttUp(DateTimeOffset at)
        {
            // Not under test here.
        }
    }
}
