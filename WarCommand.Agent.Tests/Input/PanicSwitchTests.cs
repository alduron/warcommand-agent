using WarCommand.Agent.Input;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// One press moves everything. A flag per subsystem is four things a later change can leave half
/// set, and half a kill switch is not a kill switch.
/// </summary>
public class PanicSwitchTests
{
    [Fact]
    public void One_press_suspends_every_subsystem()
    {
        var (panic, targets) = Armed();

        panic.Toggle();

        Assert.True(panic.IsSuspended);
        Assert.All(targets.Values, t => Assert.Equal(1, t.Suspends));
        Assert.All(targets.Values, t => Assert.Equal(0, t.Resumes));
    }

    [Fact]
    public void Pressing_again_resumes_every_subsystem()
    {
        var (panic, targets) = Armed();

        panic.Toggle();
        panic.Toggle();

        Assert.False(panic.IsSuspended);
        Assert.All(targets.Values, t => Assert.Equal(1, t.Resumes));
    }

    [Fact]
    public void It_refuses_to_arm_while_a_subsystem_would_be_missed()
    {
        var panic = new PanicSwitch();
        panic.Register(PanicSubsystem.Hotkeys, new CountingSuspendable());

        var error = Assert.Throws<InvalidOperationException>(panic.Arm);

        Assert.Contains(nameof(PanicSubsystem.AudioCapture), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PanicSubsystem.OverlayDrawing), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PanicSubsystem.ScreenCapture), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PanicSubsystem.TrayIndicator), error.Message, StringComparison.Ordinal);
        Assert.False(panic.IsArmed);
    }

    [Fact]
    public void An_unarmed_switch_refuses_to_toggle()
    {
        Assert.Throws<InvalidOperationException>(() => new PanicSwitch().Toggle());
    }

    [Fact]
    public void The_fan_out_covers_the_four_named_subsystems_and_the_tray()
    {
        Assert.Equal(
            [
                PanicSubsystem.Hotkeys,
                PanicSubsystem.ScreenCapture,
                PanicSubsystem.OverlayDrawing,
                PanicSubsystem.AudioCapture,
                PanicSubsystem.TrayIndicator,
            ],
            Enum.GetValues<PanicSubsystem>());
    }

    [Fact]
    public void Hotkeys_go_down_first_and_come_back_last()
    {
        var order = new List<string>();
        var panic = new PanicSwitch();
        foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
        {
            panic.Register(subsystem, new OrderingSuspendable(subsystem.ToString(), order));
        }

        panic.Arm();
        panic.Toggle();
        Assert.Equal(nameof(PanicSubsystem.Hotkeys) + ":suspend", order[0]);

        order.Clear();
        panic.Toggle();
        Assert.Equal(nameof(PanicSubsystem.Hotkeys) + ":resume", order[^1]);
    }

    private static (PanicSwitch Panic, Dictionary<PanicSubsystem, CountingSuspendable> Targets) Armed()
    {
        var panic = new PanicSwitch();
        var targets = new Dictionary<PanicSubsystem, CountingSuspendable>();

        foreach (var subsystem in Enum.GetValues<PanicSubsystem>())
        {
            var target = new CountingSuspendable();
            targets[subsystem] = target;
            panic.Register(subsystem, target);
        }

        panic.Arm();
        return (panic, targets);
    }

    private sealed class CountingSuspendable : ISuspendable
    {
        internal int Suspends { get; private set; }

        internal int Resumes { get; private set; }

        public void Suspend() => Suspends++;

        public void Resume() => Resumes++;
    }

    private sealed class OrderingSuspendable(string name, List<string> order) : ISuspendable
    {
        public void Suspend() => order.Add(name + ":suspend");

        public void Resume() => order.Add(name + ":resume");
    }
}
