using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Input;

/// <summary>
/// Where the overlay sits inside the game's client rect. Pure arithmetic: no window, no Win32, no
/// DPI, so every anchor is testable without a message loop.
/// </summary>
/// <remarks>
/// It places against the game's CLIENT RECT and never the monitor. On a 32:9 panel with the game
/// windowed to the left two thirds, a monitor-anchored overlay lands on the desktop beside the
/// game rather than against its edge. See 06-overlay-ux.md "Window".
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

    /// <summary>The panel's bounds inside <paramref name="game"/>, in screen pixels.</summary>
    /// <remarks>
    /// Height is the cap rather than the drawn height: the window sizes to its content and the
    /// caller hands this in as MaxHeight.
    /// </remarks>
    public static ScreenRect Place(ScreenRect game, OverlayAnchor anchor, int widthPx)
    {
        if (game.IsEmpty)
        {
            return ScreenRect.Empty;
        }

        // Never wider than the game itself, which is the case on a small windowed launch and the
        // one where an unclamped width puts the panel off the side of the picture entirely.
        var width = Math.Clamp(widthPx, 1, Math.Max(1, game.Width - (Margin * 2)));
        var height = MaxHeight(game);

        var left = anchor == OverlayAnchor.Left
            ? game.Left + Margin
            : game.Left + game.Width - width - Margin;

        var top = anchor switch
        {
            OverlayAnchor.TopRight => game.Top + Margin,
            OverlayAnchor.BottomRight => game.Top + game.Height - height - Margin,

            // Left and Right are both vertically centred. The default anchor in the spec.
            _ => game.Top + ((game.Height - height) / 2),
        };

        return new ScreenRect(left, top, width, height);
    }

    /// <summary>The height cap for a given game rect.</summary>
    public static int MaxHeight(ScreenRect game) =>
        Math.Max(120, (int)(game.Height * MaxHeightFraction));
}
