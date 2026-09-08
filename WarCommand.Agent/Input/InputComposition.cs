using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Game;
using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Input.Devices;
using WarCommand.Agent.Input.Hooks;
using WarCommand.Agent.Tray;

namespace WarCommand.Agent.Composition;

/// <summary>
/// Assembles the input layer: the panic switch, the bridge, the hooks and the sinks that carry a
/// key press to something visible.
/// </summary>
/// <remarks>
/// The pieces all existed and nothing built them, so no hotkey did anything in any build. This is
/// the composition root for them and the only place they are constructed.
///
/// Binding rule 6 holds throughout: nothing here logs a key code, and every binding except Panic is
/// inert unless the game is the foreground window. The gate is <see cref="IForegroundProbe"/>, so a
/// dev build satisfies it with a fixed probe rather than by weakening the rule.
/// </remarks>
public sealed class InputComposition : IDisposable
{
    private readonly HookHost _hooks;
    private readonly IClientLog _log;
    private bool _disposed;

    private InputComposition(
        InputBridge bridge,
        PanicSwitch panic,
        HookHost hooks,
        IDeviceButtonSource devices,
        IClientLog log)
    {
        Bridge = bridge;
        Panic = panic;
        Devices = devices;
        _hooks = hooks;
        _log = log;
    }

    public InputBridge Bridge { get; }

    public PanicSwitch Panic { get; }

    /// <summary>
    /// The HOTAS buttons. It is NOT registered with Panic: the stick has to keep being read while
    /// suspended or a panic pressed on the stick could never be released from it, and the bridge
    /// already refuses everything but Panic while it is engaged.
    /// </summary>
    public IDeviceButtonSource Devices { get; }

    /// <summary>True once the low-level hook is actually installed.</summary>
    public bool IsRunning => _hooks.IsRunning;

