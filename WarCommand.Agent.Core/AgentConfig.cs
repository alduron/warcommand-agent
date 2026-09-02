using System.Text.Json.Serialization;

namespace WarCommand.Agent.Core;

/// <summary>What the overlay does while the game is not the foreground window.</summary>
public enum UnfocusedOverlayMode
{
    /// <summary>The default. A topmost layered window otherwise draws coordinates over Discord.</summary>
    Hide,

    /// <summary>For a deliberate second-monitor setup.</summary>
    Dim,
}

/// <summary>
/// The signed-in user, mirroring the contract's UserOut. No email: there is no email anywhere in
/// WarCommand.
/// </summary>
public sealed record ConfigUser
{
    public required Guid Id { get; init; }

    /// <summary>The account callsign, the one name a person has.</summary>
    public required string Callsign { get; init; }

    public string? AuthProvider { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>The deployment this membership is currently on.</summary>
public sealed record ConfigDeployment
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// How the browser addresses it. The web routes on /g/{group}/d/{deployment} and never on a
    /// uuid, so the id alone cannot build a link somebody can open.
    /// </summary>
    public string? Slug { get; init; }

    public int MemberCount { get; init; }

    /// <summary>Six digits, rendered in the overlay header. Re-emitted on deployment.roster when rotated.</summary>
    public string? InviteCode { get; init; }
}

/// <summary>One group the device is paired into.</summary>
public sealed record ConfigMembership
{
    public required Guid MembershipId { get; init; }

    public required Guid GroupId { get; init; }

    public required string GroupName { get; init; }

    /// <summary>The group half of a web URL. See <see cref="ConfigDeployment.Slug"/>.</summary>
    public string? GroupSlug { get; init; }

    public required string Callsign { get; init; }

    /// <summary>'owner', 'admin', 'member'.</summary>
    public required string Permission { get; init; }

    /// <summary>Grants the command role and board.read.all. Never self-service.</summary>
    public bool CanCommand { get; init; }

    /// <summary>'member' or 'visitor'.</summary>
    public string ParticipantKind { get; init; } = "member";

    /// <summary>Null when the member is on no deployment. The board stays empty; that is not a fault.</summary>
    public ConfigDeployment? Deployment { get; init; }

    public IReadOnlyList<string> SubscribedRoleIds { get; init; } = [];

    /// <summary>Roles the group has switched on. The grammar compiles from these.</summary>
    public IReadOnlyList<string> EnabledRoleIds { get; init; } = [];

    public double RequestTtlMultiplier { get; init; } = 1.0;
}

/// <summary>
/// Everything the user chose on this machine. Persisted beside the server payload in config.json.
/// </summary>
public sealed record LocalSettings
{
    /// <summary>
    /// No shipped default: first run makes the user pick, and the choice is reported to the server
    /// so every web surface names the key they actually hold.
    /// </summary>
    public string? PttBinding { get; init; }

    /// <summary>Rebindable, never unbindable. A kill switch with no key is not a kill switch.</summary>
    public string PanicBinding { get; init; } = "RightAlt+P";

    /// <summary>Null means the system default device.</summary>
    public string? InputDeviceId { get; init; }

    /// <summary>Separate from the input device, because game audio and comms land in different places.</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>Off is a real mode: the microphone is never opened and key-down goes straight to the menu.</summary>
    public bool VoiceEnabled { get; init; } = true;

    /// <summary>Opt-in. Off at first run, per the M1/M2 split.</summary>
    public bool ScreenCaptureEnabled { get; init; }

    public bool SoundsEnabled { get; init; } = true;

    public double MasterVolume { get; init; } = 1.0;

    /// <summary>Event ids the user muted individually.</summary>
    public IReadOnlyList<string> MutedSoundEvents { get; init; } = [];

    /// <summary>Requesters hidden on this board only. Client-side, instant, needs no permission.</summary>
    public IReadOnlyList<Guid> MutedParticipantIds { get; init; } = [];

    public string OverlayAnchor { get; init; } = "top_left";

    public double OverlayWidthScale { get; init; } = 1.0;

    public double OverlayOpacity { get; init; } = 1.0;

    /// <summary>Swaps the green only. Urgent keeps its red.</summary>
    public bool ColorblindTheme { get; init; }

    public bool SecondScreenMode { get; init; }

    public UnfocusedOverlayMode OverlayWhenUnfocused { get; init; } = UnfocusedOverlayMode.Hide;

    /// <summary>Overrides grammar_rules.min_intent_confidence. Null keeps the catalog value.</summary>
    public double? IntentConfidenceFloor { get; init; }

    /// <summary>Shows what was heard for every utterance, not only failures.</summary>
    public bool RecognizedTextFeedback { get; init; }

    /// <summary>Clobbers the clipboard on claim. One toggle, defaults on.</summary>
    public bool AutoCopyOnClaim { get; init; } = true;

    /// <summary>A format id from coordinate_handoff in the game profile. Null uses its default_format.</summary>
    public string? ClipboardFormatId { get; init; }

    /// <summary>Tray map override. Null means Auto, which asks the detectors.</summary>
    public string? ManualMapId { get; init; }
}

/// <summary>
/// config.json: the payload from the API plus this machine's settings.
/// </summary>
/// <remarks>
/// This type never holds a token. The agent and refresh tokens live in tokens.dat under DPAPI
/// CurrentUser scope, and adding a token field here would write one to a plaintext file.
/// </remarks>
public sealed record AgentConfig
{
    /// <summary>Increments on every server-side change. Compared against ready.config_version.</summary>
    public required int ConfigVersion { get; init; }

    public required ConfigUser User { get; init; }

    /// <summary>Empty is normal: an unpaired or group-less agent idles waiting for six digits.</summary>
    public IReadOnlyList<ConfigMembership> Memberships { get; init; } = [];

    /// <summary>Sent as If-None-Match on GET /v1/catalog/request-types.</summary>
    public string? CatalogEtag { get; init; }

    public required Uri RealtimeUrl { get; init; }

    /// <summary>This machine's settings. Never sent to the server except the PTT binding.</summary>
    public LocalSettings Local { get; init; } = new();

    /// <summary>The cold-start state. No error, no retry loop, no nag.</summary>
    [JsonIgnore]
    public bool BelongsToNothing => Memberships.Count == 0;

    public ConfigMembership? MembershipForGroup(Guid groupId) =>
        Memberships.FirstOrDefault(m => m.GroupId == groupId);

    public ConfigMembership? MembershipForDeployment(Guid deploymentId) =>
        Memberships.FirstOrDefault(m => m.Deployment?.Id == deploymentId);

    /// <summary>Every role the grammar compiler must build a vocabulary for.</summary>
    public IReadOnlyList<string> AllEnabledRoleIds =>
        [.. Memberships.SelectMany(m => m.EnabledRoleIds).Distinct(StringComparer.Ordinal)];

    public bool IsMuted(Guid participantId) => Local.MutedParticipantIds.Contains(participantId);
}
