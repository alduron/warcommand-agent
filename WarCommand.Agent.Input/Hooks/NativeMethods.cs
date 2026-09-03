using System.Runtime.InteropServices;

namespace WarCommand.Agent.Input.Hooks;

/// <summary>
/// The Win32 surface this assembly touches. All of it is either a hook in our own process or a
/// public shell read of the foreground window. Nothing here enters the game process: there is no
/// memory access, no thread creation, no message posted to a window we do not own, and no input
/// synthesis of any kind.
/// </summary>
internal static class NativeMethods
{
    internal const int WhKeyboardLowLevel = 13;
    internal const int WhMouseLowLevel = 14;

    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmQuit = 0x0012;

    internal const int WmMiddleButtonDown = 0x0207;
    internal const int WmMiddleButtonUp = 0x0208;
    internal const int WmXButtonDown = 0x020B;
    internal const int WmXButtonUp = 0x020C;

    // Taken from the game only while a hold key is down. Never bindable: see
    // Convention_WarCommandNeverBindsLeftOrRightMouse and the hold gate in InputBridge.
    internal const int WmLeftButtonDown = 0x0201;
    internal const int WmLeftButtonUp = 0x0202;
    internal const int WmRightButtonDown = 0x0204;
    internal const int WmRightButtonUp = 0x0205;
    internal const int WmMouseWheel = 0x020A;

    internal const int GwlStyle = -16;
    internal const long WsCaption = 0x00C00000L;
    internal const long WsThickFrame = 0x00040000L;

    /// <summary>SHQueryUserNotificationState: a D3D application holds the display exclusively.</summary>
    internal const int QunsRunningD3DFullScreen = 3;

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr CallNextHook(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in Msg lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr DispatchMessage(in Msg lpMsg);

    /// <summary>Wakes our own pump thread so it can leave the loop. Never sent to a foreign window.</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>Public shell read. Which process owns a window. Nothing is opened or read inside it.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Public shell API. Answers whether a D3D app holds the display exclusively.</summary>
    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        internal IntPtr Hwnd;
        internal uint Message;
        internal IntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal Point Pt;
    }
}
