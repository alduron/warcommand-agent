namespace WarCommand.Agent.Core.Model;

/// <summary>One captured point on a request, with the label its type gave that ordinal.</summary>
public sealed record BoardPoint(int Ordinal, string Label, MapPoint Point);

/// <summary>
/// One request as the overlay and the slot allocator need it. Board order is always ascending by
/// <see cref="Slot"/>; priority, age and type never affect order.
/// </summary>
/// <remarks>
/// A row claimed by another participant stays visible to same-role subscribers on the secondary
/// strip, dimmed, holds no slot (<see cref="Slot"/> is null) and is never claimable by digit.
/// A row claimed by the viewer keeps its slot.
/// </remarks>
public sealed record BoardRow
{
    /// <summary>Server request id. Stable for the whole visible life of the row.</summary>
    public required Guid Id { get; init; }

    /// <summary>Deployment the request belongs to. Immutable server-side; a row never moves.</summary>
    public required Guid DeploymentId { get; init; }

    /// <summary>Per group, never resets. 'MTR-14'.</summary>
    public required string TicketCode { get; init; }

    /// <summary>Catalog request type id.</summary>
    public required string TypeId { get; init; }

    /// <summary>The catalog's overlay_label for the type. Rendered, never derived from TypeId.</summary>
    public required string OverlayLabel { get; init; }

    /// <summary>Roles this request was addressed to.</summary>
    public required IReadOnlyList<string> TargetRoleIds { get; init; }

    public required Priority Priority { get; init; }

    /// <summary>Catalog modifier ids. Quantity is never a modifier.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Null on a type with no takes_quantity. Never inferred from a modifier.</summary>
    public int? QuantityRequested { get; init; }

    /// <summary>Short of QuantityRequested re-opens the remainder under the same ticket.</summary>
    public int? QuantityDelivered { get; init; }

    /// <summary>Every point the type's arity required, in ordinal order.</summary>
    public required IReadOnlyList<BoardPoint> Points { get; init; }

    public required Guid RequestedByParticipantId { get; init; }

    public required string RequestedByCallsign { get; init; }

    /// <summary>Participants attached by submit-time coalescing. Renders as 'MORTAR x5'.</summary>
    public int CoRequesterCount { get; init; }

    public required RequestState State { get; init; }

    /// <summary>Null unless the row is claimed or in progress.</summary>
    public string? ClaimantCallsign { get; init; }

    /// <summary>Null unless the row is claimed or in progress.</summary>
    public Guid? ClaimantParticipantId { get; init; }

    /// <summary>Server wall clock. Render <c>ExpiresAt + clock_offset</c>, never this value raw.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Server wall clock. The tiebreak in the admission rule (priority DESC, created_at ASC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optimistic concurrency token. Sent on every claim and transition.</summary>
    public required int Version { get; init; }

    /// <summary>Increments on every released event, by any reason. Renders as 'RETRY x2'.</summary>
    public int ReleaseCount { get; init; }

    /// <summary>Paired spotter request, or null.</summary>
    public Guid? RelatedRequestId { get; init; }

    /// <summary>The request this one corrected, or null.</summary>
    public Guid? SupersedesRequestId { get; init; }

    /// <summary>Requester note, 120 characters or fewer.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Digit 1..9 this row answers to, or null when it holds none: overflow, demoted, or claimed by
    /// somebody else. Never re-assigned while the row holds it.
    /// </summary>
    public int? Slot { get; init; }

    /// <summary>Client-side, per viewer. A muted requester's rows are hidden on this board only.</summary>
    public bool Muted { get; init; }

    /// <summary>Set when the requester was later seen further than moved_threshold_units from this point.</summary>
    public bool RequesterMoved { get; init; }

    /// <summary>True while the row holds a claimable digit.</summary>
    public bool HoldsSlot => Slot.HasValue;

    /// <summary>Only an open row holding a digit may be claimed by voice or by keypad.</summary>
    public bool ClaimableByDigit => Slot.HasValue && State == RequestState.Open;

    public bool IsOpen => State == RequestState.Open;

    public bool IsHeld => State is RequestState.Claimed or RequestState.InProgress;

    public bool IsTerminal => State is RequestState.Completed or RequestState.Cancelled or RequestState.Expired;

    /// <summary>The lowest confidence any point reported, or null when no point reports one.</summary>
    public decimal? MinPointConfidence
    {
        get
        {
            decimal? min = null;
            foreach (var p in Points)
            {
                if (p.Point.Confidence is { } c && (min is null || c < min))
                {
                    min = c;
                }
            }

            return min;
        }
    }

    public bool IsClaimedBy(Guid participantId) => ClaimantParticipantId == participantId;

    /// <summary>A row held by anybody else renders dim on the secondary strip and holds no slot.</summary>
    public bool RendersOnSecondaryStrip(Guid viewerParticipantId) => IsHeld && !IsClaimedBy(viewerParticipantId);

    /// <summary>
    /// LOW CONF treatment. Never renders for a source that reports no confidence.
    /// The threshold is point_confidence.warn from the game profile, never a constant here.
    /// </summary>
    public bool IsLowConfidence(decimal warnThreshold) => MinPointConfidence is { } c && c < warnThreshold;

    /// <summary>Drops the digit without touching anything else. Used on claim by another participant.</summary>
    public BoardRow WithoutSlot() => Slot is null ? this : this with { Slot = null };

    /// <summary>Assigns a digit. The allocator owns which one.</summary>
    public BoardRow WithSlot(int slot) => this with { Slot = slot };
}
