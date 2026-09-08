using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// Covers the pure edge of the fix for the tray rows that were never populated: MapName,
/// MicrophoneName, PushToTalkLabel, PanicArmed and PanicChordLabel all sat unset in App.xaml.cs
/// forever, so TrayMenu.Build never rendered them despite being fully tested against hand-set
/// state. See Caveat_WarCommandTrayMenuStateFieldsNeverSet.
/// </summary>
/// <remarks>
/// The wiring itself lives in App.OnStartup, App.ArmForAccount and App.OnBindingsChanged, which are
/// unreachable from a test per Caveat_WarCommandAgentAppXamlCsIsMultiAgentHotFile /
/// StartupArmingTests' own note: the composition root is a WPF Application whose startup cannot be
/// driven from a test. <see cref="WarCommand.Agent.App.ChordLabelOrNull"/> is the one piece of that
/// wiring that is pure, so it is exercised directly here.
/// </remarks>
public class TrayStateWiringTests
{
    [Fact]
    public void A_bound_chord_renders_its_label()
    {
        Assert.Equal("RightAlt+P", WarCommand.Agent.App.ChordLabelOrNull(Chord.RightAlt("P")));
        Assert.Equal("V", WarCommand.Agent.App.ChordLabelOrNull(Chord.Bare("V")));
    }

    [Fact]
    public void An_unbound_chord_hides_the_row_rather_than_rendering_a_placeholder() =>
        Assert.Null(WarCommand.Agent.App.ChordLabelOrNull(Chord.Unbound));
}
