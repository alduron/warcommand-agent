using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WarCommand.Agent.Core.Settings;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The in-game surface from 06-overlay-ux.md: a layered, click-through, no-activate, topmost tool
/// window with no chrome, drawing the same <see cref="BoardView"/> the settings window hosts.
/// </summary>
/// <remarks>
/// This is the one exception to Convention_WarCommandAgentHasExactlyOneWindow, which names it: the
/// overlay "is not a window and is not this: it is a click-through surface with no chrome". It has
/// no title bar, no taskbar entry, no alt-tab entry and cannot be focused or clicked.
/// <para>
/// Nothing here enters the game process. It is a window of our own beside the game's, positioned
/// from a client rect the shell handed us.
/// </para>
/// </remarks>
public partial class OverlayWindow : Window
{
    /// <summary>Creates the surface. It is not shown until the composition root shows it.</summary>
    public OverlayWindow()
    {
        InitializeComponent();
        Board.SetOverlayMode(true);
    }

    /// <summary>The board this surface draws. Rendered by the same presenter as the window's.</summary>
    public BoardView BoardView => Board;

    /// <summary>
    /// The three opacity steps as real numbers. Low still has to be legible against snow, so the
    /// floor is not near-invisible; High is fully opaque scrim, not a fully opaque plate.
    /// </summary>
    public static double OpacityFor(OverlayOpacity step) => step switch
    {
        OverlayOpacity.Low => 0.55,
        OverlayOpacity.High => 1.0,
        _ => 0.85,
    };

    /// <summary>
    /// Places the surface, in physical screen pixels, converting to the DIPs WPF positions in.
    /// The caller works in pixels because the game's client rect arrives that way.
    /// </summary>
    public void ApplyBounds(int left, int top, int width, int height)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        var origin = transform.Transform(new Point(left, top));
        var size = transform.Transform(new Point(width, height));

        Left = origin.X;
        Top = origin.Y;
        Width = Math.Max(size.X, 1);
        Height = Math.Max(size.Y, 1);
        Board.SetPanelWidth(Math.Max(size.X, 1));
    }

    /// <summary>
    /// Which edge of the anchor rect the board sits against. Left and Right centre it; the two
    /// corner anchors push it to that corner, which is the whole point of choosing one.
    /// </summary>
    public void ApplyAnchor(OverlayAnchor anchor) => Board.VerticalAlignment = anchor switch
    {
        OverlayAnchor.TopRight => VerticalAlignment.Top,
        OverlayAnchor.BottomRight => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center,
    };

    /// <summary>
    /// Applies the four extended styles. Done here rather than in the constructor because there is
    /// no window handle to apply them to until the source exists.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var current = OverlayNativeMethods.GetWindowLongPtr(handle, OverlayNativeMethods.GwlExStyle);
        _ = OverlayNativeMethods.SetWindowLongPtr(
            handle,
            OverlayNativeMethods.GwlExStyle,
            current | OverlayNativeMethods.OverlayStyles);
    }
}
