using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// <summary>How long the surface takes to appear, dim, or leave.</summary>
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));

    /// <summary>
    /// How long it takes to follow the game window. Long enough to read as one movement rather
    /// than a jump, short enough that an alt-tab back does not catch it still travelling.
    /// </summary>
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(160));

    private bool _placed;

    /// <summary>Creates the surface. It is not shown until the composition root shows it.</summary>
    public OverlayWindow()
    {
        InitializeComponent();
        Board.SetOverlayMode(true);
        Opacity = 0;
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
    /// <remarks>
    /// The first placement snaps: there is nothing on screen to move from, and animating in from
    /// wherever WPF put the window is the pop this exists to avoid. Every placement after that
    /// glides, so a game window dragged to another monitor takes the board with it smoothly.
    /// </remarks>
    public void ApplyBounds(int left, int top, int width, int height)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        var origin = transform.Transform(new Point(left, top));
        var size = transform.Transform(new Point(width, height));

        var w = Math.Max(size.X, 1);
        var h = Math.Max(size.Y, 1);

        Board.SetPanelWidth(w);

        if (!_placed || !IsVisible)
        {
            _placed = true;
            StopAnimation(LeftProperty, origin.X);
            StopAnimation(TopProperty, origin.Y);
            StopAnimation(WidthProperty, w);
            StopAnimation(HeightProperty, h);
            return;
        }

        Glide(LeftProperty, origin.X);
        Glide(TopProperty, origin.Y);
        Glide(WidthProperty, w);
        Glide(HeightProperty, h);
    }

    /// <summary>
    /// Brings the surface to <paramref name="target"/> opacity, fading rather than switching. Also
    /// what a Show and a Dim look like: the same animation to a different number.
    /// </summary>
    public void FadeTo(double target)
    {
        if (!IsVisible)
        {
            StopAnimation(OpacityProperty, 0);
            Show();
        }

        Animate(OpacityProperty, target, FadeDuration, onDone: null);
    }

    /// <summary>
    /// Off, this frame. Panic only: every other path fades, because every other path is a normal
    /// state change and a kill switch that animates is not a kill switch.
    /// </summary>
    public void HideNow()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
    }

    /// <summary>Fades out and then hides. Hiding first is the pop.</summary>
    public void FadeOutAndHide()
    {
        if (!IsVisible)
        {
            return;
        }

        Animate(OpacityProperty, 0, FadeDuration, onDone: Hide);
    }

    private void Glide(DependencyProperty property, double to)
    {
        if (Math.Abs((double)GetValue(property) - to) < 0.5)
        {
            return;
        }

        Animate(property, to, MoveDuration, onDone: null);
    }

    /// <summary>
    /// One animation, cleared on completion and replaced by the plain value.
    /// </summary>
    /// <remarks>
    /// A held animation owns the property: without the clear, the next direct set is silently
    /// ignored and the surface sticks where the last animation left it.
    /// </remarks>
    private void Animate(DependencyProperty property, double to, Duration duration, Action? onDone)
    {
        var animation = new DoubleAnimation(to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            StopAnimation(property, to);
            onDone?.Invoke();
        };

        BeginAnimation(property, animation);
    }

    private void StopAnimation(DependencyProperty property, double value)
    {
        BeginAnimation(property, null);
        SetValue(property, value);
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
