using WarCommand.Agent.Core.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>The two rows a well-behaved tray app is judged on: autostart, and being out of date.</summary>
public class TrayMenuUpdateTests
{
    private static IEnumerable<TrayMenuItem> Flatten(IEnumerable<TrayMenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children))
            {
                yield return child;
            }
        }
    }

    private static TrayMenuItem? Find(TrayMenuState state, Func<TrayMenuItem, bool> match) =>
        Flatten(TrayMenu.Build(state)).FirstOrDefault(match);

    // --- start with Windows ---------------------------------------------------------------------

    [Fact]
    public void The_startup_row_is_absent_until_the_state_carries_it()
    {
        Assert.Null(Find(new TrayMenuState(), i => i.Text == "Start with Windows"));
    }

    [Theory]
    [InlineData(true, "on")]
    [InlineData(false, "off")]
    public void The_startup_row_shows_the_registry_state_as_on_or_off(bool enabled, string expected)
    {
        var row = Find(new TrayMenuState { StartWithWindows = enabled }, i => i.Text == "Start with Windows");
        Assert.NotNull(row);
        Assert.Equal(expected, row!.Value);
        Assert.Equal(enabled, row.IsChecked);
        Assert.Equal(TrayCommand.ToggleStartWithWindows, row.Command);
    }

    // --- updates --------------------------------------------------------------------------------

    [Fact]
    public void No_update_renders_no_row()
    {
        Assert.Null(Find(new TrayMenuState(), i => i.Text.StartsWith("Update", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_available_update_is_clickable_and_says_it_restarts()
    {
        var row = Find(new TrayMenuState { UpdateVersion = "1.4.0" }, i => i.Text == "Update to 1.4.0");
        Assert.NotNull(row);
        Assert.True(row!.IsEnabled);
        Assert.Equal(TrayCommand.InstallUpdate, row.Command);
        Assert.Equal("restarts", row.Value);
    }

    [Fact]
    public void An_update_held_by_the_game_says_why_rather_than_greying_out_silently()
    {
        var state = new TrayMenuState { UpdateVersion = "1.4.0", UpdateWaitingForGameToClose = true };
        var row = Find(state, i => i.Text == "Update to 1.4.0");
        Assert.NotNull(row);
        Assert.False(row!.IsEnabled);
        Assert.Equal("on next launch", row.Value);
        Assert.Equal(TrayCommand.None, row.Command);
    }

    [Fact]
    public void An_update_in_flight_cannot_be_started_a_second_time()
    {
        var state = new TrayMenuState { UpdateVersion = "1.4.0", UpdateInProgress = true };
        var row = Find(state, i => i.Text.StartsWith("Updating to", StringComparison.Ordinal));
        Assert.NotNull(row);
        Assert.False(row!.IsEnabled);
        Assert.Equal(TrayCommand.None, row.Command);
        Assert.Null(Find(state, i => i.Command == TrayCommand.InstallUpdate));
    }

    [Fact]
    public void The_update_row_sits_above_the_account_line()
    {
        var rows = TrayMenu.Build(new TrayMenuState { UpdateVersion = "1.4.0", Callsign = "Wraith" }).ToList();
        var update = rows.FindIndex(i => i.Text == "Update to 1.4.0");
        var account = rows.FindIndex(i => i.Text == "Signed in as Wraith");
        Assert.True(update >= 0 && account >= 0);
        Assert.True(update < account, "the update row must be visible above the account line");
    }
}
