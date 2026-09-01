namespace WarCommand.Agent.Input;

/// <summary>
/// Everything one Panic press moves. Adding a member here breaks <see cref="PanicSwitch.Arm"/> until
/// the composition root registers it, which is what stops the suspend set drifting out of step.
/// </summary>
public enum PanicSubsystem
{
    /// <summary>Hotkey grabs. Only Panic itself stays armed.</summary>
    Hotkeys = 0,

    /// <summary>Screen capture sessions and frame grabs.</summary>
    ScreenCapture,

    /// <summary>Overlay drawing. The layered window stops rendering.</summary>
    OverlayDrawing,

    /// <summary>Audio capture. The device is released and the buffer zeroed.</summary>
    AudioCapture,

    /// <summary>The tray icon, which goes grey while suspended.</summary>
    TrayIndicator,
}

/// <summary>
/// One signal, fanned out. Panic suspends hotkey grabs, capture, overlay drawing and audio capture
/// in one press and turns the tray icon grey; pressing again resumes them, and every subsystem
/// re-derives its state rather than trusting what it held.
/// </summary>
/// <remarks>
/// A single switch rather than a flag per subsystem: four flags is four things a later change can
/// leave half set, and half a kill switch is not a kill switch.
/// </remarks>
public sealed class PanicSwitch
{
    private static readonly PanicSubsystem[] Order = Enum.GetValues<PanicSubsystem>();

    private readonly Dictionary<PanicSubsystem, ISuspendable> _registered = [];
    private readonly IInputLog _log;

    /// <summary>Creates an unarmed switch.</summary>
    public PanicSwitch(IInputLog? log = null) => _log = log ?? NullInputLog.Instance;

    /// <summary>Raised after every toggle, with the new suspended state.</summary>
    public event EventHandler<bool>? Toggled;

    /// <summary>True while Panic is engaged.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>True once every <see cref="PanicSubsystem"/> has a registration.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>Points a subsystem at the switch. The last registration for a subsystem wins.</summary>
    public void Register(PanicSubsystem subsystem, ISuspendable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _registered[subsystem] = target;
    }

    /// <summary>
    /// Refuses to arm until every subsystem is registered, naming the ones that are not. The agent
    /// must not run with a partial kill switch.
    /// </summary>
    public void Arm()
    {
        var missing = Order.Where(s => !_registered.ContainsKey(s)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Panic would not reach: " + string.Join(", ", missing));
        }

        IsArmed = true;
    }

    /// <summary>
    /// One press. Suspends in declaration order, hotkeys first, and resumes in reverse so input is
    /// the last thing to come back.
    /// </summary>
    public bool Toggle()
    {
        if (!IsArmed)
        {
            throw new InvalidOperationException("Panic is not armed. Call Arm() once every subsystem is registered.");
        }

        IsSuspended = !IsSuspended;

        if (IsSuspended)
        {
            foreach (var subsystem in Order)
            {
                _registered[subsystem].Suspend();
            }

            _log.Note(InputEvent.PanicEngaged);
        }
        else
        {
            for (var i = Order.Length - 1; i >= 0; i--)
            {
                _registered[Order[i]].Resume();
            }

            _log.Note(InputEvent.PanicReleased);
        }

        Toggled?.Invoke(this, IsSuspended);
        return IsSuspended;
    }
}
