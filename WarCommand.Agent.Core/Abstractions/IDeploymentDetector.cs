namespace WarCommand.Agent.Core.Abstractions;

/// <summary>
/// Answers which server the player is on, and which map. The composition root sorts detectors by
/// <see cref="Priority"/> and takes the first non-null answer.
/// </summary>
/// <remarks>
/// Null means "no answer right now", which is not the same as "not in a game" and never moves
/// anybody: the failure mode of guessing is putting a fire mission on the board of a match the
/// requester is not in. A null map is likewise not a default map.
/// </remarks>
public interface IDeploymentDetector
{
    string Name { get; }

    /// <summary>Ask order, 0 first. Comes from game-profile.json, never hardcoded.</summary>
    int Priority { get; }

    /// <summary>An opaque server key reported on the presence heartbeat, or null.</summary>
    Task<string?> CurrentServerKeyAsync(CancellationToken ct);

    /// <summary>A map id from the profile's maps list, or null. Consumed by units_to_meters.</summary>
    Task<string?> CurrentMapAsync(CancellationToken ct);
}
