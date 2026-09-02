using System.Linq;
using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Core.Tray;

/// <summary>
/// What the tray icon's field colour is saying. Mapped from the realtime socket's state by the
/// composition root; the menu model never sees a socket.
/// </summary>
public enum TrayIndicator
{
    /// <summary>Grey. Panicked, unpaired, or the socket is not up.</summary>
    Offline = 0,

    /// <summary>Amber. Backing off and retrying.</summary>
    Reconnecting,

    /// <summary>Green. Ready has landed.</summary>
    Connected,
}

/// <summary>
/// Every action the tray menu can raise. The renderer carries one of these per item and the
/// composition root switches on it, so adding an item is one row in <see cref="TrayMenu.Build"/>
/// plus one case, never a new event.
/// </summary>
public enum TrayCommand
{
    /// <summary>A label or a submenu parent. Raises nothing.</summary>
    None = 0,

    OpenWebBoard,
    SwitchMatch,
    RestartMatch,
    EndMatch,
    SelectMap,
    SelectMicrophone,
    RebindPushToTalk,
    TestPushToTalk,
    ToggleScreenCapture,
    ToggleSounds,
    ToggleStartWithWindows,
    /// <summary>Argument is an <c>OverlayMode</c> name: AlwaysOn, MirrorGame, Hidden.</summary>
    SelectOverlayMode,

    /// <summary>Argument is a Windows display device name, or null for the primary.</summary>
    SelectOverlayDisplay,

    CheckForUpdates,
    InstallUpdate,
    SelectSoundOutput,
    ToggleSecondScreen,
    CopyPairingCode,
    EnterPairingCode,
    OpenSettings,
    TogglePanic,
    Quit,

    /// <summary>Dev profile only: forces the icon to a state with no socket. See DEVELOPING.md.</summary>
    DevForceConnected,

    /// <summary>Dev profile only.</summary>
    DevForceReconnecting,

    /// <summary>Dev profile only.</summary>
    DevForceOffline,
}

/// <summary>One monitor the overlay can be put on.</summary>
/// <remarks>
/// The device name is what is persisted and the label is what a person can recognise: nobody
/// knows which panel is \.\DISPLAY2, and every monitor reports itself as Generic PnP Monitor.
/// </remarks>
public sealed record TrayDisplay(string DeviceName, string Label, bool IsPrimary);

/// <summary>One row. A separator, a label, a command, or a parent holding children.</summary>
public sealed record TrayMenuItem
{
    /// <summary>The rendered text. Ignored for a separator.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Right-aligned secondary text, in the mock's dim grey: '31 people', 'off', 'admin+'. This is
    /// how the design shows a toggle's state, rather than a checkmark.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>The dim 12px heading row from the mock: 'Requests: 4 open, 1 yours', 'Not set up'.</summary>
    public bool IsHeading { get; init; }

    /// <summary>The first row, which carries the connection dot beside the product name.</summary>
    public bool IsTitle { get; init; }

    /// <summary>What clicking it raises. <see cref="TrayCommand.None"/> for labels and parents.</summary>
    public TrayCommand Command { get; init; } = TrayCommand.None;

    /// <summary>
    /// Which one. A submenu of monitors or of overlay modes is one command over many rows, and
    /// without a payload each row would need a command of its own and the enum would grow with
    /// the number of monitors somebody owns.
    /// </summary>
    public string? Argument { get; init; }

    /// <summary>False renders greyed. Prefer omitting the row: a control you cannot use is noise.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Null when the row is not a toggle. True or false renders the check.</summary>
    public bool? IsChecked { get; init; }

    /// <summary>Submenu rows. Empty for a leaf.</summary>
    public IReadOnlyList<TrayMenuItem> Children { get; init; } = [];

    /// <summary>True for a horizontal rule.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>The one separator instance. Adjacent ones are collapsed by <see cref="TrayMenu.Build"/>.</summary>
    public static TrayMenuItem Separator { get; } = new() { Text = "-", IsSeparator = true };
}

