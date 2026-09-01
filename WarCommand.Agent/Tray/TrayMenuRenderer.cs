using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WarCommand.Agent.Tray;

/// <summary>
/// Paints the tray menu the way docs/design/mocks/TrayMenu.dc.html draws it: a Windows 11 light
/// menu, #F9F9F9 on a #D6D6D6 hairline, 32px rows, no image gutter, and a right-aligned dim value
/// for a toggle's state instead of a checkmark.
/// </summary>
/// <remarks>
/// A renderer rather than owner-draw on each item: the colours are the design's tokens and belong
/// in one place, and every item added later inherits them without knowing they exist.
/// </remarks>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
    internal static readonly Color Text = Color.FromArgb(0x1B, 0x1B, 0x1B);
    internal static readonly Color TextDim = Color.FromArgb(0x5D, 0x5D, 0x5D);
    internal static readonly Color TextFaint = Color.FromArgb(0x8A, 0x8A, 0x8A);
    internal static readonly Color Hover = Color.FromArgb(0xED, 0xED, 0xED);
    internal static readonly Color Line = Color.FromArgb(0xE2, 0xE2, 0xE2);
    internal static readonly Color Border = Color.FromArgb(0xD6, 0xD6, 0xD6);
    internal static readonly Color Ok = Color.FromArgb(0x0F, 0x7B, 0x0F);
    internal static readonly Color Warn = Color.FromArgb(0x9D, 0x5D, 0x00);
    internal static readonly Color Grey = Color.FromArgb(0x8A, 0x8A, 0x8A);

    internal TrayMenuRenderer()
        : base(new Colors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var brush = new SolidBrush(Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var pen = new Pen(Border);
        var bounds = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    /// <summary>The mock's 4px-radius hover fill, inset 4px from the menu's own padding.</summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var bounds = new Rectangle(4, 0, e.Item.Bounds.Width - 8, e.Item.Bounds.Height);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectangle(bounds, 4);
        using var brush = new SolidBrush(Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var pen = new Pen(Line);
        var y = e.Item.Bounds.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Bounds.Width - 8, y);
    }

    /// <summary>No checkmarks. A toggle's state is the right-aligned value text.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Intentionally empty.
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.ArrowColor = e.Item?.Enabled is true ? TextDim : TextFaint;
        base.OnRenderArrow(e);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Kills the professional table's grey image gutter and its blue selection. Everything visible
    /// is drawn above; this exists so nothing underneath paints a colour the design does not have.
    /// </summary>
    private sealed class Colors : ProfessionalColorTable
    {
        public Colors() => UseSystemColors = false;

        public override Color ImageMarginGradientBegin => Surface;

        public override Color ImageMarginGradientMiddle => Surface;

        public override Color ImageMarginGradientEnd => Surface;

        public override Color ToolStripDropDownBackground => Surface;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color MenuItemBorder => Hover;

        public override Color MenuBorder => Border;

        public override Color SeparatorDark => Line;

        public override Color SeparatorLight => Line;
    }
}
