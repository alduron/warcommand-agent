using System.Runtime.InteropServices;

namespace WarCommand.Agent.CaptureProbe;

/// <summary>
/// Answers one question a real build has to answer for us: while a hold key is down, can a
/// low-level mouse hook stop movement, wheel and side buttons from reaching the game.
/// </summary>
/// <remarks>
/// It only ever SWALLOWS. Nothing is synthesised and nothing is sent, so binding rule 1 holds:
/// suppressing delivery is not injecting input. The answer decides whether a commo rose is
/// possible at all, or whether the menu stays on a key ring.
/// </remarks>
internal static class SuppressionProbe
{
    private const int WhMouseLowLevel = 14;
    private const int HcAction = 0;

    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private static nint _hook;
    private static int _holdKey;
    private static long _swallowedMoves;
    private static long _swallowedWheels;
    private static long _swallowedButtons;
    private static long _passedThrough;

    private static HookProc? _proc;

    internal static void Run(int holdVirtualKey, string holdLabel, int seconds)
    {
        _holdKey = holdVirtualKey;
        _proc = Callback;

        _hook = SetWindowsHookEx(WhMouseLowLevel, _proc, GetModuleHandle(null), 0);
        if (_hook == nint.Zero)
        {
            Console.WriteLine($"  hook FAILED, win32 {Marshal.GetLastWin32Error()}");
            return;
        }

        Console.WriteLine($"  hook installed. HOLD {holdLabel} in the game and move the mouse, scroll, and click the side buttons.");
        Console.WriteLine($"  watching for {seconds}s. Report whether the camera turned or the weapon swapped.");
        Console.WriteLine();

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var nextReport = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < until && PeekPump())
        {
            if (DateTime.UtcNow >= nextReport)
            {
                var seen = Interlocked.Read(ref _passedThrough)
                    + Interlocked.Read(ref _swallowedMoves)
                    + Interlocked.Read(ref _swallowedWheels)
                    + Interlocked.Read(ref _swallowedButtons);

                var left = (int)(until - DateTime.UtcNow).TotalSeconds;
                Console.WriteLine(
                    $"  {left,3}s left   events seen {seen,6}   swallowed {Interlocked.Read(ref _swallowedMoves)} moves,"
                    + $" {Interlocked.Read(ref _swallowedWheels)} wheel, {Interlocked.Read(ref _swallowedButtons)} buttons");

                nextReport = DateTime.UtcNow.AddSeconds(2);
            }

            Thread.Sleep(1);
        }

        UnhookWindowsHookEx(_hook);

        Console.WriteLine($"  swallowed while held:  {_swallowedMoves} moves, {_swallowedWheels} wheel, {_swallowedButtons} buttons");
        Console.WriteLine($"  passed through:        {_passedThrough}");
        Console.WriteLine();
        Console.WriteLine("  If the counts are non-zero and the camera did NOT move, suppression works and the rose is on.");
        Console.WriteLine("  If the counts are non-zero and the camera DID move, the game reads raw input and the rose is off.");
        Console.WriteLine("  If the counts are zero, the hold key was never seen: try a different key.");
    }

    private static nint Callback(int code, nint wParam, nint lParam)
    {
        if (code != HcAction)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        // The callback must do nothing slow. Exceeding LowLevelHooksTimeout makes Windows stop
        // calling us with no error anywhere, which reads exactly like suppression not working.
        if ((GetAsyncKeyState(_holdKey) & 0x8000) != 0)
        {
            switch ((int)wParam)
            {
                case WmMouseMove:
                    Interlocked.Increment(ref _swallowedMoves);
                    return 1;
                case WmMouseWheel:
                case WmMouseHWheel:
                    Interlocked.Increment(ref _swallowedWheels);
                    return 1;
                case WmXButtonDown:
                case WmXButtonUp:
                    Interlocked.Increment(ref _swallowedButtons);
                    return 1;
                default:
                    break;
            }
        }

        Interlocked.Increment(ref _passedThrough);
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // A low-level hook is only called on a thread that pumps messages.
    private static bool PeekPump()
    {
        while (PeekMessage(out var message, nint.Zero, 0, 0, 1))
        {
            if (message.Message == 0x0012)
            {
                return false;
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        return true;
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
