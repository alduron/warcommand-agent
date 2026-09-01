using WarCommand.Agent.Input;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// What finding and losing the game window has to raise. Losing it disables hotkeys except Panic,
/// hides the overlay rather than dimming it, and reports idle on the next heartbeat.
/// </summary>
public class GameWindowTrackerTests
{
    private static readonly GameWindowScan Windowed =
        new(Handle: 1, new ScreenRect(0, 0, 2560, 1440), IsForeground: true, ExclusiveFullscreen: false);

    [Fact]
    public void Finding_the_window_enables_hotkeys_and_shows_the_overlay()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed);

        Assert.Equal(1, sink.Found);
        Assert.Equal([true], sink.HotkeyStates);
        Assert.Equal([OverlayVisibility.Show], sink.Visibilities);
        Assert.Equal([PresenceState.Active], sink.PresenceStates);
        Assert.True(tracker.GameIsRunning);
        Assert.True(tracker.GameIsForeground);
    }

    [Fact]
    public void Losing_the_window_disables_hotkeys_hides_the_overlay_and_reports_idle()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed);
        sink.Clear();
        tracker.Observe(null);

        Assert.Equal(1, sink.Lost);
        Assert.Equal([false], sink.HotkeyStates);
        Assert.Equal([OverlayVisibility.Hide], sink.Visibilities);
        Assert.Equal([PresenceState.Idle], sink.PresenceStates);
        Assert.False(tracker.GameIsRunning);
        Assert.False(tracker.GameIsForeground);
    }

    [Fact]
    public void Hide_is_the_default_and_dim_is_the_opt_in()
    {
        var hideSink = new RecordingSink();
        var hide = new GameWindowTracker(hideSink);
        hide.Observe(Windowed with { IsForeground = false });
        Assert.Equal([OverlayVisibility.Hide], hideSink.Visibilities);

        var dimSink = new RecordingSink();
        var dim = new GameWindowTracker(dimSink, OverlayFocusBehavior.Dim);
        dim.Observe(Windowed with { IsForeground = false });
        Assert.Equal([OverlayVisibility.Dim], dimSink.Visibilities);
    }

    [Fact]
    public void A_first_observation_with_no_game_reports_idle_and_no_hotkeys()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(null);

        Assert.Equal(0, sink.Lost);
        Assert.Equal([false], sink.HotkeyStates);
        Assert.Equal([PresenceState.Idle], sink.PresenceStates);
    }

    [Fact]
    public void A_steady_absence_raises_nothing_twice()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(null);
        sink.Clear();
        tracker.Observe(null);
        tracker.Observe(null);

        Assert.Empty(sink.HotkeyStates);
        Assert.Empty(sink.PresenceStates);
    }

    [Fact]
    public void A_moved_client_rect_is_raised_for_the_overlay()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed);
        sink.Clear();
        tracker.Observe(Windowed with { ClientRect = new ScreenRect(100, 50, 1920, 1080) });

        Assert.Equal([new ScreenRect(100, 50, 1920, 1080)], sink.Rects);
    }

    [Fact]
    public void A_minimized_window_does_not_move_the_overlay()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed);
        sink.Clear();
        tracker.Observe(Windowed with { ClientRect = ScreenRect.Empty });

        Assert.Empty(sink.Rects);
    }

    [Fact]
    public void Exclusive_fullscreen_is_raised_once_per_transition()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed with { ExclusiveFullscreen = true });
        tracker.Observe(Windowed with { ExclusiveFullscreen = true });
        Assert.Equal(1, sink.ExclusiveFullscreens);

        tracker.Observe(Windowed);
        tracker.Observe(Windowed with { ExclusiveFullscreen = true });
        Assert.Equal(2, sink.ExclusiveFullscreens);
    }

    [Fact]
    public void Alt_tabbing_away_hides_the_overlay_without_losing_the_window()
    {
        var sink = new RecordingSink();
        var tracker = new GameWindowTracker(sink);

        tracker.Observe(Windowed);
        sink.Clear();
        tracker.Observe(Windowed with { IsForeground = false });

        Assert.Equal(0, sink.Lost);
        Assert.Empty(sink.PresenceStates);
        Assert.Equal([OverlayVisibility.Hide], sink.Visibilities);
        Assert.True(tracker.GameIsRunning);
        Assert.False(tracker.GameIsForeground);
    }

    private sealed class RecordingSink : IGameWindowSink
    {
        internal int Found { get; private set; }

        internal int Lost { get; private set; }

        internal int ExclusiveFullscreens { get; private set; }

        internal List<ScreenRect> Rects { get; } = [];

        internal List<bool> HotkeyStates { get; } = [];

        internal List<OverlayVisibility> Visibilities { get; } = [];

        internal List<PresenceState> PresenceStates { get; } = [];

        internal void Clear()
        {
            Found = 0;
            Lost = 0;
            ExclusiveFullscreens = 0;
            Rects.Clear();
            HotkeyStates.Clear();
            Visibilities.Clear();
            PresenceStates.Clear();
        }

        public void GameWindowFound(GameWindowScan window) => Found++;

        public void ClientRectChanged(ScreenRect clientRect) => Rects.Add(clientRect);

        public void ExclusiveFullscreenDetected() => ExclusiveFullscreens++;

        public void GameWindowLost() => Lost++;

        public void HotkeysEnabled(bool enabled) => HotkeyStates.Add(enabled);

        public void OverlayVisibilityChanged(OverlayVisibility visibility) => Visibilities.Add(visibility);

        public void PresenceStateChanged(PresenceState state) => PresenceStates.Add(state);
    }
}
