using WarCommand.Agent.Core.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The overlay rows, the display picker, the status word and the update rows. All of them exist
/// because what they control is otherwise invisible from the tray.
/// </summary>
public class TrayMenuOverlayTests
{
    private static readonly TrayMenuState Empty = new();

    private static readonly TrayDisplay[] ThreeScreens =
    [
        new(@"\\.\DISPLAY1", "display 1 (1920x1080)", IsPrimary: true),
        new(@"\\.\DISPLAY2", "display 2 (1920x1080)", IsPrimary: false),
        new(@"\\.\DISPLAY3", "display 3 (2560x1440)", IsPrimary: false),
    ];

    private static TrayMenuItem? Find(IEnumerable<TrayMenuItem> items, string text) =>
        items.FirstOrDefault(i => i.Text == text);

    private static TrayMenuItem? Child(IEnumerable<TrayMenuItem> items, string parent, string child) =>
        Find(items, parent)?.Children.FirstOrDefault(i => i.Text == child);

    // --- the status word -------------------------------------------------------------------------

    /// <summary>
    /// Connected and signed in are different questions with different fixes, so the dot's colour
    /// alone cannot answer them. Green plus "connected" means both.
    /// </summary>
    [Theory]
    [InlineData(TrayIndicator.Connected, true, false, "connected")]
    [InlineData(TrayIndicator.Connected, false, false, "not signed in")]
    [InlineData(TrayIndicator.Reconnecting, true, false, "reconnecting")]
    [InlineData(TrayIndicator.Offline, true, false, "not connected")]
    [InlineData(TrayIndicator.Connected, true, true, "panic engaged")]
    public void The_title_row_says_what_the_dot_means(
        TrayIndicator indicator, bool paired, bool panic, string expected)
    {
        var state = Empty with { Indicator = indicator, IsPaired = paired, PanicEngaged = panic };

        Assert.Equal(expected, TrayMenu.StatusWord(state));
        Assert.Equal(expected, TrayMenu.Build(state)[0].Value);
    }

    // --- overlay mode ----------------------------------------------------------------------------