/// <summary>
/// Everything the menu renders, as plain data. Nothing here is a live object: the composition root
/// snapshots it each time the menu opens, so a section whose subsystem is not wired up yet simply
/// leaves its fields null and the section does not render.
/// </summary>
/// <remarks>
/// This is why the menu can be developed against no API at all. See <c>WARCOMMAND_TRAY_ONLY</c> in
/// DEVELOPING.md: an empty state plus the dev section is a complete, launchable menu.
/// </remarks>
public sealed record TrayMenuState
{
    /// <summary>The field colour, which is also the header's state word.</summary>
    public TrayIndicator Indicator { get; init; } = TrayIndicator.Offline;

    /// <summary>True while Panic is engaged. Overrides <see cref="Indicator"/> in the header.</summary>
    public bool PanicEngaged { get; init; }

    /// <summary>False until every <c>PanicSubsystem</c> is registered. The row is absent until then.</summary>
    public bool PanicArmed { get; init; }

    /// <summary>Rendered in the Panic row, e.g. "RightAlt+P". Null hides the chord.</summary>
    public string? PanicChordLabel { get; init; }

    /// <summary>Null until a membership is known. Hides the whole group section.</summary>
    public string? GroupName { get; init; }

    /// <summary>Members in the group, rendered beside the name.</summary>
    public int GroupMemberCount { get; init; }

    /// <summary>Null when there is no live deployment. Hides the match rows.</summary>
    public string? MatchName { get; init; }

    /// <summary>People on the match.</summary>
    public int MatchPeopleCount { get; init; }

    /// <summary>Admin and owner only. Absent rather than greyed for a member; see 10-agent-spec.md.</summary>
    public bool CanRestartMatch { get; init; }

    /// <summary>Open rows on the board. Null hides the board line.</summary>
    public int? OpenRequestCount { get; init; }

    /// <summary>Of the open rows, how many are the viewer's own.</summary>
    public int MyRequestCount { get; init; }

    /// <summary>Null hides the map row.</summary>
    public string? MapName { get; init; }

    /// <summary>True when the map came from detection rather than a manual pick.</summary>
    public bool MapIsAuto { get; init; }

    /// <summary>Null until capture is wired up. Hides the microphone row.</summary>
    public string? MicrophoneName { get; init; }

    /// <summary>Null until a PTT binding exists. Hides the push-to-talk row.</summary>
    public string? PushToTalkLabel { get; init; }

    /// <summary>Null until an output device is chosen. Hides the sound output row.</summary>
    public string? SoundOutputName { get; init; }

    /// <summary>Opt-in, off by default. Null hides the toggle entirely.</summary>
    public bool? ScreenCaptureEnabled { get; init; }

    /// <summary>Null hides the toggle.</summary>
    public bool? SoundsEnabled { get; init; }

    /// <summary>Whether second-screen mode's window is showing.</summary>
    public bool SecondScreenVisible { get; init; }

    /// <summary>
    /// Always on, mirroring the game, or hidden. Null until the overlay subsystem exists, which
    /// hides the row rather than offering a control over nothing.
    /// </summary>
    public string? OverlayMode { get; init; }

    /// <summary>
    /// Why the overlay is not on screen while it is switched on: "waiting for game", "game not
    /// focused". Null when it is drawing, which renders the row as a plain on.
    /// </summary>
    /// <remarks>
    /// Without this the row says on while nothing is visible, and the only conclusion available to
    /// the user is that the overlay is broken. It usually is not: the game is not up yet.
    /// </remarks>
    public string? OverlayHint { get; init; }

    /// <summary>
    /// The monitors to choose between, in order, as (device name, label). Empty hides the row,
    /// which is right when there is only one monitor to be on.
    /// </summary>
    public IReadOnlyList<TrayDisplay> Displays { get; init; } = [];

    /// <summary>The device name currently chosen. Null means the primary.</summary>
    public string? OverlayDisplayDeviceName { get; init; }

    /// <summary>False until the update checker exists. Hides the manual check row.</summary>
    public bool UpdateCheckAvailable { get; init; }

