using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// A digit is a digit, whichever row of the keyboard it came from.
/// </summary>
/// <remarks>
/// The numpad did nothing. Its keys are labeled Numpad0 to Numpad9, and both the arming table and
/// the digit test only ever looked at the single-character labels of the number row, so a six digit
/// invite code or an eight digit typed grid could not be entered on the pad at all. That is the
/// place a hand goes to type numbers.
/// </remarks>
public sealed class DigitsComeFromBothRowsTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("9", 9)]
    [InlineData("Numpad0", 0)]
    [InlineData("Numpad5", 5)]
    [InlineData("Numpad9", 9)]
    public void Both_rows_read_as_the_same_digit(string label, int expected)
    {
        Assert.True(BindingKey.TryFromLabel(label, out var key));
        Assert.True(key.TryDigit(out var digit), $"{label} is not read as a digit");
        Assert.Equal(expected, digit);
    }

    [Theory]
    [InlineData("W")]
    [InlineData("NumpadAdd")]
    [InlineData("NumpadDecimal")]
    [InlineData("F1")]
    public void Nothing_else_reads_as_a_digit(string label)
    {
        Assert.True(BindingKey.TryFromLabel(label, out var key));
        Assert.False(key.TryDigit(out _), $"{label} is being read as a digit");
    }

    [Fact]
    public void An_open_menu_arms_both_rows()
    {
        var armed = ArmedKeysFor(menuOpen: true);

        for (var d = 0; d <= 9; d++)
        {
            var digit = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var label in (string[])[digit, $"Numpad{digit}"])
            {
                Assert.True(BindingKey.TryFromLabel(label, out var key));
                Assert.True(IsArmed(armed, key), $"{label} is not armed while the menu is open");
            }
        }
    }

    [Fact]
    public void The_menu_no_longer_eats_escape()
    {
        var armed = ArmedKeysFor(menuOpen: true);

        // Escape discarded and closed, which is what letting go of the hold key already does, and
        // for as long as the menu was open the game could not see its own Escape.
        Assert.True(BindingKey.TryFromLabel("Escape", out var key));
        Assert.False(IsArmed(armed, key), "the menu is still swallowing Escape");
    }

    /// <summary>
    /// Backspace is armed with the digits. The back key deletes a typed digit too, and that was the
    /// argument for leaving this one out, but a hand that just typed a grid on the number row
    /// reaches for Backspace, and on a coordinate page it did nothing at all.
    /// </summary>
    [Fact]
    public void An_open_menu_arms_backspace()
    {
        Assert.True(BindingKey.TryFromLabel("Backspace", out var key));

        Assert.True(IsArmed(ArmedKeysFor(menuOpen: true), key), "Backspace is not armed with the digits");
        Assert.False(IsArmed(ArmedKeysFor(menuOpen: false), key), "Backspace is taken from the game with no menu open");
    }

    [Fact]
    public void A_closed_menu_takes_no_digit_from_the_game()
    {
        var armed = ArmedKeysFor(menuOpen: false);

        Assert.True(BindingKey.TryFromLabel("4", out var row));
        Assert.True(BindingKey.TryFromLabel("Numpad4", out var pad));
        Assert.False(IsArmed(armed, row));
        Assert.False(IsArmed(armed, pad));
    }

    private static ArmedKeys ArmedKeysFor(bool menuOpen)
    {
        var bindings = BindingSet.Defaults();
        var panic = new PanicSwitch();
        foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
        {
            panic.Register(subsystem, new Inert());
        }

        panic.Arm();

        var bridge = new InputBridge(bindings, panic, new FixedForegroundProbe(true, true));
        bridge.Connect(null, null, null, new Gate(menuOpen));
        return bridge.Armed;
    }

    /// <summary>ArmedKeys is indexed by virtual key code, which BindingKey never hands out.</summary>
    private static bool IsArmed(ArmedKeys armed, BindingKey key)
    {
        for (var code = 0; code < 256; code++)
        {
            if (armed.IsArmed(code) && key.Matches(code))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Gate(bool open) : IMenuGate
    {
        public bool MenuIsOpen { get; } = open;
    }

    private sealed class Inert : ISuspendable
    {
        public void Suspend()
        {
        }

        public void Resume()
        {
        }
    }
}
