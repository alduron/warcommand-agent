using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Input;

/// <summary>Where the panel sits, and where the board sits inside it.</summary>
/// <param name="Bounds">The window's bounds in screen pixels.</param>
/// <param name="VerticalBias">
/// Where the board sits inside those bounds: +1 against the top, -1 against the bottom, 0 centered.
/// The window is taller than the board, so the bias is what makes a top anchor draw on the top edge.
/// </param>
public readonly record struct OverlayPlacement(ScreenRect Bounds, double VerticalBias)
{
    /// <summary>No game window, so nothing to place against.</summary>
    public static OverlayPlacement Empty => new(ScreenRect.Empty, 0);

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Bounds.IsEmpty;
}

/// <summary>
/// Where the overlay sits inside the game's client rect. Pure arithmetic: no window, no Win32, no
/// DPI, so every anchor is testable without a message loop.
/// </summary>
/// <remarks>
/// It places against the game's CLIENT RECT and never the monitor. On a 32:9 panel with the game
/// windowed to the left two thirds, a monitor-anchored overlay lands on the desktop beside the
/// game rather than against its edge. See 06-overlay-ux.md "Window".
///
/// Nothing here is measured in pixels the user chose. Width is a share of the game's width and the
/// nudge is a share of the travel that anchor has left, so one setting means the same thing on a
/// 1080p laptop and a 32:9 panel.
/// </remarks>
public static class OverlayLayout
{
    /// <summary>Breathing room from the game's edge, in pixels at 100 percent scale.</summary>
    public const int Margin = 16;

    /// <summary>
    /// The tallest the panel may draw, as a share of the game's height. Above this the overlay
    /// stops being a glanceable strip and starts being the screen; the overflow row carries the
    /// rest, which is what it is for.
    /// </summary>
    public const double MaxHeightFraction = 0.72;

    /// <summary>Which third of an axis an anchor sits in.</summary>
    private enum Band
    {
        Start,
        Center,
        End,
    }

    /// <summary>The panel's placement inside <paramref name="game"/>, in screen pixels.</summary>
    /// <param name="game">The game's client rect, or the chosen monitor's work area.</param>
    /// <param name="anchor">One of the nine grid positions.</param>
    /// <param name="widthFraction">Panel width as a share of the game's width.</param>
    /// <param name="slide">
    /// Nudge along the anchor's free axis, -1 to +1. Positive is up on a vertical axis and right on
    /// a horizontal one, so +1 always travels toward the top right corner. A corner anchor has no
    /// free axis and ignores it.
    /// </param>
    /// <remarks>
    /// Bounds height is the cap rather than the drawn height: the board sizes to its content and
    /// aligns inside the window by <see cref="OverlayPlacement.VerticalBias"/>.
    /// </remarks>
    public static OverlayPlacement Place(
        ScreenRect game,
        OverlayAnchor anchor,
        double widthFraction,
        double slide)
    {
        if (game.IsEmpty)
        {
            return OverlayPlacement.Empty;
        }

        // Never wider than the game itself, which is the case on a small windowed launch and the
        // one where an unclamped width puts the panel off the side of the picture entirely.
        var share = Math.Clamp(widthFraction, OverlayWidth.MinFraction, OverlayWidth.MaxFraction);
        var width = Math.Clamp(
            (int)Math.Round(game.Width * share),
            1,
            Math.Max(1, game.Width - (Margin * 2)));
        var height = MaxHeight(game);
        var nudge = Math.Clamp(slide, -1, 1);

        var horizontal = HorizontalBand(anchor);
        var vertical = VerticalBand(anchor);

        // Exactly one axis is ever free, and vertical wins for the center anchor so that Left,
        // Right and Center all nudge the same way.
        var verticalFree = vertical == Band.Center;
        var horizontalFree = !verticalFree && horizontal == Band.Center;

        var left = Along(game.Left, game.Width, width, horizontal, horizontalFree ? nudge : 0);
        var top = Along(game.Top, game.Height, height, vertical, verticalFree ? -nudge : 0);

        var bias = vertical switch
        {
            Band.Start => 1.0,
            Band.End => -1.0,
            _ => verticalFree ? nudge : 0,
        };

        return new OverlayPlacement(new ScreenRect(left, top, width, height), bias);
    }

    /// <summary>The height cap for a given game rect.</summary>
    public static int MaxHeight(ScreenRect game) =>
        Math.Max(120, (int)(game.Height * MaxHeightFraction));

    /// <summary>
    /// One axis. <paramref name="towardEnd"/> is the nudge expressed as travel toward the far edge,
    /// so it reaches that edge exactly at 1 and the center at 0.
    /// </summary>
    private static int Along(int origin, int span, int size, Band band, double towardEnd)
    {
        var start = origin + Margin;
        var end = origin + span - size - Margin;
        var center = origin + ((span - size) / 2);

        if (end < start)
        {
            // No room for the margin at all: centering is the least wrong answer.
            return center;
        }

        return band switch
        {
            Band.Start => start,
            Band.End => end,
            _ => Lerp(center, towardEnd >= 0 ? end : start, Math.Abs(towardEnd)),
        };
    }

    private static int Lerp(double from, double to, double t) => (int)Math.Round(from + ((to - from) * t));

    private static Band HorizontalBand(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.Left or OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft => Band.Start,
        OverlayAnchor.Right or OverlayAnchor.TopRight or OverlayAnchor.BottomRight => Band.End,
        _ => Band.Center,
    };

    private static Band VerticalBand(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.Top or OverlayAnchor.TopLeft or OverlayAnchor.TopRight => Band.Start,
        OverlayAnchor.Bottom or OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight => Band.End,
        _ => Band.Center,
    };
}