    /// <summary>True while a manual check is in flight, so the row cannot be clicked twice.</summary>
    public bool UpdateCheckInProgress { get; init; }

    /// <summary>The running build, e.g. "1.4.0". Rendered beside the manual check row.</summary>
    public string? RunningVersion { get; init; }

    /// <summary>Set only in unpaired mode. Both pairing rows are absent once paired.</summary>
    public string? PairingCode { get; init; }

    /// <summary>True once the device holds tokens. Hides the pairing rows.</summary>
    public bool IsPaired { get; init; }

    /// <summary>The account the agent holds. Null until it holds one.</summary>
    public string? Callsign { get; init; }

    /// <summary>False until the settings window exists. The row is absent, never greyed.</summary>
    public bool SettingsAvailable { get; init; }

    /// <summary>Read from the HKCU Run key, never from settings.json. Null hides the row.</summary>
    public bool? StartWithWindows { get; init; }

    /// <summary>The version on offer, e.g. "1.4.0". Null when the agent is current.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary>True while the game is up, which defers the install rather than cancelling it.</summary>
    public bool UpdateWaitingForGameToClose { get; init; }

    /// <summary>True from the click until the process exits, so the row cannot be clicked twice.</summary>
    public bool UpdateInProgress { get; init; }

    /// <summary>Dev profile. Adds the force-state section and nothing else.</summary>
    public bool IsDev { get; init; }
}

/// <summary>
/// Builds the tray menu from <see cref="TrayMenuState"/>, following the tree in 10-agent-spec.md.
/// Pure and platform-free so the whole menu is unit-testable with no message loop; the WinForms
/// <c>ContextMenuStrip</c> is a renderer over this and holds no rules of its own.
/// </summary>
/// <remarks>
/// A section whose data is absent does not render, and the separators around it collapse with it.
/// That is what lets the menu ship before speech, capture, hotkeys or the settings window exist:
/// each one arrives as a filled-in state field rather than a rewrite of this method.
/// </remarks>
public static class TrayMenu
{
    /// <summary>Builds the rows top to bottom.</summary>
    public static IReadOnlyList<TrayMenuItem> Build(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var items = new List<TrayMenuItem>
        {
            // The dot beside the name is the colour; the word beside it is the same thing said out
            // loud, because a colour alone cannot distinguish "not connected" from "not signed in".
            new() { Text = "WarCommand", Value = StatusWord(state), IsTitle = true, IsEnabled = false },
            TrayMenuItem.Separator,
        };

        if (!state.IsPaired && state.GroupName is null)
        {
            items.Add(new TrayMenuItem { Text = "Not set up", IsHeading = true, IsEnabled = false });
            items.Add(TrayMenuItem.Separator);
        }

        AppendDeploymentSection(items, state);
        AppendAudioSection(items, state);
        AppendToggleSection(items, state);
        AppendControlSection(items, state);
        AppendDevSection(items, state);

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem { Text = "Quit", Command = TrayCommand.Quit });

