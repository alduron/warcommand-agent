using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>The binding model: defaults, conflicts, rebinding and the honest gap about the game.</summary>
public class BindingTests
{
    [Fact]
    public void Defaults_are_the_right_alt_chord_set_with_no_ptt()
    {
        var bindings = BindingSet.Defaults();

        Assert.False(bindings.PttChosen);
        Assert.Equal("RightAlt+B", bindings[BindingAction.ToggleBoard].Label);
        Assert.Equal("RightAlt+O", bindings[BindingAction.CycleOpacity].Label);
        Assert.Equal("RightAlt+D", bindings[BindingAction.DeploymentPicker].Label);
        Assert.Equal("RightAlt+R", bindings[BindingAction.Roles].Label);
        Assert.Equal("RightAlt+M", bindings[BindingAction.Participants].Label);
        Assert.Equal("RightAlt+G", bindings[BindingAction.GunPosition].Label);
        Assert.Equal("RightAlt+L", bindings[BindingAction.LinkAccount].Label);
        Assert.Equal("RightAlt+C", bindings[BindingAction.CopyCoordinate].Label);
        Assert.Equal("RightAlt+H", bindings[BindingAction.Help].Label);
        Assert.Equal("RightAlt+P", bindings[BindingAction.Panic].Label);
        Assert.Equal("Escape", bindings[BindingAction.Escape].Label);
    }

    [Fact]
    public void A_conflict_with_another_warcommand_binding_is_refused_and_names_it()
    {
        var bindings = BindingSet.Defaults();

        var result = bindings.Rebind(BindingAction.Roles, Chord.RightAlt("B"));

        Assert.Equal(RebindStatus.RefusedConflict, result.Status);
        Assert.Equal(BindingAction.ToggleBoard, result.ConflictsWith);
        Assert.Equal("Show or hide the board", BindingActions.Display(result.ConflictsWith));
        Assert.Equal("RightAlt+R", bindings[BindingAction.Roles].Label);
    }

    [Fact]
    public void Ptt_conflicts_are_refused_the_same_way()
    {
        var bindings = BindingSet.Defaults();
        bindings.Rebind(BindingAction.Ptt, Chord.Bare("Mouse5"));

        var result = bindings.Rebind(BindingAction.GunPosition, Chord.Bare("Mouse5"));

        Assert.Equal(RebindStatus.RefusedConflict, result.Status);
        Assert.Equal(BindingAction.Ptt, result.ConflictsWith);
    }

    [Fact]
    public void Rebinding_an_action_to_the_chord_it_already_holds_is_accepted()
    {
        var bindings = BindingSet.Defaults();

        Assert.True(bindings.Rebind(BindingAction.Roles, Chord.RightAlt("R")).Applied);
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
        Assert.False(bindings.PttChosen);
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
    public void The_suggested_ptt_is_mouse5_and_is_never_applied_on_its_own()
    {
        Assert.Equal("Mouse5", BindingSet.SuggestedPtt.Label);
        Assert.False(BindingSet.Defaults().PttChosen);
    }
}
