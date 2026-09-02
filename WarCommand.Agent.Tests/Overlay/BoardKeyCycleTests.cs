using System.Threading;
using System.Windows.Threading;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Game;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The board key. It replaced a show/hide toggle and a three-step opacity cycle, which were two of
/// the twelve bindings the hotkey surface used to carry.
/// </summary>
public class BoardKeyCycleTests
{
    [Fact]
    public void One_key_cycles_full_dim_off_and_round()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            Assert.Equal(BoardStep.Full, controller.BoardStep);

            controller.CycleBoard();
            Assert.Equal(BoardStep.Dim, controller.BoardStep);
            Assert.True(controller.IsDrawing);

            // Off fades out rather than cutting, the same path an alt-tab takes, so the surface is
            // still on screen for the length of the fade. Panic is the one that cuts; see OverlayPanicTests.
            controller.CycleBoard();
            Assert.Equal(BoardStep.Off, controller.BoardStep);

            controller.CycleBoard();
            Assert.Equal(BoardStep.Full, controller.BoardStep);
            Assert.True(controller.IsDrawing);
        });
    }

    /// <summary>Dim multiplies the chosen opacity. The settings slider still says how bright full is.</summary>
    [Fact]
    public void The_cycle_never_rewrites_the_opacity_setting()
    {
        OnStaThread(() =>
        {
            var settings = new AgentSettings { OverlayMode = OverlayMode.AlwaysOn, Opacity = OverlayOpacity.Low };
            using var controller = new OverlayController(
                Dispatcher.CurrentDispatcher, settings, factory: () => new OverlayWindow());

            controller.CycleBoard();
            controller.CycleBoard();
            controller.CycleBoard();

            Assert.Equal(OverlayOpacity.Low, settings.Opacity);
        });
    }

    /// <summary>Panic outranks it: cycling back to full while suspended still draws nothing.</summary>
    [Fact]
    public void Panic_outranks_the_board_key()
    {
        OnStaThread(() =>
        {
            using var controller = Controller();
            controller.Suspend();

            controller.CycleBoard();
            controller.CycleBoard();
            controller.CycleBoard();

            Assert.False(controller.IsDrawing);
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
