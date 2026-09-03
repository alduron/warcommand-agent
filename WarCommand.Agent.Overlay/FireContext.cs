using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Everything a row needs to draw an opening bracket: where the gun is, what it is, and the
/// tables. Null on every surface where the viewer is not on a gun, which is most of them.
/// </summary>
/// <remarks>
/// Always a BRACKET and never a firing solution. The tables are player-measured, the L81 groups at
/// 50 MOA, and there is no altitude anywhere in the system, so the answer is flat-earth and wrong
/// on slopes. Every row that carries one also carries ADJUST FROM SPOTTER.
/// </remarks>
/// <param name="Gun">Where the viewer's gun is, and when it was set.</param>
/// <param name="RoleId">The role the gun fills, so only that role's rows draw a bracket.</param>
/// <param name="Ballistics">The served firing tables.</param>
/// <param name="Profile">The served game profile, for the map scale.</param>
/// <param name="MapId">The loaded map, or null when unknown: the range then stays in map units.</param>
public sealed record FireContext(
    GunPosition Gun,
    string RoleId,
    Ballistics Ballistics,
    GameProfile Profile,
    string? MapId);
