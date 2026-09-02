using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// Where the overlay lands inside the game's client rect. Pure arithmetic, so every anchor is
/// checked here rather than by looking at a screenshot.
/// </summary>
public class OverlayLayoutTests
{
    /// <summary>1080p, windowed at the origin. The rect every case below places against.</summary>
    private static readonly ScreenRect Game = new(0, 0, 1920, 1080);

    [Fact]
    public void Right_is_against_the_right_edge_and_vertically_centred()
    {
        var placed = OverlayLayout.Place(Game, OverlayAnchor.Right, 380);

        Assert.Equal(1920 - 380 - OverlayLayout.Margin, placed.Left);
        Assert.Equal((1080 - placed.Height) / 2, placed.Top);
        Assert.Equal(380, placed.Width);
    }

    [Fact]
    public void Left_is_against_the_left_edge()
    {
        var placed = OverlayLayout.Place(Game, OverlayAnchor.Left, 380);

        Assert.Equal(OverlayLayout.Margin, placed.Left);
    }

    [Fact]
    public void Top_right_and_bottom_right_share_an_edge_and_differ_only_in_height()
    {
        var top = OverlayLayout.Place(Game, OverlayAnchor.TopRight, 380);
        var bottom = OverlayLayout.Place(Game, OverlayAnchor.BottomRight, 380);

        Assert.Equal(top.Left, bottom.Left);
        Assert.Equal(OverlayLayout.Margin, top.Top);
        Assert.Equal(1080 - bottom.Height - OverlayLayout.Margin, bottom.Top);
    }

    /// <summary>
    /// The one that puts the panel off the picture. A 560 px panel in a 640 px windowed launch has
    /// to shrink, because a placement that starts at a negative left draws on the desktop.
    /// </summary>
    [Fact]
    public void A_panel_wider_than_the_game_is_clamped_inside_it()
    {
        var small = new ScreenRect(100, 100, 640, 480);
        var placed = OverlayLayout.Place(small, OverlayAnchor.Right, 560);

        Assert.True(placed.Left >= small.Left);
        Assert.True(placed.Left + placed.Width <= small.Left + small.Width);
    }

    /// <summary>It places against the GAME, never the monitor. A game at an offset moves with it.</summary>
    [Fact]
    public void It_follows_the_game_rect_rather_than_the_desktop()
    {
        var offset = new ScreenRect(2560, 300, 1280, 720);
        var placed = OverlayLayout.Place(offset, OverlayAnchor.Right, 380);

        Assert.Equal(2560 + 1280 - 380 - OverlayLayout.Margin, placed.Left);
        Assert.True(placed.Top >= 300);
    }

    [Fact]
    public void No_game_window_places_nothing()
    {
        Assert.True(OverlayLayout.Place(ScreenRect.Empty, OverlayAnchor.Right, 380).IsEmpty);
    }

    /// <summary>The overflow row carries the rest. The panel never becomes the screen.</summary>
    [Fact]
    public void It_never_takes_more_than_the_height_fraction()
    {
        var placed = OverlayLayout.Place(Game, OverlayAnchor.Right, 380);

        Assert.True(placed.Height <= (int)(1080 * OverlayLayout.MaxHeightFraction));
    }
}
