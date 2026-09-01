namespace WarCommand.Agent.Core.Model;

/// <summary>
/// A multi-point request being assembled locally. Never reaches the server until complete, so an
/// abandoned draft leaves no orphan state anywhere.
/// </summary>
/// <remarks>
/// A draft does not survive a deployment change: aborting it is step 0 of the deployment.entered
/// handler, ahead of dropping rows and resetting the allocator.
/// </remarks>
public sealed record Draft
{
    public required string TypeId { get; init; }

    /// <summary>Point count the type requires. From the catalog, never assumed.</summary>
    public required int Arity { get; init; }

    /// <summary>One label per ordinal, from the catalog. 'pickup', 'dropoff'.</summary>
    public required IReadOnlyList<string> PointLabels { get; init; }

    /// <summary>Points captured so far, in ordinal order. The first was snapshotted at key-down.</summary>
    public IReadOnlyList<MapPoint> Points { get; init; } = [];

    /// <summary>
    /// The deployment the points were read in. Asserted at submit as captured_in_deployment_id and
    /// compared server-side, never re-stamped.
    /// </summary>
    public required Guid CapturedInDeploymentId { get; init; }

    /// <summary>Local clock. Past this the draft is discarded silently, awaiting_point_timeout_s after capture.</summary>
    public required DateTimeOffset Deadline { get; init; }

    public Priority Priority { get; init; } = Priority.Normal;

    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Only on a type with takes_quantity.</summary>
    public int? Quantity { get; init; }

    /// <summary>Supply kind or structure kind id, on a type that requires one.</summary>
    public string? Kind { get; init; }

    /// <summary>120 characters or fewer.</summary>
    public string? Note { get; init; }

    /// <summary>Set when this draft corrects the requester's own open request.</summary>
    public Guid? SupersedesRequestId { get; init; }

    public bool IsComplete => Points.Count >= Arity;

    /// <summary>Label of the point still wanted, or null when the draft is complete.</summary>
    public string? NextPointLabel =>
        IsComplete || Points.Count >= PointLabels.Count ? null : PointLabels[Points.Count];

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;

    /// <summary>Appends a point. Throws when the draft already has every point its arity wants.</summary>
    public Draft WithPoint(MapPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (IsComplete)
        {
            throw new InvalidOperationException($"Draft for {TypeId} already holds {Arity} points.");
        }

        return this with { Points = [.. Points, point] };
    }
}
