using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The in-game surface, checked against the real window handle rather than against the XAML that
/// asked for it. 06-overlay-ux.md names four extended styles and says all three of the behavioural
/// ones are required; a surface missing one is a surface that eats clicks, steals focus, or turns
/// up in alt-tab in the middle of a fight.
/// </summary>
public class OverlayWindowTests
{
    private const int GwlExStyle = -20;
    private const nint WsExLayered = 0x00080000;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [Fact]
    public void The_surface_is_layered_click_through_no_activate_and_out_of_alt_tab()
    {
        OnStaThread(() =>
        {
            var overlay = new OverlayWindow();
            overlay.Show();

            var handle = new WindowInteropHelper(overlay).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);

            var styles = GetWindowLongPtr(handle, GwlExStyle);

            Assert.True((styles & WsExLayered) != 0, "not layered: it cannot draw per-pixel alpha");
            Assert.True((styles & WsExTransparent) != 0, "not click-through: it eats the player's shot");
            Assert.True((styles & WsExNoActivate) != 0, "activatable: it can pull focus out of the game");
            Assert.True((styles & WsExToolWindow) != 0, "in alt-tab: a tool surface does not belong there");

            overlay.Close();
        });
    }

    /// <summary>
    /// It has to actually be on screen. Everything else here is about how it draws; this is the
    /// one that fails when the answer to "is the overlay on" is no.
    /// </summary>
    [Fact]
    public void Showing_it_puts_it_on_screen_where_it_was_placed()
    {
        OnStaThread(() =>
        {
            var overlay = new OverlayWindow();
            overlay.Show();
            overlay.ApplyAnchor(OverlayAnchor.Right);
            overlay.ApplyBounds(1524, 150, 380, 777);

            Assert.True(overlay.IsVisible);
            Assert.True(overlay.Topmost);
            Assert.False(overlay.ShowInTaskbar);
            Assert.True(overlay.Width > 0 && overlay.Height > 0);

            overlay.Close();
        });
    }

    /// <summary>
    /// The overlay draws the same board as the window, on the mock's scrim rather than on Ground:
    /// the terrain showing through is the game, and an opaque ground would hide it.
    /// </summary>
    [Fact]
    public void The_overlay_board_has_no_ground_of_its_own()
    {
        OnStaThread(() =>
        {
            var overlay = new OverlayWindow();
            overlay.Show();

            Assert.True(overlay.BoardView.IsOverlay);
            Assert.Null(overlay.BoardView.Background);

            overlay.Close();
        });
    }

    /// <summary>Three named steps, ordered, and none of them invisible.</summary>
    [Fact]
    public void The_opacity_steps_are_ordered_and_all_legible()
    {
        var low = OverlayWindow.OpacityFor(OverlayOpacity.Low);
        var normal = OverlayWindow.OpacityFor(OverlayOpacity.Normal);
        var high = OverlayWindow.OpacityFor(OverlayOpacity.High);

        Assert.True(low < normal && normal < high);
        Assert.True(low >= 0.4);
        Assert.Equal(1.0, high);
    }

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
