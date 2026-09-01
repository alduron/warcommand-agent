using System.Runtime.InteropServices;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Input.Hooks;

/// <summary>
/// A WH_MOUSE_LL hook in our own process, installed with a zero module handle. It exists because a
/// mouse button is a legitimate binding and Mouse5 is the natural push-to-talk key.
/// </summary>
/// <remarks>
/// Left and Right carry no label and so cannot be bound: swallowing either would take the game's
/// primary controls. Movement is never examined. Same three rules as the keyboard hook: unarmed
/// buttons return immediately, nothing runs unless the game has focus except Panic, and no code path
/// renders a button code into a string.
/// </remarks>
public sealed class LowLevelMouseHook : IDisposable
{
    private readonly InputBridge _bridge;
    private readonly IInputLog _log;
    private readonly NativeMethods.HookProc _callback;

    private IntPtr _handle;

    /// <summary>Creates the hook. Nothing is installed until <see cref="Install"/> is called.</summary>
    public LowLevelMouseHook(InputBridge bridge, IInputLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        _log = log ?? NullInputLog.Instance;
        _callback = Callback;
    }

    /// <summary>True while the hook is installed.</summary>
    public bool IsInstalled => _handle != IntPtr.Zero;

    /// <summary>Installs the hook on the calling thread, which must pump messages.</summary>
    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        _handle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLowLevel,
            _callback,
            hMod:IntPtr.Zero,
            dwThreadId: 0);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "the mouse hook could not be installed",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        _log.Note(InputEvent.MouseHookInstalled);
    }

    /// <summary>Removes the hook.</summary>
    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
        _log.Note(InputEvent.MouseHookRemoved);
    }

    /// <inheritdoc />
    public void Dispose() => Uninstall();

    /// <summary>
    /// The whole decision, with no Win32 in it. <paramref name="virtualKey"/> is the button's
    /// virtual-key code; the arming test comes first.
    /// </summary>
    internal HookVerdict Evaluate(int virtualKey, KeyTransition transition)
    {
        if (!_bridge.Armed.IsArmed(virtualKey))
        {
            return HookVerdict.PassThrough;
        }

        if (transition == KeyTransition.Other || !BindingKey.TryFromVirtualKey(virtualKey, out var key))
        {
            return HookVerdict.PassThrough;
        }

        // A mouse binding is bare. RightAlt plus a mouse button is not in the chord set.
        var chord = Chord.Of(key);
        var dispatch = transition == KeyTransition.Down ? _bridge.Handle(chord) : _bridge.HandleUp(chord);
        return dispatch.Swallow ? HookVerdict.Swallow : HookVerdict.PassThrough;
    }

    /// <summary>The virtual-key code a mouse message names, or zero for one we never bind.</summary>
    private static int ButtonOf(int message, IntPtr lParam, out KeyTransition transition)
    {
        switch (message)
        {
            case NativeMethods.WmMiddleButtonDown:
                transition = KeyTransition.Down;
                return 0x04;
            case NativeMethods.WmMiddleButtonUp:
                transition = KeyTransition.Up;
                return 0x04;
            case NativeMethods.WmXButtonDown:
            case NativeMethods.WmXButtonUp:
                transition = message == NativeMethods.WmXButtonDown ? KeyTransition.Down : KeyTransition.Up;

                // MSLLHOOKSTRUCT.mouseData sits after the POINT. Its high word names the thumb button.
                var which = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                return which switch { 1 => 0x05, 2 => 0x06, _ => 0 };
            default:
                transition = KeyTransition.Other;
                return 0;
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHook(_handle, nCode, wParam, lParam);
        }

        var button = ButtonOf((int)(long)wParam, lParam, out var transition);
        if (button == 0)
        {
            return NativeMethods.CallNextHook(_handle, nCode, wParam, lParam);
        }

        return Evaluate(button, transition) == HookVerdict.Swallow
            ? new IntPtr(1)
            : NativeMethods.CallNextHook(_handle, nCode, wParam, lParam);
    }
}
