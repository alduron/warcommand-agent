using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Game;
using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;
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

    private InputComposition(InputBridge bridge, PanicSwitch panic, HookHost hooks, IClientLog log)
    {
        Bridge = bridge;
        Panic = panic;
        _hooks = hooks;
        _log = log;
    }

    public InputBridge Bridge { get; }

    public PanicSwitch Panic { get; }

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
    /// <param name="tray">Registered as the indicator subsystem so it greys on panic.</param>
    /// <param name="onPtt">Push-to-talk edges. Down is where a coordinate is snapshotted.</param>
    public static InputComposition Start(
        BindingSet bindings,
        IForegroundProbe foreground,
        OverlayController overlay,
        TrayIconController? tray,
        Action<bool> onPtt,
        IClientLog log)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(onPtt);
        ArgumentNullException.ThrowIfNull(log);

        var panic = new PanicSwitch();
        var bridge = new InputBridge(bindings, panic, foreground);
        var hooks = new HookHost(bridge);

        var chords = new ChordRouter(overlay, panic, log);
        bridge.Connect(new PttRouter(onPtt), menu: null, chords, menuGate: null);

        // Arm() refuses until every subsystem is registered, so a new one cannot silently miss the
        // kill switch. Capture and audio are not built yet and register as explicit no-ops rather
        // than being left out, which is the difference between "nothing to suspend" and "forgot".
        panic.Register(PanicSubsystem.Hotkeys, hooks);
        panic.Register(PanicSubsystem.OverlayDrawing, overlay);
        panic.Register(PanicSubsystem.ScreenCapture, NotBuiltYet.Instance);
        panic.Register(PanicSubsystem.AudioCapture, NotBuiltYet.Instance);
        panic.Register(PanicSubsystem.TrayIndicator, (ISuspendable?)tray ?? NotBuiltYet.Instance);
        panic.Arm();

        hooks.Start();
        log.Info(hooks.IsRunning
            ? $"Input armed. PTT {Label(bindings, BindingAction.Ptt)}, board {Label(bindings, BindingAction.Board)}, panic {Label(bindings, BindingAction.Panic)}."
            : "Input did NOT arm: the low-level hook is not installed.");

        return new InputComposition(bridge, panic, hooks, log);
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
        _log.Info("Input disarmed.");
    }

    /// <summary>Carries the two PTT edges out to whoever is listening. No key code, ever.</summary>
    private sealed class PttRouter(Action<bool> onPtt) : IPttSink
    {
        public void PttDown(DateTimeOffset at) => onPtt(true);

        public void PttUp(DateTimeOffset at) => onPtt(false);
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