    /// <summary>
    /// Builds the whole input layer and starts the hook.
    /// </summary>
    /// <param name="bindings">The four bindings. Panic can never be unbound.</param>
    /// <param name="foreground">
    /// The gate. <see cref="GameWindowWatcher"/> in a real run; a fixed probe in the overlay demo,
    /// where the game is not running and every binding would otherwise be inert.
    /// </param>
    /// <param name="overlay">Registered as the drawing subsystem and driven by the Board chord.</param>
    /// <param name="tray">Registered as the indicator subsystem so it grays on panic.</param>
    /// <param name="onHold">
    /// Hold-key edges, with the action so the two can be told apart. Both open the microphone and
    /// both open the menu; down is where a coordinate is snapshotted, for both.
    /// </param>
    /// <param name="menu">
    /// The menu keys and the gate. Null leaves every digit inert, which is the tray-only and
    /// overlay-demo case: there is no board for a verb to act on.
    /// </param>
    /// <param name="screenCapture">The frame grabber, so Panic stops it. Null on a surface with none.</param>
    /// <param name="audioCapture">The microphone path, so Panic closes it. Null on a surface with none.</param>
    /// <param name="devices">HOTAS buttons. Null takes the Raw Input reader, which is what ships.</param>
    public static InputComposition Start(
        BindingSet bindings,
        IForegroundProbe foreground,
        OverlayController overlay,
        TrayIconController? tray,
        Action<BindingAction, bool> onHold,
        IClientLog log,
        MenuDriver? menu = null,
        ISuspendable? screenCapture = null,
        ISuspendable? audioCapture = null,
        IDeviceButtonSource? devices = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(onHold);
        ArgumentNullException.ThrowIfNull(log);

        // The input layer's own seam, connected to the exported file. Every IInputLog parameter
        // below is optional and none of them was ever passed one, so hooks installing, panic, the
        // game window coming and going and the exclusive-fullscreen refusal all went to a null
        // sink. A report of "no hotkeys" arrived with nothing about hotkeys in the log.
        var events = new WarCommand.Agent.Diagnostics.InputLogBridge(log);

        var panic = new PanicSwitch(events);
        var bridge = new InputBridge(bindings, panic, foreground, events);
        var hooks = new HookHost(bridge, events);

        var chords = new ChordRouter(overlay, panic, log);

        // The menu, and the gate that decides whether bare digits are hooked at all. Passing null
        // for both is what made every digit inert: the machine, its tree and its outcomes were all
        // written and tested with nothing on the other end of them.
        bridge.Connect(new PttRouter(bridge, onHold), menu, chords, menuGate: menu, menuNav: menu);

        if (menu is not null)
        {
            // Bare digits are hooked only while the menu is open, so every state change has to
            // rebuild the armed table before the next key lands.
            menu.Rearm = bridge.Rearm;
        }

        // Arm() refuses until every subsystem is registered, so a new one cannot silently miss the
        // kill switch. A subsystem that genuinely does not exist on this surface registers as an
        // explicit no-op, which is the difference between "nothing to suspend" and "forgot".
        // Capture and audio DO exist now: leaving the placeholders in meant Panic stopped the hooks
        // and the drawing and left the microphone and the frame grabber running, which is the one
        // thing binding rule 7 exists to prevent.
        panic.Register(PanicSubsystem.Hotkeys, hooks);
        panic.Register(PanicSubsystem.OverlayDrawing, overlay);
        panic.Register(PanicSubsystem.ScreenCapture, screenCapture ?? NotBuiltYet.Instance);
        panic.Register(PanicSubsystem.AudioCapture, audioCapture ?? NotBuiltYet.Instance);
        panic.Register(PanicSubsystem.TrayIndicator, (ISuspendable?)tray ?? NotBuiltYet.Instance);
        panic.Arm();

        // The second binding row. Edges go through the same dispatch the keyboard uses, so a HOTAS
        // button is gated on the game, on Panic and on the hold exactly as its key twin is.
        var buttons = devices ?? new RawInputDeviceSource();
        buttons.ButtonChanged += (_, e) =>
        {
            var at = DateTimeOffset.UtcNow;
            _ = e.Down ? bridge.HandleDevice(e.Button, at) : bridge.HandleDeviceUp(e.Button, at);
        };
        buttons.Start();

        hooks.Start();
        log.Info(hooks.IsRunning
            ? $"Input armed. Menu {Label(bindings, BindingAction.Menu)}, PTT {Label(bindings, BindingAction.Ptt)}, board {Label(bindings, BindingAction.Board)}, panic {Label(bindings, BindingAction.Panic)}."
            : "Input did NOT arm: the low-level hook is not installed.");

        return new InputComposition(bridge, panic, hooks, buttons, log);
    }

    private static string Label(BindingSet bindings, BindingAction action) =>
        bindings[action].IsBound ? bindings[action].Label : "unbound";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hooks.Dispose();
        Devices.Dispose();
        _log.Info("Input disarmed.");
    }

    /// <summary>
    /// Carries the hold-key edges out, naming which key it was. No key code, ever.
    /// </summary>
    /// <remarks>
    /// Both hold keys land on this one sink, so the action is read back from the bridge. They now
    /// do the same thing; the action is still carried because a binding is still a binding.
    /// </remarks>
    private sealed class PttRouter(InputBridge bridge, Action<BindingAction, bool> onHold) : IPttSink
    {
        public void PttDown(DateTimeOffset at) => onHold(bridge.LastHoldAction, true);

        public void PttUp(DateTimeOffset at) => onHold(bridge.LastHoldAction, false);
    }

    /// <summary>Board cycles the surface, Panic toggles the kill switch. Nothing else is a chord.</summary>
    private sealed class ChordRouter(OverlayController overlay, PanicSwitch panic, IClientLog log)
        : IChordSink
    {
        public void Invoke(BindingAction action)
        {
            switch (action)
            {
                case BindingAction.Board:
                    overlay.CycleBoard();
                    break;

                case BindingAction.Panic:
                    log.Info(panic.Toggle() ? "Panic engaged." : "Panic released.");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>A subsystem that exists in the enum and not yet in the product.</summary>
    private sealed class NotBuiltYet : ISuspendable
    {
        public static NotBuiltYet Instance { get; } = new();

        public void Suspend()
        {
        }

        public void Resume()
        {
        }
    }
}
