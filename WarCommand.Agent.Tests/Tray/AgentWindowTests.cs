using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarCommand.Agent;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The agent's one window. WPF resolves a control template the first time it renders, so a broken
/// one throws when a tab is opened rather than when the window is built. Every tab is forced here.
/// </summary>
public class AgentWindowTests
{
    /// <summary>Deliberately bright, and fills rather than grounds: accent, urgent, yours, warn, ink.</summary>
    private static readonly string[] StateFills =
        ["#FFD9A840", "#FFE2685C", "#FF7BCB5A", "#FFE3AC43", "#FFFFFFFF"];

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

    private static SettingsStore TempStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-agent-tests", Guid.NewGuid().ToString("N"));
        var paths = new AgentPaths(root);
        paths.EnsureCreated();
        return new SettingsStore(paths);
    }

    [Fact]
    public void Every_tab_renders_without_throwing()
    {
        OnStaThread(() =>
        {
            var window = new AgentWindow(TempStore(), devices: null);
            window.Show();

            for (var i = 0; i < window.Tabs.Items.Count; i++)
            {
                window.Tabs.SelectedIndex = i;
                window.UpdateLayout();
            }

            // Audio, Keybinds, Speech, Overlay. No Board: the queue is the web board.
            Assert.Equal(4, window.Tabs.Items.Count);
            window.Close();
        });
    }

    [Fact]
    public void The_board_and_the_settings_live_in_the_same_window()
    {
        OnStaThread(() =>
        {
            var window = new AgentWindow(TempStore(), devices: null);
            window.Show();

            // Settings and nothing else. The queue is the web board and the glance is the overlay,
            // so a third copy of the board in a desktop tab was the same list a worse way.
            window.ShowSettingsTab();

            Assert.Equal(0, window.Tabs.SelectedIndex);
            Assert.DoesNotContain(
                window.Tabs.Items.Cast<TabItem>(),
                tab => string.Equals(tab.Header as string, "BOARD", StringComparison.Ordinal));
            window.Close();
        });
    }

    [Fact]
    public void Nothing_in_the_window_paints_a_light_background()
    {
        OnStaThread(() =>
        {
            var window = new AgentWindow(TempStore(), devices: null);
            window.Show();

            for (var i = 0; i < window.Tabs.Items.Count; i++)
            {
                window.Tabs.SelectedIndex = i;
                window.UpdateLayout();
            }

            // Every ground in this product is dark. A stray light one is the two-theme bug.
            foreach (var (element, colour) in Grounds(window))
            {
                if (StateFills.Contains(colour.ToString(CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                var luminance = (0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B);
                Assert.True(
                    colour.A < 0x40 || luminance < 140,
                    FormattableString.Invariant(
                        $"{element} paints {colour}, too light for the agent's theme."));
            }

            window.Close();
        });
    }

    /// <summary>The pairing dialog is the other top-level surface, and it shipped light once.</summary>
    [Fact]
    public void The_pairing_dialog_paints_the_same_dark_ground()
    {
        OnStaThread(() =>
        {
            var window = new PairingCodeWindow((_, _) => Task.CompletedTask);
            window.Show();
            window.UpdateLayout();

            var ground = (SolidColorBrush)window.FindResource("Ground");
            Assert.Equal(ground.Color, ((SolidColorBrush)window.Background).Color);

            foreach (var (element, colour) in Grounds(window))
            {
                if (StateFills.Contains(colour.ToString(CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                var luminance = (0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B);
                Assert.True(
                    colour.A < 0x40 || luminance < 140,
                    FormattableString.Invariant(
                        $"{element} paints {colour}, too light for the agent's theme."));
            }

            window.Close();
        });
    }

    /// <summary>
    /// Every colour any element paints as its ground. Gradients are flattened to their stops: the
    /// first version of this walked solid brushes only, and the board's terrain gradient went
    /// straight past it.
    /// </summary>
    private static IEnumerable<(string Element, Color Colour)> Grounds(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            var background = child switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null,
            };

            foreach (var colour in ColoursOf(background))
            {
                yield return (child.GetType().Name, colour);
            }

            foreach (var found in Grounds(child))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<Color> ColoursOf(Brush? brush) => brush switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color),
        _ => [],
    };
}