    [Fact]
    public void There_is_no_overlay_row_before_the_overlay_exists()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), "Overlay"));
    }

    [Fact]
    public void All_three_modes_are_offered_and_the_current_one_is_checked()
    {
        var items = TrayMenu.Build(Empty with { OverlayMode = "MirrorGame" });

        Assert.NotNull(Child(items, "Overlay", "Always on"));
        Assert.NotNull(Child(items, "Overlay", "Hidden"));

        var mirror = Child(items, "Overlay", "Mirror Wardogs");
        Assert.NotNull(mirror);
        Assert.True(mirror.IsChecked);
        Assert.Equal(TrayCommand.SelectOverlayMode, mirror.Command);
        Assert.Equal("MirrorGame", mirror.Argument);

        Assert.False(Child(items, "Overlay", "Always on")!.IsChecked);
    }

    [Theory]
    [InlineData("AlwaysOn", "always on")]
    [InlineData("Hidden", "hidden")]
    public void The_collapsed_row_names_the_mode(string mode, string expected)
    {
        Assert.Equal(expected, Find(TrayMenu.Build(Empty with { OverlayMode = mode }), "Overlay")!.Value);
    }

    /// <summary>
    /// Mirroring can be correct and show nothing, so it always says why. Without this the row
    /// reads "mirroring Wardogs" over a blank screen and looks broken.
    /// </summary>
    [Fact]
    public void Mirroring_says_what_it_is_waiting_for()
    {
        var items = TrayMenu.Build(Empty with
        {
            OverlayMode = "MirrorGame",
            OverlayHint = "waiting for Wardogs",
        });

        Assert.Equal("waiting for Wardogs", Find(items, "Overlay")!.Value);
    }

    // --- the display picker ----------------------------------------------------------------------

    [Fact]
    public void Every_monitor_is_offered_and_the_chosen_one_is_checked()
    {
        var items = TrayMenu.Build(Empty with
        {
            OverlayMode = "AlwaysOn",
            Displays = ThreeScreens,
            OverlayDisplayDeviceName = @"\\.\DISPLAY3",
        });

        var row = Find(items, "Overlay display");
        Assert.NotNull(row);
        Assert.Equal("display 3 (2560x1440)", row.Value);
        Assert.Equal(3, row.Children.Count);

        var chosen = row.Children.Single(c => c.IsChecked is true);
        Assert.Equal(@"\\.\DISPLAY3", chosen.Argument);
        Assert.Equal(TrayCommand.SelectOverlayDisplay, chosen.Command);
    }

    /// <summary>Null device name means the primary, not "nothing chosen".</summary>
    [Fact]
    public void With_nothing_chosen_the_primary_is_the_checked_one()
    {
        var items = TrayMenu.Build(Empty with { OverlayMode = "AlwaysOn", Displays = ThreeScreens });

        var row = Find(items, "Overlay display");
        Assert.NotNull(row);
        Assert.Equal(@"\\.\DISPLAY1", row.Children.Single(c => c.IsChecked is true).Argument);
    }

    /// <summary>
    /// Mirroring puts the board on the game's screen by definition, so a monitor picker there
    /// would be a control that does nothing.
    /// </summary>
    [Fact]
    public void Mirroring_offers_no_display_picker()
    {
        var items = TrayMenu.Build(Empty with { OverlayMode = "MirrorGame", Displays = ThreeScreens });

        Assert.Null(Find(items, "Overlay display"));
    }

    [Fact]
    public void One_monitor_offers_no_display_picker()
    {
        var items = TrayMenu.Build(Empty with { OverlayMode = "AlwaysOn", Displays = [ThreeScreens[0]] });

        Assert.Null(Find(items, "Overlay display"));
    }

    // --- the rest of what the menu must carry ----------------------------------------------------

    /// <summary>
    /// "Second-screen mode" named a mode nobody could define. It is the app's own window, the same
    /// one Settings opens.
    /// </summary>
    [Fact]
    public void The_window_row_is_called_what_it_is()
    {
        var items = TrayMenu.Build(Empty);

        Assert.Null(Find(items, "Second-screen mode"));
        Assert.Equal(TrayCommand.ToggleSecondScreen, Find(items, "Board window")!.Command);
    }

    [Fact]
    public void Sounds_start_with_windows_settings_and_quit_are_all_present()
    {
        var items = TrayMenu.Build(Empty with
        {
            SoundsEnabled = true,
            StartWithWindows = false,
            SettingsAvailable = true,
        });

        Assert.Equal(TrayCommand.ToggleSounds, Find(items, "Sounds")!.Command);
        Assert.Equal(TrayCommand.ToggleStartWithWindows, Find(items, "Start with Windows")!.Command);
        Assert.Equal(TrayCommand.OpenSettings, Find(items, "Settings...")!.Command);
        Assert.Equal(TrayCommand.Quit, items[^1].Command);
    }

    // --- updates ---------------------------------------------------------------------------------

    [Fact]
    public void There_is_no_check_for_updates_row_before_the_checker_exists()
    {
        Assert.DoesNotContain(TrayMenu.Build(Empty), i => i.Command == TrayCommand.CheckForUpdates);
    }

    [Fact]
    public void With_nothing_on_offer_the_row_offers_a_check_and_names_the_running_build()
    {
        var items = TrayMenu.Build(Empty with { UpdateCheckAvailable = true, RunningVersion = "1.4.0" });

        var row = Find(items, "Check for updates");
        Assert.NotNull(row);
        Assert.Equal("1.4.0", row.Value);
    }

    [Fact]
    public void A_check_in_flight_cannot_be_clicked_again()
    {
        var items = TrayMenu.Build(Empty with
        {
            UpdateCheckAvailable = true,
            UpdateCheckInProgress = true,
        });

        Assert.DoesNotContain(items, i => i.Command == TrayCommand.CheckForUpdates);
        Assert.Contains(items, i => i.Text == "Checking for updates..." && !i.IsEnabled);
    }

    /// <summary>An offer outranks the check: the row that matters is the one you can install.</summary>
    [Fact]
    public void An_offer_replaces_the_check_row()
    {
        var items = TrayMenu.Build(Empty with
        {
            UpdateCheckAvailable = true,
            RunningVersion = "1.3.0",
            UpdateVersion = "1.4.0",
        });

        Assert.DoesNotContain(items, i => i.Command == TrayCommand.CheckForUpdates);
        Assert.Contains(items, i => i.Command == TrayCommand.InstallUpdate);
    }
}