        return Collapse(items);
    }

    /// <summary>
    /// The status word beside the product name. Panic wins over everything, and an agent that is
    /// connected but holds no account says so: those are different problems with different fixes.
    /// </summary>
    public static string StatusWord(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.PanicEngaged)
        {
            return "panic engaged";
        }

        return state.Indicator switch
        {
            TrayIndicator.Connected => state.IsPaired ? "connected" : "not signed in",
            TrayIndicator.Reconnecting => "reconnecting",
            _ => "not connected",
        };
    }

    /// <summary>The header line. Panic wins over the socket state, exactly as the icon does.</summary>
    public static string Header(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var word = state.PanicEngaged
            ? "panic engaged"
            : state.Indicator switch
            {
                TrayIndicator.Connected => "connected",
                TrayIndicator.Reconnecting => "reconnecting",
                _ => "not connected",
            };

        return $"WarCommand ({word})";
    }

    private static void AppendDeploymentSection(List<TrayMenuItem> items, TrayMenuState state)
    {
        if (state.GroupName is { } group)
        {
            items.Add(new TrayMenuItem
            {
                Text = $"{group}  ({state.GroupMemberCount})",
                Children = [new TrayMenuItem { Text = "Open web board", Command = TrayCommand.OpenWebBoard }],
            });
        }

        if (state.OpenRequestCount is { } open)
        {
            items.Add(new TrayMenuItem
            {
                Text = $"Requests: {open} open, {state.MyRequestCount} yours",
                IsHeading = true,
                IsEnabled = false,
            });
        }

        // Deployment, never Match: the entity is a Deployment everywhere else in the product.
        if (state.MatchName is { } deployment)
        {
            items.Add(new TrayMenuItem
            {
                Text = $"Deployment: {deployment}",
                Value = $"{state.MatchPeopleCount} people",
                Children =
                [
                    new TrayMenuItem { Text = "Switch deployment...", Command = TrayCommand.SwitchMatch },
                    new TrayMenuItem { Text = "End deployment", Command = TrayCommand.EndMatch },
                ],
            });

            // Top level because it happens every round, and absent rather than greyed for a member.
            if (state.CanRestartMatch)
            {
                items.Add(new TrayMenuItem
                {
                    Text = "Restart deployment",
                    Value = "admin+",
                    Command = TrayCommand.RestartMatch,
                });
            }
        }

        if (state.MapName is { } map)
        {
            items.Add(new TrayMenuItem
            {
                Text = $"Map: {map}",
                Value = state.MapIsAuto ? "auto" : null,
                Command = TrayCommand.SelectMap,
            });
        }

        items.Add(TrayMenuItem.Separator);
    }

    private static void AppendAudioSection(List<TrayMenuItem> items, TrayMenuState state)
    {
        // The two things that break, and the support ticket this product will receive most. One
        // click from the icon, never inside Settings.
        if (state.MicrophoneName is { } microphone)
        {
            items.Add(new TrayMenuItem { Text = $"Microphone: {microphone}", Command = TrayCommand.SelectMicrophone });
        }

        if (state.PushToTalkLabel is { } ptt)
        {
            items.Add(new TrayMenuItem
            {
                Text = $"Push to talk: {ptt}",
                Children =
                [
                    new TrayMenuItem { Text = "Rebind...", Command = TrayCommand.RebindPushToTalk },
                    new TrayMenuItem { Text = "Test", Command = TrayCommand.TestPushToTalk },
                ],
            });
        }

        items.Add(TrayMenuItem.Separator);
    }

    private static void AppendToggleSection(List<TrayMenuItem> items, TrayMenuState state)
    {
        // The mock shows a toggle's state as right-aligned 'on' or 'off', never as a checkmark.
        if (state.ScreenCaptureEnabled is { } capture)
        {
            items.Add(new TrayMenuItem
            {
                Text = "Screen capture",
                Value = OnOff(capture),
                Command = TrayCommand.ToggleScreenCapture,
                IsChecked = capture,
            });
        }

        if (state.SoundsEnabled is { } sounds)
        {
            items.Add(new TrayMenuItem
            {
                Text = "Sounds",
                Value = OnOff(sounds),
                Command = TrayCommand.ToggleSounds,
                IsChecked = sounds,
            });
        }

        if (state.SoundOutputName is { } output)
        {
            items.Add(new TrayMenuItem { Text = $"Sound output: {output}", Command = TrayCommand.SelectSoundOutput });
        }

        // What every tray app has, in the place users look for it. Absent in a dev launch: a
        // developer's machine must not start an agent they did not ask for.
        if (state.StartWithWindows is { } startup)
        {
            items.Add(new TrayMenuItem
            {
                Text = "Start with Windows",
                Value = OnOff(startup),
                Command = TrayCommand.ToggleStartWithWindows,
                IsChecked = startup,
            });
        }

        AppendOverlaySection(items, state);

        // Not "second-screen mode": that named a mode nobody could define. It is the app's own
        // window, the same one Settings opens, on its Board tab.
        items.Add(new TrayMenuItem
        {
            Text = "Board window",
            Value = OnOff(state.SecondScreenVisible),
            Command = TrayCommand.ToggleSecondScreen,
            IsChecked = state.SecondScreenVisible,
        });

        items.Add(TrayMenuItem.Separator);
    }

    /// <summary>
    /// The overlay's two rows: which mode, and on which monitor. The monitor row is absent while
    /// mirroring the game, because mirroring puts the board on the game's screen by definition and
    /// a monitor picker there would be a control that does nothing.
    /// </summary>
    private static void AppendOverlaySection(List<TrayMenuItem> items, TrayMenuState state)
    {
        if (state.OverlayMode is not { } mode)
        {
            return;
        }

        items.Add(new TrayMenuItem
        {
            Text = "Overlay",
            Value = OverlayModeLabel(mode, state.OverlayHint),
            Children =
            [
                ModeRow(mode, nameof(Settings.OverlayMode.AlwaysOn), "Always on"),
                ModeRow(mode, nameof(Settings.OverlayMode.MirrorGame), "Mirror Wardogs"),
                ModeRow(mode, nameof(Settings.OverlayMode.Hidden), "Hidden"),
            ],
        });

        if (mode == nameof(Settings.OverlayMode.MirrorGame) || state.Displays.Count <= 1)
        {
            return;
        }

        items.Add(new TrayMenuItem
        {
            Text = "Overlay display",
            Value = DisplayLabel(state),
            Children = [.. state.Displays.Select(d => new TrayMenuItem
            {
                Text = d.Label,
                Value = d.IsPrimary ? "primary" : null,
                Command = TrayCommand.SelectOverlayDisplay,
                Argument = d.DeviceName,
                IsChecked = IsChosen(state, d),
            })],
        });
    }

    private static TrayMenuItem ModeRow(string current, string mode, string text) => new()
    {
        Text = text,
        Command = TrayCommand.SelectOverlayMode,
        Argument = mode,
        IsChecked = string.Equals(current, mode, StringComparison.Ordinal),
        Value = string.Equals(current, mode, StringComparison.Ordinal) ? "on" : null,
    };

    /// <summary>
    /// What the collapsed row says. Always on and Hidden speak for themselves; mirroring reads
    /// "waiting for Wardogs" until the game is up, because otherwise the row says it is mirroring
    /// while nothing is on screen and the only conclusion available is that it is broken.
    /// </summary>
    private static string OverlayModeLabel(string mode, string? hint) => mode switch
    {
        nameof(Settings.OverlayMode.Hidden) => "hidden",
        nameof(Settings.OverlayMode.MirrorGame) => hint ?? "mirroring Wardogs",
        _ => hint ?? "always on",
    };

    private static string DisplayLabel(TrayMenuState state)
    {
        var chosen = state.Displays.FirstOrDefault(d => IsChosen(state, d));
        return chosen?.Label ?? "primary";
    }

    private static bool IsChosen(TrayMenuState state, TrayDisplay display) =>
        state.OverlayDisplayDeviceName is { } name
            ? string.Equals(display.DeviceName, name, StringComparison.Ordinal)
            : display.IsPrimary;

    private static void AppendControlSection(List<TrayMenuItem> items, TrayMenuState state)
    {
        AppendUpdateRow(items, state);

        // The account is always on screen. A user who cannot see which one the agent holds cannot
        // notice it drifting from the one their browser is signed into.
        if (state.Callsign is { } callsign)
        {
            items.Add(new TrayMenuItem { Text = $"Signed in as {callsign}", IsHeading = true, IsEnabled = false });
        }

        if (!state.IsPaired)
        {
            if (state.PairingCode is { } code)
            {
                items.Add(new TrayMenuItem { Text = $"Pairing code: {code}", Command = TrayCommand.CopyPairingCode });
            }

            items.Add(new TrayMenuItem { Text = "Enter pairing code...", Command = TrayCommand.EnterPairingCode });
        }

        if (state.SettingsAvailable)
        {
            items.Add(new TrayMenuItem { Text = "Settings...", Command = TrayCommand.OpenSettings });
        }

        // Absent until the switch is armed: a Panic row that cannot fire is worse than no row,
        // because it says the kill switch is there when it is not.
        if (state.PanicArmed)
        {
            var chord = state.PanicChordLabel is { } label ? $"Panic ({label})" : "Panic";
            items.Add(new TrayMenuItem
            {
                Text = chord,
                Value = state.PanicEngaged ? "engaged" : "active",
                Command = TrayCommand.TogglePanic,
                IsChecked = state.PanicEngaged,
            });
        }

        items.Add(TrayMenuItem.Separator);
    }

    /// <summary>
    /// One row, above the account, because an out-of-date agent is the thing most likely to be
    /// wrong and the user has to be able to see it without opening anything.
    /// </summary>
    private static void AppendUpdateRow(List<TrayMenuItem> items, TrayMenuState state)
    {
        if (state.UpdateVersion is not { } version)
        {
            // Nothing on offer. The row still has to exist, because the six-hourly check is
            // invisible and "am I on the latest build" is otherwise unanswerable from the tray.
            if (state.UpdateCheckAvailable)
            {
                items.Add(new TrayMenuItem
                {
                    Text = state.UpdateCheckInProgress ? "Checking for updates..." : "Check for updates",
                    Value = state.RunningVersion,
                    Command = state.UpdateCheckInProgress ? TrayCommand.None : TrayCommand.CheckForUpdates,
                    IsEnabled = !state.UpdateCheckInProgress,
                });
                items.Add(TrayMenuItem.Separator);
            }

            return;
        }

        if (state.UpdateInProgress)
        {
            items.Add(new TrayMenuItem { Text = $"Updating to {version}...", IsEnabled = false });
        }
        else if (state.UpdateWaitingForGameToClose)
        {
            // Not greyed with no reason given: the row says why, because "why can I not click this"
            // is the support ticket a disabled row without an explanation generates.
            items.Add(new TrayMenuItem
            {
                Text = $"Update to {version}",
                Value = "on next launch",
                IsEnabled = false,
            });
        }
        else
        {
            items.Add(new TrayMenuItem
            {
                Text = $"Update to {version}",
                Value = "restarts",
                Command = TrayCommand.InstallUpdate,
            });
        }

        items.Add(TrayMenuItem.Separator);
    }

    private static void AppendDevSection(List<TrayMenuItem> items, TrayMenuState state)
    {
        if (!state.IsDev)
        {
            return;
        }

        items.Add(new TrayMenuItem
        {
            Text = "Dev: force icon state",
            Children =
            [
                new TrayMenuItem
                {
                    Text = "Connected (green)",
                    Command = TrayCommand.DevForceConnected,
                    IsChecked = state is { PanicEngaged: false, Indicator: TrayIndicator.Connected },
                },
                new TrayMenuItem
                {
                    Text = "Reconnecting (amber)",
                    Command = TrayCommand.DevForceReconnecting,
                    IsChecked = state is { PanicEngaged: false, Indicator: TrayIndicator.Reconnecting },
                },
                new TrayMenuItem
                {
                    Text = "Offline (grey)",
                    Command = TrayCommand.DevForceOffline,
                    IsChecked = state is { PanicEngaged: false, Indicator: TrayIndicator.Offline },
                },
            ],
        });

        items.Add(TrayMenuItem.Separator);
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>Drops leading, trailing and doubled separators left behind by absent sections.</summary>
    private static List<TrayMenuItem> Collapse(List<TrayMenuItem> items)
    {
        var collapsed = new List<TrayMenuItem>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSeparator && (collapsed.Count == 0 || collapsed[^1].IsSeparator))
            {
                continue;
            }

            collapsed.Add(item);
        }

        if (collapsed.Count > 0 && collapsed[^1].IsSeparator)
        {
            collapsed.RemoveAt(collapsed.Count - 1);
        }

        return collapsed;
    }
}
