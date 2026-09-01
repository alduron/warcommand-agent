using System.Threading;
using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Dev;

/// <summary>
/// Yields a scripted, cycling list of coordinates instead of reading the game. It has no platform
/// dependency, same as every other <see cref="ICoordinateSource"/>: this is the whole point of the
/// interface. See <c>Convention_WarCommandCoordinateAcquisitionIsBehindAnInterface</c>.
/// </summary>
/// <remarks>
/// The composition root must construct this only under the dev profile. Nothing here enforces that;
/// it is a wiring decision, not a runtime guard, because a runtime guard would be one more fact a
/// production build has to carry for a class production never touches.
/// </remarks>
public sealed class FakeCoordinateSource : ICoordinateSource
{
    /// <summary>Written verbatim to request_points.source on every point this source produces.</summary>
    public const string SourceId = "dev_fake";

    private static readonly IReadOnlyList<MapPoint> DefaultScript =
    [
        new MapPoint(85.53m, 69.42m, SourceId, RawText: null, Confidence: null),
        new MapPoint(84.10m, 70.88m, SourceId, RawText: null, Confidence: null),
        new MapPoint(88.00m, 61.20m, SourceId, RawText: null, Confidence: null),
        new MapPoint(91.44m, 58.02m, SourceId, RawText: null, Confidence: null),
        new MapPoint(50.00m, 50.00m, SourceId, RawText: null, Confidence: null),
    ];

    private readonly IReadOnlyList<MapPoint> _script;
    private int _index = -1;

    /// <param name="script">The points to cycle through, in order. Defaults to a fixed five-point script.</param>
    /// <param name="priority">From <see cref="Priority"/>. Defaults to 0: asked first.</param>
    public FakeCoordinateSource(IReadOnlyList<MapPoint>? script = null, int priority = 0)
    {
        if (script is { Count: > 0 })
        {
            _script = script;
        }
        else
        {
            _script = DefaultScript;
        }

        Priority = priority;
    }

    public string Id => SourceId;

    public int Priority { get; }

    /// <summary>Always true. There is no game, no microphone and no capture pipeline to fail here.</summary>
    public bool IsAvailable => true;

    /// <summary>Never null: the whole point is to answer instantly so the flow above it can be exercised.</summary>
    public Task<MapPoint?> TryReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var i = Interlocked.Increment(ref _index);
        var point = _script[(int)((uint)i % (uint)_script.Count)];
        return Task.FromResult<MapPoint?>(point with { Source = SourceId });
    }
}
