using System.Runtime.InteropServices;

namespace WarCommand.Agent.Capture;

/// <summary>
/// Win32 needed to find the game window and copy pixels off the desktop. Nothing here opens a
/// handle to the game process: binding rule 1 forbids it and EAC would refuse anyway.
/// </summary>
internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;

    internal const long WsCaption = 0x00C00000L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsPopup = 0x80000000L;

    internal const int SrcCopy = 0x00CC0020;
    internal const int CaptureBlt = 0x40000000;

    internal static readonly nint PerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    internal delegate bool EnumWindowsProc(nint hwnd, nint param);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint param);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, char[] text, int count);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(
        nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader header, uint usage);
}
