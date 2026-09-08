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

    /// <summary>The default share: 380 px at 1920, which is what the mocks draw.</summary>
    private const double Fifth = 0.20;

    private static OverlayPlacement Place(OverlayAnchor anchor, double slide = 0, double width = Fifth) =>
        OverlayLayout.Place(Game, anchor, width, slide);

    [Fact]
    public void Right_is_against_the_right_edge_and_vertically_centred()
    {
        var placed = Place(OverlayAnchor.Right).Bounds;

        Assert.Equal(1920 - 384 - OverlayLayout.Margin, placed.Left);
        Assert.Equal((1080 - placed.Height) / 2, placed.Top);
        Assert.Equal(384, placed.Width);
    }

    [Fact]
    public void Left_is_against_the_left_edge()
    {
        Assert.Equal(OverlayLayout.Margin, Place(OverlayAnchor.Left).Bounds.Left);
    }

    [Fact]
    public void Top_right_and_bottom_right_share_an_edge_and_differ_only_in_height()
    {
        var top = Place(OverlayAnchor.TopRight).Bounds;
        var bottom = Place(OverlayAnchor.BottomRight).Bounds;

        Assert.Equal(top.Left, bottom.Left);
        Assert.Equal(OverlayLayout.Margin, top.Top);
        Assert.Equal(1080 - bottom.Height - OverlayLayout.Margin, bottom.Top);
    }

    /// <summary>The nine positions are nine distinct places, not four with aliases.</summary>
    [Fact]
    public void Every_anchor_lands_somewhere_of_its_own()
    {
        var seen = Enum.GetValues<OverlayAnchor>()
            .Select(a => Place(a).Bounds)
            .Select(b => (b.Left, b.Top))
            .ToHashSet();

        Assert.Equal(Enum.GetValues<OverlayAnchor>().Length, seen.Count);
    }

    /// <summary>Positive slide travels toward the top right, whichever axis is free.</summary>
    [Theory]
    [InlineData(OverlayAnchor.Right)]
    [InlineData(OverlayAnchor.Left)]
    [InlineData(OverlayAnchor.Centre)]
    public void A_vertically_centred_anchor_slides_up_at_plus_one(OverlayAnchor anchor)
    {
        var up = Place(anchor, 1);
        var centre = Place(anchor);
        var down = Place(anchor, -1);

        Assert.Equal(OverlayLayout.Margin, up.Bounds.Top);
        Assert.Equal(1080 - down.Bounds.Height - OverlayLayout.Margin, down.Bounds.Top);
        Assert.True(up.Bounds.Top < centre.Bounds.Top && centre.Bounds.Top < down.Bounds.Top);
        Assert.Equal(centre.Bounds.Left, up.Bounds.Left);
    }

    [Theory]
    [InlineData(OverlayAnchor.Top)]
    [InlineData(OverlayAnchor.Bottom)]
    public void A_horizontally_centred_anchor_slides_right_at_plus_one(OverlayAnchor anchor)
    {
        var right = Place(anchor, 1);
        var centre = Place(anchor);
        var left = Place(anchor, -1);

        Assert.Equal(1920 - right.Bounds.Width - OverlayLayout.Margin, right.Bounds.Left);
        Assert.Equal(OverlayLayout.Margin, left.Bounds.Left);
        Assert.True(left.Bounds.Left < centre.Bounds.Left && centre.Bounds.Left < right.Bounds.Left);
        Assert.Equal(centre.Bounds.Top, right.Bounds.Top);
    }

    /// <summary>Right at +1 and Top at +1 are the same corner, which is what the sign promises.</summary>
    [Fact]
    public void Plus_one_on_either_axis_reaches_the_top_right_corner()
    {
        var fromRight = Place(OverlayAnchor.Right, 1).Bounds;
        var fromTop = Place(OverlayAnchor.Top, 1).Bounds;
        var corner = Place(OverlayAnchor.TopRight).Bounds;

        Assert.Equal(corner.Left, fromRight.Left);
        Assert.Equal(corner.Top, fromRight.Top);
        Assert.Equal(corner.Left, fromTop.Left);
        Assert.Equal(corner.Top, fromTop.Top);
    }

    /// <summary>A corner has no free axis, so the nudge is inert rather than wrong.</summary>
    [Fact]
    public void A_corner_anchor_ignores_the_slide()
    {
        var nudged = Place(OverlayAnchor.BottomLeft, -1).Bounds;
        var plain = Place(OverlayAnchor.BottomLeft).Bounds;

        Assert.Equal(plain.Left, nudged.Left);
        Assert.Equal(plain.Top, nudged.Top);
    }

    /// <summary>
    /// The bias is what puts the board on the edge: the window is the height cap, so a top anchor
    /// whose board centred inside it would draw a third of the way down the picture.
    /// </summary>
    [Fact]
    public void The_vertical_bias_follows_the_anchor_and_then_the_slide()
    {
        Assert.Equal(1.0, Place(OverlayAnchor.TopLeft).VerticalBias);
        Assert.Equal(-1.0, Place(OverlayAnchor.Bottom).VerticalBias);
        Assert.Equal(0.0, Place(OverlayAnchor.Right).VerticalBias);
        Assert.Equal(0.25, Place(OverlayAnchor.Right, 0.25).VerticalBias);
        Assert.Equal(1.0, Place(OverlayAnchor.Top, 0.25).VerticalBias);
    }

    /// <summary>Width is a share, so the same setting is the same picture on any panel.</summary>
    [Fact]
    public void Width_is_a_share_of_the_game_rather_than_a_pixel_count()
    {
        var wide = OverlayLayout.Place(new ScreenRect(0, 0, 5120, 1440), OverlayAnchor.Right, Fifth, 0);

        Assert.Equal(1024, wide.Bounds.Width);
        Assert.Equal(384, Place(OverlayAnchor.Right).Bounds.Width);
    }

    [Fact]
    public void An_out_of_range_share_is_clamped_rather_than_honoured()
    {
        Assert.Equal(
            (int)(1920 * OverlayWidth.MaxFraction),
            Place(OverlayAnchor.Right, 0, 0.9).Bounds.Width);
        Assert.Equal(
            (int)(1920 * OverlayWidth.MinFraction),
            Place(OverlayAnchor.Right, 0, 0.01).Bounds.Width);
    }

    /// <summary>
    /// The one that puts the panel off the picture. A wide share in a 640 px windowed launch has
    /// to shrink, because a placement that starts at a negative left draws on the desktop.
    /// </summary>
    [Fact]
    public void A_panel_wider_than_the_game_is_clamped_inside_it()
    {
        var small = new ScreenRect(100, 100, 640, 480);
        var placed = OverlayLayout.Place(small, OverlayAnchor.Right, OverlayWidth.MaxFraction, 0).Bounds;

        Assert.True(placed.Left >= small.Left);
        Assert.True(placed.Left + placed.Width <= small.Left + small.Width);
    }

    /// <summary>It places against the GAME, never the monitor. A game at an offset moves with it.</summary>
    [Fact]
    public void It_follows_the_game_rect_rather_than_the_desktop()
    {
        var offset = new ScreenRect(2560, 300, 1280, 720);
        var placed = OverlayLayout.Place(offset, OverlayAnchor.Right, Fifth, 0).Bounds;

        Assert.Equal(2560 + 1280 - 256 - OverlayLayout.Margin, placed.Left);
        Assert.True(placed.Top >= 300);
    }

    [Fact]
    public void No_game_window_places_nothing()
    {
        Assert.True(OverlayLayout.Place(ScreenRect.Empty, OverlayAnchor.Right, Fifth, 0).IsEmpty);
    }

    /// <summary>The overflow row carries the rest. The panel never becomes the screen.</summary>
    [Fact]
    public void It_never_takes_more_than_the_height_fraction()
    {
        Assert.True(Place(OverlayAnchor.Right).Bounds.Height <= (int)(1080 * OverlayLayout.MaxHeightFraction));
    }
}
