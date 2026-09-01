using WarCommand.Agent.Core.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The tray menu's rules, with no message loop and no icon. Everything the WinForms renderer does
/// is mechanical, so this is where the menu is actually developed.
/// </summary>
public class TrayMenuTests
{
    private static readonly TrayMenuState Empty = new();

    private static IEnumerable<string> TextsOf(IEnumerable<TrayMenuItem> items) =>
        items.Where(i => !i.IsSeparator).Select(i => i.Text);

    private static TrayMenuItem? Find(IEnumerable<TrayMenuItem> items, TrayCommand command) =>
        items.FirstOrDefault(i => i.Command == command)
        ?? items.SelectMany(i => i.Children).FirstOrDefault(i => i.Command == command);

    [Fact]
    public void An_empty_state_still_builds_a_usable_menu()
    {
        var items = TrayMenu.Build(Empty);

        // The title row carries the state as a coloured dot, not as a word: TrayMenu.Header is the
        // tooltip's text, and the mock's first row is the product name alone.
        Assert.Equal("WarCommand", items[0].Text);
        Assert.True(items[0].IsTitle);
        Assert.False(items[0].IsEnabled);
        Assert.NotNull(Find(items, TrayCommand.ToggleSecondScreen));
        Assert.NotNull(Find(items, TrayCommand.Quit));
        Assert.Equal(TrayCommand.Quit, items[^1].Command);
    }

    [Theory]
    [InlineData(TrayIndicator.Connected, "WarCommand (connected)")]
    [InlineData(TrayIndicator.Reconnecting, "WarCommand (reconnecting)")]
    [InlineData(TrayIndicator.Offline, "WarCommand (not connected)")]
    public void The_header_names_the_connection_state(TrayIndicator indicator, string expected) =>
        Assert.Equal(expected, TrayMenu.Header(Empty with { Indicator = indicator }));

    [Fact]
    public void Panic_wins_the_header_over_a_connected_socket() =>
        Assert.Equal(
            "WarCommand (panic engaged)",
            TrayMenu.Header(Empty with { Indicator = TrayIndicator.Connected, PanicEngaged = true }));

