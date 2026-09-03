using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WarCommand.Agent.Capture;

/// <summary>One visible top-level window, and enough about it to decide whether it is the game.</summary>
public sealed record WindowCandidate(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    int ClientWidth,
    int ClientHeight,
    bool IsBorderless,
    bool CoversItsMonitor);

/// <summary>
/// Finds the game window by process name. The names come from game.process_names in the served
/// profile and are a guess until somebody runs this against a real build, which is why the probe
/// can list every window rather than only matching.
/// </summary>
public static class GameWindow
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Every visible top-level window with a title, newest handles last.</summary>
    public static IReadOnlyList<WindowCandidate> Enumerate()
    {
        var found = new List<WindowCandidate>();

        NativeMethods.EnumWindows(
            (hwnd, unused) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                var length = NativeMethods.GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                var buffer = new char[length + 1];
                var written = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
                var title = new string(buffer, 0, Math.Max(0, written));

                var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (thread == 0)
                {
                    return true;
                }

                if (!NativeMethods.GetClientRect(hwnd, out var client) || client.Width <= 0 || client.Height <= 0)
                {
                    return true;
                }

                var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle);
                var borderless = (style & (NativeMethods.WsCaption | NativeMethods.WsThickFrame)) == 0;

                var covers = CoversItsMonitor(hwnd);

                found.Add(new WindowCandidate(
                    hwnd,
                    title,
                    ProcessNameOf((int)pid),
                    (int)pid,
                    client.Width,
                    client.Height,
                    borderless,
                    covers));

                return true;
            },
            nint.Zero);

        return found;
    }

    /// <summary>The first window whose process name matches, case-insensitively. Null when absent.</summary>
    public static WindowCandidate? Find(IReadOnlyCollection<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        return Enumerate().FirstOrDefault(w => processNames.Any(
            n => string.Equals(n, w.ProcessName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The client rect in screen coordinates, which is what a capture copies.</summary>
    public static CaptureArea ClientRectOnScreen(nint hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = new NativeMethods.Point { X = 0, Y = 0 };
        NativeMethods.ClientToScreen(hwnd, ref origin);

        return new CaptureArea(origin.X, origin.Y, client.Width, client.Height);
    }

    /// <summary>
    /// The cursor in client coordinates, or null when it is outside the window. The map readout is
    /// anchored to the moving crosshair, so where the pointer is is the only reliable way to say
    /// which blob is the readout without a fixed rectangle, which binding rule 5 forbids.
    /// </summary>
    public static (int X, int Y)? CursorInClient(nint hwnd)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return null;
        }

        var area = ClientRectOnScreen(hwnd);
        if (area.IsEmpty)
        {
            return null;
        }

        var x = cursor.X - area.Left;
        var y = cursor.Y - area.Top;

        return x >= 0 && y >= 0 && x < area.Width && y < area.Height ? (x, y) : null;
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "?";
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>Borderless plus covering its monitor is the shape the overlay can draw over.</summary>
    private static bool CoversItsMonitor(nint hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top
            && window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
    }
}
