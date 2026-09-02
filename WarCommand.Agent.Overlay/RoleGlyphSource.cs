using System.Windows.Media;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Resolves a role id to the glyph the overlay draws, from whichever catalog is current.
/// </summary>
/// <remarks>
/// The catalog is refetched, so the glyph set changes under a running process. Handing the view a
/// resolver rather than a snapshot is what lets an edited glyph reach the next frame.
/// </remarks>
public sealed class RoleGlyphSource
{
    private readonly Func<string, RoleDef?> _lookup;
    private readonly Dictionary<string, (Geometry? First, Geometry? Second)> _cache = new(StringComparer.Ordinal);

    public RoleGlyphSource(Func<string, RoleDef?> lookup) => _lookup = lookup;

    /// <summary>Nothing to draw. Used before a catalog is loaded and by the design-time view.</summary>
    public static RoleGlyphSource Empty { get; } = new(_ => null);

    public (Geometry? First, Geometry? Second) Geometry(string roleId)
    {
        if (string.IsNullOrEmpty(roleId))
        {
            return (null, null);
        }

        if (_cache.TryGetValue(roleId, out var hit))
        {
            return hit;
        }

        var parsed = RoleGlyph.Of(_lookup(roleId));
        _cache[roleId] = parsed;
        return parsed;
    }

    public string BrushKey(string roleId) => RoleGlyph.BrushKey(_lookup(roleId)?.ColorGroup);

    /// <summary>The role's served display name. Falls back to the id so a row is never blank.</summary>
    public string Display(string roleId) => _lookup(roleId)?.Display ?? roleId;

    /// <summary>Drops what it parsed. Called when a new catalog lands.</summary>
    public void Invalidate() => _cache.Clear();
}
