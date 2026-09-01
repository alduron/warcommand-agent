using System.Threading;
using System.Windows.Forms;
using WarCommand.Agent.Core.Tray;
using WarCommand.Agent.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The WinForms half of the tray. <see cref="TrayMenu"/> is covered without a message loop; this is
/// for the things only the real control can be wrong about.
/// </summary>
public class TrayIconControllerTests
{
    /// <summary>WinForms wants STA, and xUnit gives every test an MTA thread.</summary>
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
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException("The STA body threw.", failure);
        }
    }

    /// <summary>
    /// A ContextMenuStrip holding no items refuses to open, and it decides that before Opening can
    /// fill it. Built only in Opening, the tray menu was a right-click that did nothing at all.
    /// </summary>
    [Fact]
    public void The_menu_has_its_rows_before_it_is_ever_opened()
    {
        OnStaThread(() =>
        {
            using var tray = new TrayIconController();

            Assert.NotEmpty(tray.Menu.Items);
            Assert.Contains(tray.Menu.Items.Cast<ToolStripItem>(), item => item.Text == "Quit");
        });
    }

    [Fact]
    public void Quit_is_always_reachable_whatever_the_state()
    {
        OnStaThread(() =>
        {
            using var tray = new TrayIconController
            {
                StateProvider = () => new TrayMenuState { IsPaired = true, IsDev = false },
            };

            var quit = tray.Menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "Quit");
            Assert.True(quit.Enabled);
        });
    }

    [Fact]
    public void Opening_rebuilds_from_the_current_state_rather_than_the_one_it_was_built_with()
    {
        OnStaThread(() =>
        {
            var secondScreenVisible = false;
            using var tray = new TrayIconController
            {
                StateProvider = () => new TrayMenuState { SecondScreenVisible = secondScreenVisible },
            };

            static ToolStripItem Row(TrayIconController tray) =>
                tray.Menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "Second-screen mode");

            Assert.Equal("off", ((ToolStripMenuItem)Row(tray)).ShortcutKeyDisplayString);

            secondScreenVisible = true;
            tray.Menu.PerformLayout();
            typeof(TrayIconController)
                .GetMethod("Rebuild", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(tray, null);

            Assert.Equal("on", ((ToolStripMenuItem)Row(tray)).ShortcutKeyDisplayString);
        });
    }
}
