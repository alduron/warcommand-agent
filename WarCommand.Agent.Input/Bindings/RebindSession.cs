using WarCommand.Agent.Input.Devices;

namespace WarCommand.Agent.Input.Bindings;

/// <summary>Which of an action's two rows a capture is filling.</summary>
public enum BindingSlot
{
    /// <summary>The key or mouse button.</summary>
    Primary = 0,

    /// <summary>The HOTAS button. Keyboard presses are ignored by a capture on this row.</summary>
    Secondary,
}

/// <summary>Where a rebind capture stands.</summary>
public enum RebindState
{
    /// <summary>Waiting for the next key or mouse button.</summary>
    Capturing = 0,

    /// <summary>A chord was accepted and applied.</summary>
    Captured,

    /// <summary>Five seconds passed with no acceptable press.</summary>
    Aborted,

    /// <summary>The user closed the dialog.</summary>
    Canceled,
}

/// <summary>What one offered press did.</summary>
public enum RebindOutcome
{
    /// <summary>The press was not a bindable key. Nothing happened and capture continues.</summary>
    Ignored = 0,

    /// <summary>Applied to the set.</summary>
    Captured,

    /// <summary>Another WarCommand binding holds it. Refused, and the other one is named.</summary>
    RefusedConflict,

    /// <summary>The session is no longer capturing.</summary>
    NotCapturing,
}

/// <summary>
/// Captures the next key or mouse button for one action, with a five second abort. Pure: the caller
/// supplies <c>now</c>, so the timeout is testable with no clock.
/// </summary>
public sealed class RebindSession
{
    /// <summary>The abort window. Five seconds of no acceptable press ends the capture.</summary>
    public static readonly TimeSpan AbortAfter = TimeSpan.FromSeconds(5);

    private readonly BindingSet _bindings;
    private readonly DateTimeOffset _deadline;

    /// <summary>Begins capturing for one action, on one of its two rows.</summary>
    public RebindSession(
        BindingSet bindings,
        BindingAction action,
        DateTimeOffset startedAt,
        BindingSlot slot = BindingSlot.Primary)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        Action = action;
        Slot = slot;
        _deadline = startedAt + AbortAfter;
    }

    /// <summary>The action being rebound.</summary>
    public BindingAction Action { get; }

    /// <summary>Which row is being filled. A row only accepts the kind of press it holds.</summary>
    public BindingSlot Slot { get; }

    /// <summary>Where the capture stands.</summary>
    public RebindState State { get; private set; } = RebindState.Capturing;

    /// <summary>The binding that refused the last offer, or <see cref="BindingAction.None"/>.</summary>
    public BindingAction ConflictsWith { get; private set; } = BindingAction.None;

    /// <summary>
    /// Always false, and shown alongside every capture. We cannot read the game's bind file, so a
    /// collision with the game is undetectable and the dialog says so instead of implying a check.
    /// </summary>
    public static bool ChecksAgainstTheGame => BindingSet.CanDetectGameConflicts;

    /// <summary>Offers a press. A conflict is refused and leaves the session capturing.</summary>
    public RebindOutcome Offer(Chord chord, DateTimeOffset now)
    {
        if (Tick(now) != RebindState.Capturing)
        {
            return RebindOutcome.NotCapturing;
        }

        // A HOTAS row waiting for a stick button ignores the keyboard outright. Taking whichever
        // press arrived first meant a hand resting on the keyboard filled the row the user had
        // opened precisely because they were not at the keyboard.
        if (Slot != BindingSlot.Primary || !chord.IsBound)
        {
            return RebindOutcome.Ignored;
        }

        var result = _bindings.Rebind(Action, chord);
        if (result.Status == RebindStatus.RefusedConflict)
        {
            ConflictsWith = result.ConflictsWith;
            return RebindOutcome.RefusedConflict;
        }

        if (!result.Applied)
        {
            return RebindOutcome.Ignored;
        }

        ConflictsWith = BindingAction.None;
        State = RebindState.Captured;
        return RebindOutcome.Captured;
    }

    /// <summary>Offers a controller button. Ignored by a capture on the keyboard row.</summary>
    public RebindOutcome Offer(DeviceButton button, DateTimeOffset now)
    {
        if (Tick(now) != RebindState.Capturing)
        {
            return RebindOutcome.NotCapturing;
        }

        if (Slot != BindingSlot.Secondary || !button.IsBound)
        {
            return RebindOutcome.Ignored;
        }

        var result = _bindings.RebindSecondary(Action, button);
        if (result.Status == RebindStatus.RefusedConflict)
        {
            ConflictsWith = result.ConflictsWith;
            return RebindOutcome.RefusedConflict;
        }

        if (!result.Applied)
        {
            return RebindOutcome.Ignored;
        }

        ConflictsWith = BindingAction.None;
        State = RebindState.Captured;
        return RebindOutcome.Captured;
    }

    /// <summary>Advances the abort timer.</summary>
    public RebindState Tick(DateTimeOffset now)
    {
        if (State == RebindState.Capturing && now >= _deadline)
        {
            State = RebindState.Aborted;
        }

        return State;
    }

    /// <summary>Closes the capture without changing anything.</summary>
    public void Cancel()
    {
        if (State == RebindState.Capturing)
        {
            State = RebindState.Canceled;
        }
    }
}
