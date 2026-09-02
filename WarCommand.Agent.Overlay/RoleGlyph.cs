using System.Windows;
using System.Windows.Media;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// The served role glyph, as WPF geometry and a brush. Same paths and hues the web draws.
/// </summary>
/// <remarks>
/// A glyph is catalog data, so it is untrusted string input: an unparseable path yields no geometry
/// and the row renders without it, never an exception on the UI thread.
/// </remarks>
public static class RoleGlyph
{
    /// <summary>Falls back to the neutral command hue for a group the overlay does not know.</summary>
    public static string BrushKey(string? colorGroup) => colorGroup switch
    {
        "fire" => "RoleFire",
        "recon" => "RoleRecon",
        "move" => "RoleMove",
        "build" => "RoleBuild",
        "medic" => "RoleMedic",
        _ => "RoleCommand",
    };

    /// <summary>Null when the role has no glyph or its path does not parse.</summary>
    public static Geometry? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        try
        {
            var geometry = Geometry.Parse(d);
            geometry.Freeze();
            return geometry;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The glyph's design box. Every served icon is drawn on 24x24, stroke only.</summary>
    public const double Box = 24.0;

    /// <summary>Both paths of one role's glyph, in draw order, centred on the box.</summary>
    /// <remarks>
    /// The ink is centred, not the box. The served icons are not drawn centred in their own 24x24:
    /// mortar is an arc from y9.5 to y20, whose middle is nearly three units below the box middle,
    /// so centring the Canvas still draws the icon low against its label. Centring here fixes every
    /// icon at once, including one edited in the catalog after this ships, which is the point of
    /// the glyph being served rather than compiled.
    /// </remarks>
    public static (Geometry? First, Geometry? Second) Of(RoleDef? role)
    {
        if (role?.Icon is null)
        {
            return (null, null);
        }

        var first = ParseOpen(role.Icon.D1);
        var second = ParseOpen(role.Icon.D2);

        var ink = Rect.Empty;
        if (first is not null)
        {
            ink.Union(first.Bounds);
        }

        if (second is not null)
        {
            ink.Union(second.Bounds);
        }

        if (!ink.IsEmpty)
        {
            var shift = new TranslateTransform(
                (Box / 2) - (ink.X + (ink.Width / 2)),
                (Box / 2) - (ink.Y + (ink.Height / 2)));
            shift.Freeze();

            if (first is not null)
            {
                first.Transform = shift;
            }

            if (second is not null)
            {
                second.Transform = shift;
            }
        }

        first?.Freeze();
        second?.Freeze();
        return (first, second);
    }

    /// <summary>
    /// Parsed into a modifiable clone, so the centring transform can be applied before freezing.
    /// </summary>
    /// <remarks>
    /// Geometry.Parse hands back an already frozen geometry, and setting Transform on it throws
    /// "Cannot set a property on object ... because it is in a read-only state".
    /// </remarks>
    private static Geometry? ParseOpen(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        try
        {
            return Geometry.Parse(d).CloneCurrentValue();
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
