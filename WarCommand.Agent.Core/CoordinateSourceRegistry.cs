using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core;

/// <summary>
/// Asks each enabled coordinate source in priority order at PTT key-down and takes the first
/// non-null answer. Both lists come from coordinate_sources in game-profile.json, so reordering
/// them, or disabling one after a bad game patch, is a served-data change rather than a release.
/// </summary>
/// <remarks>
/// Adding a source is a class implementing <see cref="ICoordinateSource"/> plus two lines of
/// profile. A registered source absent from <c>enabled</c> is never asked; a source that is
/// unavailable right now is skipped without ending the sweep.
/// </remarks>
public sealed class CoordinateSourceRegistry
{
    private readonly IReadOnlyList<ICoordinateSource> _registered;
    private IReadOnlyList<ICoordinateSource> _ordered;

    public CoordinateSourceRegistry(IEnumerable<ICoordinateSource> sources, CoordinateSourcesSection config)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(config);

        _registered = [.. sources];

        var duplicate = _registered
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Two coordinate sources registered under the id '{duplicate.Key}'.",
                nameof(sources));
        }

        _ordered = Order(_registered, config);
    }

    /// <summary>Every registered source, whether the profile enables it or not.</summary>
    public IReadOnlyList<ICoordinateSource> Registered => _registered;

    /// <summary>The enabled sources in ask order. Rebuilt whenever the profile changes.</summary>
    public IReadOnlyList<ICoordinateSource> Ordered => _ordered;

    /// <summary>Applies a newly adopted game profile. Called after every profile change.</summary>
    public void Reconfigure(CoordinateSourcesSection config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _ordered = Order(_registered, config);
    }

    /// <summary>
    /// The first non-null answer from an available source, or null when nothing answered. Null is
    /// never a default coordinate: the caller submits nothing.
    /// </summary>
    public async Task<MapPoint?> ReadAsync(CancellationToken ct)
    {
        foreach (var source in _ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.IsAvailable)
            {
                continue;
            }

            var point = await source.TryReadAsync(ct).ConfigureAwait(false);
            if (point is null)
            {
                continue;
            }

            // Provenance is the registry's guarantee, not the implementation's good manners.
            return string.Equals(point.Source, source.Id, StringComparison.Ordinal)
                ? point
                : point with { Source = source.Id };
        }

        return null;
    }

    private static IReadOnlyList<ICoordinateSource> Order(
        IReadOnlyList<ICoordinateSource> registered,
        CoordinateSourcesSection config) =>
        [.. registered
            .Where(s => config.IsEnabled(s.Id))
            .OrderBy(s => config.PriorityOf(s.Id))
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
}
