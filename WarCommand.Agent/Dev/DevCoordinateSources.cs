using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Dev;

namespace WarCommand.Agent.Dev;

/// <summary>
/// Builds the coordinate-source configuration for the dev profile only. Real deployments describe
/// <see cref="CoordinateSourcesSection"/> in the served game-profile.json; this is the one place
/// that instead builds one in memory, entirely locally, so <see cref="FakeCoordinateSource"/> is
/// never asked outside a dev launch and contracts/game-profile.json never has to know it exists.
/// </summary>
public static class DevCoordinateSources
{
    /// <summary>The dev profile's whole coordinate-source list: the scripted source, asked first.</summary>
    public static CoordinateSourcesSection FakeOnly() => new()
    {
        Enabled = [FakeCoordinateSource.SourceId],
        Priority = [FakeCoordinateSource.SourceId],
        Known =
        [
            new CoordinateSourceDef
            {
                Id = FakeCoordinateSource.SourceId,
                Display = "Dev scripted (local only)",
                Gives = "A cycling list of test coordinates. No game, no capture, no microphone.",
                Status = "dev",
            },
        ],
    };
}
