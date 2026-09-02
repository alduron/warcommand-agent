using System.Threading;
using System.Windows.Threading;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Game;
using WarCommand.Agent.Input;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// Binding rule 7: one Panic press suspends every hook, capture and draw. The overlay is the draw.
/// </summary>
/// <remarks>
/// <see cref="PanicSwitch.Arm"/> refuses to arm until every <see cref="PanicSubsystem"/> has a
/// registration, so a surface that is not an <see cref="ISuspendable"/> does not merely miss the
/// kill switch: it stops the whole switch arming.
/// </remarks>
public class OverlayPanicTests
{
    [Fact]
    public void The_controller_can_be_registered_as_the_overlay_drawing_subsystem()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            var panic = new PanicSwitch();

            panic.Register(PanicSubsystem.OverlayDrawing, controller);

            Assert.IsAssignableFrom<ISuspendable>(controller);
        });
    }

    [Fact]
    public void Panic_takes_the_surface_off_screen_and_releasing_it_puts_it_back()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            controller.OverlayVisibilityChanged(OverlayVisibility.Show);
            Assert.True(controller.IsDrawing);

            controller.Suspend();

            Assert.True(controller.IsSuspended);
            Assert.False(controller.IsDrawing);

            controller.Resume();

            Assert.False(controller.IsSuspended);
            Assert.True(controller.IsDrawing);
        });
    }

    /// <summary>Panic outranks the tracker: a Show that lands while suspended draws nothing.</summary>
    [Fact]
    public void Nothing_the_tracker_says_can_bring_it_back_while_panic_is_engaged()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            controller.Suspend();

            controller.OverlayVisibilityChanged(OverlayVisibility.Show);
            controller.GameWindowFound(new GameWindowScan(1, new ScreenRect(0, 0, 1920, 1080), true, false));

            Assert.False(controller.IsDrawing);
        });
    }

    /// <summary>The tray says panic rather than "waiting for game", which would be a lie.</summary>
    [Fact]
    public void The_tray_row_names_panic_as_the_reason_nothing_is_drawing()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            controller.Suspend();

            Assert.Equal("panic", controller.Hint);
        });
    }

    private static OverlayController Controller() => new(
        Dispatcher.CurrentDispatcher,
        new AgentSettings { OverlayMode = OverlayMode.AlwaysOn },
        factory: () => new OverlayWindow());

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (failure is not null)
        {
            throw new InvalidOperationException("The STA body threw.", failure);
        }
    }
}
