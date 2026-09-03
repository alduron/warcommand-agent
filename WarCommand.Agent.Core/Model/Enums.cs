namespace WarCommand.Agent.Core.Model;

/// <summary>
/// Request states, matching ops.request_state. Release is an edge back to Open, never a state.
/// There is no Abandoned member and there never may be.
/// </summary>
public enum RequestState
{
    Open,
    Claimed,
    InProgress,
    Completed,
    Cancelled,
    Expired,
}

/// <summary>Admission key when a slot frees. Never a sort key: the board sorts by slot ascending.</summary>
public enum Priority
{
    Low,
    Normal,
    Urgent,
}

/// <summary>Outcome on a completed event. Unable is the one outcome that is not terminal.</summary>
public enum Outcome
{
    Serviced,
    NoLongerRequired,

    /// <summary>Returns the request to Open. The frame carries the full request body.</summary>
    Unable,
}

/// <summary>Reason on a released event. Unable is deliberately not a member: it is an Outcome.</summary>
public enum ReleaseReason
{
    Voluntary,
    Timeout,
    NotInGame,
}

/// <summary>
/// Rows in ops.request_events. RoundsAway, Adjusted and Escalated change no state.
/// There is no Abandoned kind, ever.
/// </summary>
public enum RequestEventKind
{
    Submitted,
    Claimed,
    Started,
    RoundsAway,
    Adjusted,
    Completed,
    Released,
    Cancelled,
    Superseded,
    Expired,
    StoodDown,
}

/// <summary>Spotter correction direction. Its own position class; never an initial-class token.</summary>
#pragma warning disable CA1720 // 'Short' is the wire value in contracts/events.md, not a type name.
public enum AdjustDirection
{
    Over,
    Short,
    Left,
    Right,
}
#pragma warning restore CA1720

/// <summary>Who an escalation widened the audience to. No state change either way.</summary>
public enum EscalationLevel
{
    Role,
    Deployment,
}

/// <summary>Why the participant landed on this deployment.</summary>
public enum DeploymentEnterSource
{
    Detector,
    Override,
    Invite,

    /// <summary>
    /// A restarted match replaced the one this device was standing in.
    /// </summary>
    /// <remarks>
    /// The server sends this and the agent's enum did not hold it. JsonStringEnumConverter is
    /// configured with allowIntegerValues false, so an unknown value THROWS and the whole frame is
    /// dropped: after a match restart the agent stayed on the closed deployment and never hopped.
    /// contracts/events.md and 08-api-realtime.md both document 'default' here and omit this,
    /// which is where the mistake came from.
    /// </remarks>
    Supersede,

    Default,
}

/// <summary>Why a membership or visitor participation ended.</summary>
public enum MembershipEndReason
{
    Expired,
    Kicked,
    Banned,
}

/// <summary>
/// Reported on the presence heartbeat. Idle sustained past one grace interval releases claims
/// with ReleaseReason.NotInGame; a single idle frame does nothing.
/// </summary>
public enum PresenceState
{
    InGame,
    Idle,
}
