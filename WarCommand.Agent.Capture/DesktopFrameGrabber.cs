namespace WarCommand.Agent.Capture;

/// <summary>
/// Copies a screen rectangle with BitBlt from the desktop DC. Out of process, no handle to the
/// game, no Present hook: the compositor is doing the work and the game is never touched.
/// </summary>
/// <remarks>
/// This reads the DESKTOP, which means it only works for a borderless windowed game. Exclusive
/// fullscreen returns black, and that is already an unsupported configuration that drops the agent
/// into second-screen mode. If borderless itself comes back black on a real build, the escalation
/// is Windows.Graphics.Capture, which costs a D3D device and a package reference.
/// </remarks>
public static class DesktopFrameGrabber
{
    /// <summary>Marks the process per-monitor DPI aware so a scaled display does not shrink the grab.</summary>
    public static void MakeDpiAware() =>
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);

    /// <summary>The rectangle as BGRA, or null when the copy failed outright.</summary>
    public static Frame? Grab(CaptureArea area)
    {
        if (area.IsEmpty)
        {
            return null;
        }

        var width = area.Width;
        var height = area.Height;

        var screen = NativeMethods.GetDC(nint.Zero);
        if (screen == nint.Zero)
        {
            return null;
        }

        var memory = nint.Zero;
        var bitmap = nint.Zero;
        var previous = nint.Zero;

        try
        {
            memory = NativeMethods.CreateCompatibleDC(screen);
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
            if (memory == nint.Zero || bitmap == nint.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memory, bitmap);

            var copied = NativeMethods.BitBlt(
                memory, 0, 0, width, height, screen, area.Left, area.Top,
                NativeMethods.SrcCopy | NativeMethods.CaptureBlt);
            if (!copied)
            {
                return null;
            }

            // Negative height asks GDI for a top-down buffer, so row 0 is the top row.
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var pixels = new byte[width * height * 4];
            var rows = NativeMethods.GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0);

            return rows == height ? new Frame(pixels, width, height) : null;
        }
        finally
        {
            if (previous != nint.Zero)
            {
                NativeMethods.SelectObject(memory, previous);
            }

            if (bitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memory != nint.Zero)
            {
                NativeMethods.DeleteDC(memory);
            }

            _ = NativeMethods.ReleaseDC(nint.Zero, screen);
        }
    }
}
