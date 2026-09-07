using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Input.Devices;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// The second binding row. A pilot on a HOTAS never reaches the keyboard, so every action carries a
/// controller button beside its key and the two are set, stored and resolved apart.
/// </summary>
public sealed class HotasBindingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DeviceButton Trigger = new("VID_231D+PID_0126", 7);
    private static readonly DeviceButton Pinky = new("VID_231D+PID_0126", 12);

    [Fact]
    public void A_stick_button_and_a_key_are_two_rows_of_the_same_action()
    {
        var bindings = BindingSet.Defaults();

        Assert.True(bindings.RebindSecondary(BindingAction.Menu, Trigger).Applied);

        // The key it shipped with is untouched. Binding the stick is not giving up the keyboard.
        Assert.Equal("CapsLock", bindings[BindingAction.Menu].Label);
        Assert.Equal(Trigger, bindings.Secondary(BindingAction.Menu));
        Assert.Equal(BindingAction.Menu, bindings.ResolveSecondary(Trigger));

        // The two tables never see each other. A key resolves on the chord side only, and a button
        // nobody bound resolves nowhere at all.
        Assert.Equal(BindingAction.Menu, bindings.Resolve(Chord.Bare("CapsLock")));
        Assert.Equal(BindingAction.None, bindings.ResolveSecondary(Pinky));
    }

    [Fact]
    public void One_button_cannot_hold_two_actions()
    {
        var bindings = BindingSet.Defaults();
        Assert.True(bindings.RebindSecondary(BindingAction.Ptt, Trigger).Applied);

        var refused = bindings.RebindSecondary(BindingAction.Menu, Trigger);
        Assert.Equal(RebindStatus.RefusedConflict, refused.Status);
        Assert.Equal(BindingAction.Ptt, refused.ConflictsWith);
        Assert.False(bindings.Secondary(BindingAction.Menu).IsBound);
    }

    [Fact]
    public void A_hotas_capture_ignores_the_keyboard_and_a_key_capture_ignores_the_stick()
    {
        var bindings = BindingSet.Defaults();

        // The row exists for somebody whose hands are not on the keyboard. A hand resting on one
        // used to fill whichever row was open.
        var hotas = new RebindSession(bindings, BindingAction.NavUp, T0, BindingSlot.Secondary);
        Assert.Equal(RebindOutcome.Ignored, hotas.Offer(Chord.Bare("K"), T0));
        Assert.Equal(RebindOutcome.Captured, hotas.Offer(Pinky, T0));
        Assert.Equal(Pinky, bindings.Secondary(BindingAction.NavUp));

        var keyboard = new RebindSession(bindings, BindingAction.NavDown, T0);
        Assert.Equal(RebindOutcome.Ignored, keyboard.Offer(Trigger, T0));
        Assert.Equal(RebindOutcome.Captured, keyboard.Offer(Chord.Bare("K"), T0));
    }

    [Fact]
    public void A_button_is_dispatched_like_its_key_and_is_never_swallowed()
    {
        var bindings = BindingSet.Defaults();
        Assert.True(bindings.RebindSecondary(BindingAction.Menu, Trigger).Applied);
        Assert.True(bindings.RebindSecondary(BindingAction.NavDown, Pinky).Applied);

        var bridge = new InputBridge(bindings, Panic(), new FixedForegroundProbe(true, true));
        var nav = new CountingNav();
        bridge.Connect(new NoOpPtt(), menu: null, chords: null, menuGate: null, menuNav: nav);

        // Navigation is armed only under a hold, on the stick exactly as on the keyboard.
        Assert.Equal(DispatchOutcome.NotBound, bridge.HandleDevice(Pinky, T0).Outcome);

        var hold = bridge.HandleDevice(Trigger, T0);
        Assert.True(hold.Dispatched);
        Assert.Equal(BindingAction.Menu, hold.Action);

        // Never swallowed. A HID report reaches the game whatever we do with it, so claiming a
        // swallow here would be a promise the input layer cannot keep.
        Assert.False(hold.Swallow);

        var down = bridge.HandleDevice(Pinky, T0);
        Assert.True(down.Dispatched);
        Assert.False(down.Swallow);
        Assert.Equal(1, nav.Scrolled);

        // And the hold ends on the button coming up, which is what closes the microphone.
        Assert.True(bridge.HandleDeviceUp(Trigger, T0).Dispatched);
        Assert.Equal(DispatchOutcome.NotBound, bridge.HandleDevice(Pinky, T0).Outcome);
    }

    [Fact]
    public void Panic_answers_a_stick_button_with_the_game_closed()
    {
        var bindings = BindingSet.Defaults();
        Assert.True(bindings.RebindSecondary(BindingAction.Panic, Trigger).Applied);

        var bridge = new InputBridge(bindings, Panic(), new FixedForegroundProbe(false, false));

        // Binding rule 7 does not care which surface the press came from.
        Assert.True(bridge.HandleDevice(Trigger, T0).Dispatched);
        Assert.True(bridge.Panic.IsSuspended);

        // And the same button releases it, which is why the stick reader is not a panic subsystem.
        Assert.True(bridge.HandleDevice(Trigger, T0).Dispatched);
        Assert.False(bridge.Panic.IsSuspended);
    }

    [Fact]
    public void A_button_survives_the_round_trip_through_settings()
    {
        Assert.True(DeviceButton.TryParse(Trigger.Label, out var read));
        Assert.Equal(Trigger, read);

        // A chord label must never parse as a button, or a stored keyboard binding would come back
        // as a stick one on the row nobody bound.
        Assert.False(DeviceButton.TryParse("RightAlt+B", out _));
        Assert.False(DeviceButton.TryParse(null, out _));
        Assert.False(DeviceButton.TryParse("hid:VID_231D+PID_0126:0", out _));
    }

    [Fact]
    public void An_unplugged_stick_still_shows_what_it_holds()
    {
        var devices = new[] { new DeviceInfo("VID_231D+PID_0126", "WARBRD", 128) };

        Assert.Equal("WARBRD B7", Trigger.Display(devices));

        // Nothing plugged in is not the same as nothing bound, and hiding the binding reads as the
        // setting having been lost.
        Assert.Equal("VID_231D+PID_0126 B7", Trigger.Display([]));
        Assert.Equal("Not set", DeviceButton.None.Display(devices));
    }

    private static PanicSwitch Panic()
    {
        var panic = new PanicSwitch();
        foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
        {
            panic.Register(subsystem, new NoOpSuspendable());
        }

        panic.Arm();
        return panic;
    }

    private sealed class NoOpSuspendable : ISuspendable
    {
        public void Suspend()
        {
        }

        public void Resume()
        {
        }
    }

    private sealed class NoOpPtt : IPttSink
    {
        public void PttDown(DateTimeOffset at)
        {
        }

        public void PttUp(DateTimeOffset at)
        {
        }
    }

    private sealed class CountingNav : IMenuNavSink
    {
        internal int Scrolled { get; private set; }

        public void Scroll(int notches) => Scrolled += notches;

        public void Commit()
        {
        }

        public void Back()
        {
        }

        public void Tools()
        {
        }

        public void Cycle(int step)
        {
        }
    }
}
