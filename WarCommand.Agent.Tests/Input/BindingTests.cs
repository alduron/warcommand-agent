using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>The binding model: defaults, conflicts, rebinding and the honest gap about the game.</summary>
public class BindingTests
{
    [Fact]
    public void Defaults_bind_both_hold_keys()
    {
        var bindings = BindingSet.Defaults();

        // Both ship bound. Nothing on the overlay happens without one of them held, so an unbound
        // default is a product that does nothing until the user finds a settings page.
        Assert.True(bindings.PttChosen);
        Assert.Equal("V", bindings[BindingAction.Ptt].Label);
        Assert.Equal("CapsLock", bindings[BindingAction.Menu].Label);
        Assert.Equal("RightAlt+B", bindings[BindingAction.Board].Label);
        Assert.Equal("RightAlt+P", bindings[BindingAction.Panic].Label);
        Assert.Equal("Escape", bindings[BindingAction.Escape].Label);

        // Navigation ships on the movement keys, where the left hand already is.
        Assert.Equal("W", bindings[BindingAction.NavUp].Label);
        Assert.Equal("S", bindings[BindingAction.NavDown].Label);
        Assert.Equal("D", bindings[BindingAction.NavSelect].Label);
        Assert.Equal("A", bindings[BindingAction.NavBack].Label);
    }

    [Fact]
    public void The_whole_hotkey_surface_is_nine_bindings()
    {
        // Two hold keys, Escape, two RightAlt chords, and the four navigation keys. Navigation is
        // bindable because it sits on WASD and anybody who moves those in game must move these.
        Assert.Equal(9, BindingActions.All.Count);
        Assert.Equal(9, BindingSet.Defaults().All.Count());
        Assert.Equal(4, BindingActions.Navigation.Count);
    }

    [Fact]
    public void Navigation_keys_are_armed_only_while_a_hold_is_down()
    {
        // W walks. Arming it outside a hold would put every step the player takes through the hook,
        // and swallowing it would stop them moving.
        var bindings = BindingSet.Defaults();

        var idle = new bool[256];
        bindings.ArmIn(idle, holdActive: false);

        var held = new bool[256];
        bindings.ArmIn(held, holdActive: true);

        // BindingKey hides its code on purpose, so the difference between the two tables is the
        // observable: holding arms exactly the four navigation keys and nothing else.
        var extra = 0;
        for (var i = 0; i < idle.Length; i++)
        {
            if (held[i] && !idle[i])
            {
                extra++;
            }

            Assert.False(idle[i] && !held[i], "a hold must never disarm anything");
        }

        Assert.Equal(BindingActions.Navigation.Count, extra);
    }

    [Fact]
    public void A_conflict_with_another_warcommand_binding_is_refused_and_names_it()
    {
        var bindings = BindingSet.Defaults();

        var result = bindings.Rebind(BindingAction.Panic, Chord.RightAlt("B"));

        Assert.Equal(RebindStatus.RefusedConflict, result.Status);
        Assert.Equal(BindingAction.Board, result.ConflictsWith);
        Assert.Equal("Board: full, dim, off", BindingActions.Display(result.ConflictsWith));
        Assert.Equal("RightAlt+P", bindings[BindingAction.Panic].Label);
    }

    [Fact]
    public void Ptt_conflicts_are_refused_the_same_way()
    {
        var bindings = BindingSet.Defaults();
        bindings.Rebind(BindingAction.Ptt, Chord.Bare("Mouse5"));

        var result = bindings.Rebind(BindingAction.Board, Chord.Bare("Mouse5"));

        Assert.Equal(RebindStatus.RefusedConflict, result.Status);
        Assert.Equal(BindingAction.Ptt, result.ConflictsWith);
    }

    [Fact]
    public void Rebinding_an_action_to_the_chord_it_already_holds_is_accepted()
    {
        var bindings = BindingSet.Defaults();

        Assert.True(bindings.Rebind(BindingAction.Board, Chord.RightAlt("B")).Applied);
    }

