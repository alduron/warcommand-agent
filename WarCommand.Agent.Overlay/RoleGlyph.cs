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

    /// <summary>Both paths of one role's glyph, in draw order.</summary>
    public static (Geometry? First, Geometry? Second) Of(RoleDef? role)
    {
        if (role?.Icon is null)
        {
            return (null, null);
        }

        return (Parse(role.Icon.D1), Parse(role.Icon.D2));
    }
}
