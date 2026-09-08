namespace WarCommand.Agent.Core.Model;

/// <summary>
/// Request states, matching ops.request_state. Release is an edge back to Open, never a state.
/// Abandoned records no route: a withdrawal, a TTL and a stand-down are the same state.
/// </summary>
public enum RequestState
{
    Open,
    Claimed,
    InProgress,
    Completed,
    Abandoned,
}

/// <summary>Admission key when a slot frees. Never a sort key: the board sorts by slot ascending.</summary>
public enum Priority
{
    Low,
    Normal,
    Urgent,
}

/// <summary>Reason on a released event. Release hands a row back and is never an ending.</summary>
public enum ReleaseReason
{
    Voluntary,
    Timeout,
    NotInGame,
}

/// <summary>
/// Rows in ops.request_events. Escalated changes no state. Completion carries no outcome.
/// </summary>
public enum RequestEventKind
{
    Submitted,
    Claimed,
    Started,
    Completed,
    Released,
    Abandoned,
    Superseded,
    Reopened,
}

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