    [Fact]
    public void A_game_conflict_is_never_claimed_to_have_been_checked()
    {
        Assert.False(BindingSet.CanDetectGameConflicts);
        Assert.False(RebindSession.ChecksAgainstTheGame);
        Assert.Contains("cannot see the game's keybinds", BindingSet.GameConflictNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebinding_captures_the_next_press()
    {
        var bindings = BindingSet.Defaults();
        var start = DateTimeOffset.UnixEpoch;
        var session = new RebindSession(bindings, BindingAction.Ptt, start);

        var outcome = session.Offer(Chord.Bare("Mouse4"), start.AddSeconds(1));

        Assert.Equal(RebindOutcome.Captured, outcome);
        Assert.Equal(RebindState.Captured, session.State);
        Assert.Equal("Mouse4", bindings[BindingAction.Ptt].Label);
    }

    [Fact]
    public void Rebinding_aborts_after_five_seconds()
    {
        var bindings = BindingSet.Defaults();
        var start = DateTimeOffset.UnixEpoch;
        var session = new RebindSession(bindings, BindingAction.Ptt, start);

        Assert.Equal(RebindState.Capturing, session.Tick(start.AddSeconds(4.9)));
        Assert.Equal(RebindState.Aborted, session.Tick(start.AddSeconds(5)));
        Assert.Equal(RebindOutcome.NotCapturing, session.Offer(Chord.Bare("Mouse4"), start.AddSeconds(5)));
        // The abort changed nothing: the shipped default is still in place.
        Assert.Equal("V", bindings[BindingAction.Ptt].Label);
    }

    [Fact]
    public void A_refused_capture_names_the_other_binding_and_keeps_capturing()
    {
        var bindings = BindingSet.Defaults();
        var start = DateTimeOffset.UnixEpoch;
        var session = new RebindSession(bindings, BindingAction.Ptt, start);

        Assert.Equal(RebindOutcome.RefusedConflict, session.Offer(Chord.RightAlt("P"), start));
        Assert.Equal(BindingAction.Panic, session.ConflictsWith);
        Assert.Equal(RebindState.Capturing, session.State);

        Assert.Equal(RebindOutcome.Captured, session.Offer(Chord.Bare("Mouse5"), start.AddSeconds(1)));
        Assert.Equal(BindingAction.None, session.ConflictsWith);
    }

    [Fact]
    public void Panic_is_rebindable()
    {
        var bindings = BindingSet.Defaults();

        Assert.True(bindings.Rebind(BindingAction.Panic, Chord.RightAlt("F9")).Applied);
        Assert.Equal("RightAlt+F9", bindings[BindingAction.Panic].Label);
    }

    [Fact]
    public void The_left_and_right_mouse_buttons_are_not_bindable()
    {
        Assert.False(BindingKey.TryFromVirtualKey(0x01, out _));
        Assert.False(BindingKey.TryFromVirtualKey(0x02, out _));
        Assert.True(BindingKey.TryFromMouseButton(MouseButton.Button5, out var mouse5));
        Assert.Equal("Mouse5", mouse5.Label);
    }

    [Fact]
    public void The_suggested_ptt_is_mouse5_for_anybody_who_wants_a_mouse_button()
    {
        Assert.Equal("Mouse5", BindingSet.SuggestedPtt.Label);
    }

    [Theory]
    [InlineData("Mouse5")]
    [InlineData("RightAlt+B")]
    [InlineData("RightAlt+P")]
    [InlineData("Escape")]
    public void A_chord_survives_the_round_trip_through_its_own_label(string label)
    {
        Assert.True(Chord.TryParse(label, out var chord));
        Assert.Equal(label, chord.Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+B")]
    [InlineData("RightAlt+NotAKey")]
    public void A_label_the_key_set_does_not_hold_parses_to_nothing(string? label)
    {
        Assert.False(Chord.TryParse(label, out var chord));
        Assert.False(chord.IsBound);
    }
}
