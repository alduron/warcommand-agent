using WarCommand.Agent.Core.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The overlay row and the manual update check. Both exist because the thing they control is
/// invisible: an overlay that is not drawing and a check that runs every six hours.
/// </summary>
public class TrayMenuOverlayTests
{
    private static readonly TrayMenuState Empty = new();

    private static TrayMenuItem? Find(IEnumerable<TrayMenuItem> items, TrayCommand command) =>
        items.FirstOrDefault(i => i.Command == command)
        ?? items.SelectMany(i => i.Children).FirstOrDefault(i => i.Command == command);

    /// <summary>Absent, not greyed, until the overlay subsystem exists. Same rule as every row.</summary>
    [Fact]
    public void There_is_no_overlay_row_before_the_overlay_exists()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.ToggleOverlay));
    }

    [Fact]
    public void Switched_off_the_row_reads_off()
    {
        var row = Find(TrayMenu.Build(Empty with { OverlayEnabled = false }), TrayCommand.ToggleOverlay);

        Assert.NotNull(row);
        Assert.Equal("off", row.Value);
        Assert.False(row.IsChecked);
    }

    [Fact]
    public void Switched_on_and_drawing_the_row_reads_on()
    {
        var row = Find(TrayMenu.Build(Empty with { OverlayEnabled = true }), TrayCommand.ToggleOverlay);

        Assert.NotNull(row);
        Assert.Equal("on", row.Value);
        Assert.True(row.IsChecked);
    }

    /// <summary>
    /// The one that stops the support ticket. On, with nothing on screen, and the row says why:
    /// the game is not up. Without it the only reading available is that the overlay is broken.
    /// </summary>
    [Fact]
    public void Switched_on_but_not_drawing_the_row_says_why()
    {
        var row = Find(
            TrayMenu.Build(Empty with { OverlayEnabled = true, OverlayHint = "waiting for game" }),
            TrayCommand.ToggleOverlay);

        Assert.NotNull(row);
        Assert.Equal("waiting for game", row.Value);
        Assert.True(row.IsChecked);
    }

    [Fact]
    public void There_is_no_check_for_updates_row_before_the_checker_exists()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.CheckForUpdates));
    }

    [Fact]
    public void With_nothing_on_offer_the_row_offers_a_check_and_names_the_running_build()
    {
        var row = Find(
            TrayMenu.Build(Empty with { UpdateCheckAvailable = true, RunningVersion = "1.4.0" }),
            TrayCommand.CheckForUpdates);

        Assert.NotNull(row);
        Assert.Equal("Check for updates", row.Text);
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

        Assert.Null(Find(items, TrayCommand.CheckForUpdates));
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

        Assert.Null(Find(items, TrayCommand.CheckForUpdates));
        Assert.NotNull(Find(items, TrayCommand.InstallUpdate));
    }
}
