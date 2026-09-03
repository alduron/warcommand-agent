using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// A hold that starts always ends, on every path out of it.
/// </summary>
/// <remarks>
/// The sink opens a microphone on the down edge and closes it on the up edge. Two paths ended a
/// hold without an up edge: Panic, which returned before delivering it, and losing the game window,
/// which cleared the state silently. Either one left the capture open with nothing to close it,
/// every later press early-returned because it still read as holding, and the menu's orphan guard
/// could never fire. Binding rule 7 is one press stopping everything, which this is part of.
/// </remarks>
public sealed class HoldAlwaysEndsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Losing_the_game_window_ends_the_hold_at_the_sink()
    {
        var harness = new Harness(gameForeground: true);
        harness.Bridge.Handle(harness.Ptt_Chord, T0);
        Assert.Equal(1, harness.Ptt.Downs);

        // Alt-tab. The watchdog runs on the board tick and finds a hold with no foreground.
        harness.Foreground.GameIsForeground = false;
        harness.Bridge.EnforceHoldLimit();

        Assert.Equal(1, harness.Ptt.Ups);
    }

    [Fact]
    public void Panic_mid_hold_ends_the_hold_at_the_sink()
    {
        var harness = new Harness(gameForeground: true);
        harness.Bridge.Handle(harness.Ptt_Chord, T0);

        harness.Bridge.Panic.Toggle();
        harness.Bridge.ReleaseHold(T0.AddSeconds(1));

        Assert.True(harness.Bridge.Panic.IsSuspended);
        Assert.Equal(1, harness.Ptt.Ups);
    }

    [Fact]
    public void A_real_key_up_under_panic_still_reaches_the_sink()
    {
        var harness = new Harness(gameForeground: true);
        harness.Bridge.Handle(harness.Ptt_Chord, T0);

        // Panic first, then the finger comes off. This returned Suspended and dropped the edge, so
        // the one release that was always going to arrive was the one that never got through.
        harness.Bridge.Panic.Toggle();
        harness.Bridge.HandleUp(harness.Ptt_Chord, T0.AddSeconds(1));

        Assert.Equal(1, harness.Ptt.Ups);
    }

    [Fact]
    public void Releasing_a_hold_that_never_started_tells_nobody()
    {
        var harness = new Harness(gameForeground: true);

        harness.Bridge.ReleaseHold(T0);

        // A spurious up edge would close a capture somebody else opened, and on the menu side it
        // would commit a surface nobody had open.
        Assert.Equal(0, harness.Ptt.Ups);
    }

    private sealed class Harness
    {
        internal Harness(bool gameForeground)
        {
            Bindings = BindingSet.Defaults();
            Bindings.Rebind(BindingAction.Ptt, BindingSet.SuggestedPtt);
            Ptt_Chord = Bindings[BindingAction.Ptt];

            var panic = new PanicSwitch();
            foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
            {
                panic.Register(subsystem, new NoOpSuspendable());
            }

            panic.Arm();

            Foreground = new MovableForegroundProbe(gameForeground);
            Bridge = new InputBridge(Bindings, panic, Foreground);
            Bridge.Connect(Ptt, null, null, null);
        }

        internal BindingSet Bindings { get; }

        internal Chord Ptt_Chord { get; }

        internal MovableForegroundProbe Foreground { get; }

        internal InputBridge Bridge { get; }

        internal CountingPtt Ptt { get; } = new();
    }

    private sealed class CountingPtt : IPttSink
    {
        internal int Downs { get; private set; }

        internal int Ups { get; private set; }

        public void PttDown(DateTimeOffset at) => Downs++;

        public void PttUp(DateTimeOffset at) => Ups++;
    }

    private sealed class MovableForegroundProbe(bool foreground) : IForegroundProbe
    {
        public bool GameIsForeground { get; set; } = foreground;

        public bool GameIsRunning => true;
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
}
