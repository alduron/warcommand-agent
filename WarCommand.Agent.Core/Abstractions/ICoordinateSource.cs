using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Abstractions;

/// <summary>
/// One way of obtaining the coordinate the requester is pointing at. Screen capture is one
/// implementation among several, never the mechanism: everything above this interface has never
/// heard of a glyph atlas.
/// </summary>
/// <remarks>
/// An implementation returns null rather than throwing. Null means "no answer right now" and must
/// never be turned into a default: the registry moves on to the next source.
/// </remarks>
public interface ICoordinateSource
{
    /// <summary>Written verbatim to request_points.source. Must match an id in coordinate_sources.known.</summary>
    string Id { get; }

    /// <summary>Ask order, 0 first. Comes from game-profile.json, never hardcoded.</summary>
    int Priority { get; }

    /// <summary>False when the source is off: capture disabled, no microphone, feature-flagged away.</summary>
    bool IsAvailable { get; }

    /// <summary>The coordinate, or null for no answer. Never throws to signal absence.</summary>
    Task<MapPoint?> TryReadAsync(CancellationToken ct);
}
