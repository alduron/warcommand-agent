using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// Panic is exempt from foreground gating. It lives beside the forbidden-API scan because it is the
/// same kind of rule: a promise the product makes that a future refactor will quietly break.
/// Specified verbatim in docs/design/14-testing.md.
/// </summary>
public class PanicInvariantTests
{
    private static Chord Ptt => BindingSet.SuggestedPtt;

    private static Chord RightAltD => Chord.RightAlt("D");

    private static Chord RightAltP => Chord.RightAlt("P");

    [Fact]
    public void Panic_fires_with_no_game_window()
    {
        var input = new InputBridge(gameForeground: false, gameRunning: false);
        Assert.False(input.Handle(Ptt).Dispatched);       // every other binding is inert
        Assert.False(input.Handle(RightAltD).Dispatched);
        Assert.True(input.Handle(RightAltP).Dispatched);  // Panic is not
    }

    [Fact]
    public void Panic_fires_while_the_game_runs_but_is_not_foreground()
    {
        var input = new InputBridge(gameForeground: false, gameRunning: true);

        Assert.False(input.Handle(RightAltD).Dispatched);
        Assert.True(input.Handle(RightAltP).Dispatched);
    }

    [Fact]
    public void Panic_releases_as_well_as_engages_with_no_game_window()
    {
        var input = new InputBridge(gameForeground: false, gameRunning: false);

        Assert.True(input.Handle(RightAltP).Dispatched);
        Assert.True(input.Panic.IsSuspended);

        // The press that undoes it is subject to no gate either, or Panic is a one-way door.
        Assert.True(input.Handle(RightAltP).Dispatched);
        Assert.False(input.Panic.IsSuspended);
    }

    [Fact]
    public void Nothing_but_panic_is_armed_while_suspended()
    {
        var input = new InputBridge(gameForeground: true, gameRunning: true);
        input.Handle(RightAltP);

        Assert.True(input.Panic.IsSuspended);
        Assert.False(input.Handle(RightAltD).Dispatched);
        Assert.Equal(DispatchOutcome.Suspended, input.Handle(RightAltD).Outcome);
        Assert.True(input.Handle(RightAltP).Dispatched);
    }

    [Fact]
    public void Panic_cannot_be_unbound()
    {
        var bindings = BindingSet.Defaults();

        var result = bindings.Unbind(BindingAction.Panic);

        Assert.Equal(RebindStatus.RefusedCannotUnbind, result.Status);
        Assert.True(bindings[BindingAction.Panic].IsBound);
    }

    [Fact]
    public void Reset_to_defaults_leaves_panic_bound_and_both_hold_keys_bound()
    {
        var bindings = BindingSet.Defaults();
        bindings.Rebind(BindingAction.Ptt, BindingSet.SuggestedPtt);
        bindings.Rebind(BindingAction.Panic, Chord.RightAlt("F12"));

        bindings.ResetToDefaults();

        Assert.Equal("RightAlt+P", bindings[BindingAction.Panic].Label);
        Assert.Equal("V", bindings[BindingAction.Ptt].Label);
        Assert.Equal("CapsLock", bindings[BindingAction.Menu].Label);
    }
}
