using System.Runtime.InteropServices;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Input.Hooks;

/// <summary>Whether a press is passed to the game or consumed by WarCommand.</summary>
internal enum HookVerdict
{
    /// <summary>Hand the press on. The default, and the only outcome for anything unarmed.</summary>
    PassThrough = 0,

    /// <summary>WarCommand consumed it.</summary>
    Swallow,
}

/// <summary>Which edge of a key the hook saw.</summary>
internal enum KeyTransition
{
    /// <summary>Neither edge. Ignored.</summary>
    Other = 0,

    /// <summary>Key down.</summary>
    Down,

    /// <summary>Key up.</summary>
    Up,
}

/// <summary>
/// A WH_KEYBOARD_LL hook in our own process, installed with a zero module handle. It touches no other
/// process: an OS-level hook is not injection, and the module handle is zero precisely so that it
/// cannot be one.
/// </summary>
/// <remarks>
/// The callback's first act is the arming test. Anything WarCommand does not hold returns
/// immediately, with no processing, no allocation and nothing recorded. No code path anywhere in this
/// type turns a key code into a string.
/// </remarks>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly InputBridge _bridge;
    private readonly IInputLog _log;
    private readonly NativeMethods.HookProc _callback;

    private IntPtr _handle;
    private BindingModifiers _modifiers;

    /// <summary>Creates the hook. Nothing is installed until <see cref="Install"/> is called.</summary>
    public LowLevelKeyboardHook(InputBridge bridge, IInputLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        _log = log ?? NullInputLog.Instance;

        // Held in a field so the GC cannot collect the thunk the OS is calling.
        _callback = Callback;
    }

    /// <summary>True while the hook is installed.</summary>
    public bool IsInstalled => _handle != IntPtr.Zero;

    /// <summary>
    /// Installs the hook on the calling thread, which must pump messages. The module handle is zero:
    /// a non-zero one would be a hook into a foreign module, which the architecture test fails the
    /// build on.
    /// </summary>
    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        _handle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLowLevel,
            _callback,
            hMod:IntPtr.Zero,
            dwThreadId: 0);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "the keyboard hook could not be installed",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        _log.Note(InputEvent.KeyboardHookInstalled);
    }

    /// <summary>Removes the hook and forgets any held modifier.</summary>
    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
        _modifiers = BindingModifiers.None;
        _log.Note(InputEvent.KeyboardHookRemoved);
    }

    /// <summary>Forgets any held modifier. Called when the hook stops seeing releases.</summary>
    public void ForgetModifiers() => _modifiers = BindingModifiers.None;

    /// <inheritdoc />
    public void Dispose() => Uninstall();

    /// <summary>
    /// The whole decision, with no Win32 in it. The arming test comes first and is the only work done
    /// for a key WarCommand does not hold.
    /// </summary>
    internal HookVerdict Evaluate(int virtualKey, KeyTransition transition)
    {
        if (!_bridge.Armed.IsArmed(virtualKey))
        {
            return HookVerdict.PassThrough;
        }

        if (transition == KeyTransition.Other)
        {
            return HookVerdict.PassThrough;
        }

        if (ModifierKeys.IsModifier(virtualKey))
        {
            var modifier = ModifierKeys.Of(virtualKey);
            _modifiers = transition == KeyTransition.Down ? _modifiers | modifier : _modifiers & ~modifier;

            // A modifier is never a binding on its own and is never swallowed: the game may want it.
            return HookVerdict.PassThrough;
        }

        if (!BindingKey.TryFromVirtualKey(virtualKey, out var key))
        {
            return HookVerdict.PassThrough;
        }

        var chord = new Chord(_modifiers, key);
        var dispatch = transition == KeyTransition.Down ? _bridge.Handle(chord) : _bridge.HandleUp(chord);
        return dispatch.Swallow ? HookVerdict.Swallow : HookVerdict.PassThrough;
    }

    private static KeyTransition TransitionOf(IntPtr wParam) => (int)(long)wParam switch
    {
        NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown => KeyTransition.Down,
        NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp => KeyTransition.Up,
        _ => KeyTransition.Other,
    };

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHook(_handle, nCode, wParam, lParam);
        }

        // KBDLLHOOKSTRUCT begins with the virtual key. Read as an int rather than marshalled, so the
        // unarmed path allocates nothing.
        var virtualKey = Marshal.ReadInt32(lParam);

        return Evaluate(virtualKey, TransitionOf(wParam)) == HookVerdict.Swallow
            ? new IntPtr(1)
            : NativeMethods.CallNextHook(_handle, nCode, wParam, lParam);
    }
}