    [Fact]
    public void A_section_with_no_data_does_not_render()
    {
        var texts = TextsOf(TrayMenu.Build(Empty)).ToList();

        Assert.DoesNotContain(texts, t => t.StartsWith("Board:", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Match:", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Map:", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Microphone:", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Push to talk:", StringComparison.Ordinal));
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.OpenSettings));
    }

    [Fact]
    public void No_two_separators_ever_touch_and_none_leads_or_trails()
    {
        var items = TrayMenu.Build(Empty);

        Assert.False(items[0].IsSeparator);
        Assert.False(items[^1].IsSeparator);
        for (var i = 1; i < items.Count; i++)
        {
            Assert.False(items[i].IsSeparator && items[i - 1].IsSeparator);
        }
    }

    [Fact]
    public void Panic_is_absent_until_the_switch_is_armed()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.TogglePanic));

        var armed = TrayMenu.Build(Empty with { PanicArmed = true, PanicChordLabel = "RightAlt+P" });
        var panic = Find(armed, TrayCommand.TogglePanic);

        Assert.NotNull(panic);
        Assert.Equal("Panic (RightAlt+P)", panic.Text);
    }

    [Fact]
    public void Restart_match_is_absent_for_a_member_and_present_for_an_admin()
    {
        var match = Empty with { MatchName = "ALPHA", MatchPeopleCount = 31 };

        Assert.Null(Find(TrayMenu.Build(match), TrayCommand.RestartMatch));
        Assert.NotNull(Find(TrayMenu.Build(match with { CanRestartMatch = true }), TrayCommand.RestartMatch));
    }

    [Fact]
    public void Restart_match_is_top_level_rather_than_inside_the_match_submenu()
    {
        var items = TrayMenu.Build(Empty with { MatchName = "ALPHA", CanRestartMatch = true });

        Assert.Contains(items, i => i.Command == TrayCommand.RestartMatch);
    }

    [Fact]
    public void The_pairing_rows_show_only_while_unpaired()
    {
        var unpaired = TrayMenu.Build(Empty with { PairingCode = "K4M2-9XPT" });
        Assert.NotNull(Find(unpaired, TrayCommand.CopyPairingCode));
        Assert.NotNull(Find(unpaired, TrayCommand.EnterPairingCode));

        var paired = TrayMenu.Build(Empty with { IsPaired = true, PairingCode = "K4M2-9XPT" });
        Assert.Null(Find(paired, TrayCommand.CopyPairingCode));
        Assert.Null(Find(paired, TrayCommand.EnterPairingCode));
    }

    [Fact]
    public void The_board_line_reads_open_and_yours()
    {
        var items = TrayMenu.Build(Empty with { OpenRequestCount = 4, MyRequestCount = 1 });

        Assert.Contains("Requests: 4 open, 1 yours", TextsOf(items));
    }

    [Fact]
    public void A_toggle_carries_its_state_as_a_value_rather_than_a_checkmark()
    {
        var off = Find(TrayMenu.Build(Empty), TrayCommand.ToggleSecondScreen);
        var on = Find(TrayMenu.Build(Empty with { SecondScreenVisible = true }), TrayCommand.ToggleSecondScreen);

        Assert.Equal("off", off!.Value);
        Assert.Equal("on", on!.Value);
    }

    [Fact]
    public void An_unpaired_agent_says_it_is_not_set_up()
    {
        Assert.Contains("Not set up", TextsOf(TrayMenu.Build(Empty)));
        Assert.DoesNotContain("Not set up", TextsOf(TrayMenu.Build(Empty with { IsPaired = true })));
    }

    [Fact]
    public void The_match_entity_is_called_a_deployment_never_a_match()
    {
        var texts = TextsOf(TrayMenu.Build(Empty with
        {
            MatchName = "ALPHA",
            MatchPeopleCount = 31,
            CanRestartMatch = true,
        })).ToList();

        Assert.Contains("Deployment: ALPHA", texts);
        Assert.Contains("Restart deployment", texts);
        Assert.DoesNotContain(texts, t => t.Contains("Match", StringComparison.Ordinal));
    }

    [Fact]
    public void Second_screen_mode_carries_its_own_check_state()
    {
        Assert.False(Find(TrayMenu.Build(Empty), TrayCommand.ToggleSecondScreen)!.IsChecked);
        Assert.True(Find(TrayMenu.Build(Empty with { SecondScreenVisible = true }), TrayCommand.ToggleSecondScreen)!.IsChecked);
    }

    [Fact]
    public void Screen_capture_renders_off_by_default_and_only_when_the_subsystem_exists()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.ToggleScreenCapture));

        var capture = Find(TrayMenu.Build(Empty with { ScreenCaptureEnabled = false }), TrayCommand.ToggleScreenCapture);
        Assert.NotNull(capture);
        Assert.False(capture.IsChecked);
    }

    [Fact]
    public void The_dev_force_state_section_is_dev_only()
    {
        Assert.Null(Find(TrayMenu.Build(Empty), TrayCommand.DevForceConnected));

        var dev = TrayMenu.Build(Empty with { IsDev = true });
        Assert.NotNull(Find(dev, TrayCommand.DevForceConnected));
        Assert.NotNull(Find(dev, TrayCommand.DevForceReconnecting));
        Assert.NotNull(Find(dev, TrayCommand.DevForceOffline));
    }

    [Fact]
    public void The_dev_section_checks_the_state_the_icon_is_actually_showing()
    {
        var dev = TrayMenu.Build(Empty with { IsDev = true, Indicator = TrayIndicator.Reconnecting });

        Assert.True(Find(dev, TrayCommand.DevForceReconnecting)!.IsChecked);
        Assert.False(Find(dev, TrayCommand.DevForceConnected)!.IsChecked);
    }

    [Fact]
    public void Every_rendered_row_is_a_label_a_parent_or_a_command()
    {
        var full = new TrayMenuState
        {
            Indicator = TrayIndicator.Connected,
            GroupName = "Task Force Bravo",
            GroupMemberCount = 12,
            MatchName = "ALPHA",
            MatchPeopleCount = 31,
            CanRestartMatch = true,
            OpenRequestCount = 4,
            MyRequestCount = 1,
            MapName = "Bakurani",
            MapIsAuto = true,
            MicrophoneName = "Yeti Nano",
            PushToTalkLabel = "Mouse5",
            SoundOutputName = "Speakers",
            ScreenCaptureEnabled = false,
            SoundsEnabled = true,
            PairingCode = "K4M2-9XPT",
            SettingsAvailable = true,
            PanicArmed = true,
            PanicChordLabel = "RightAlt+P",
            IsDev = true,
        };

        foreach (var item in TrayMenu.Build(full).Where(i => !i.IsSeparator))
        {
            Assert.True(
                item.Command is not TrayCommand.None || item.Children.Count > 0 || !item.IsEnabled,
                $"'{item.Text}' is an enabled leaf that raises nothing.");
        }
    }
}
