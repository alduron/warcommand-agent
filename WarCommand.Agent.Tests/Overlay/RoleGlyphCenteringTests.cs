using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The glyph must be centered by its INK, not by its box. Centering the Canvas was not enough and
/// the icon still drew low against its label, because the served icons are not centered inside
/// their own 24x24: mortar is an arc sitting on the bottom edge.
/// </summary>
public class RoleGlyphCenteringTests
{
    private const double Middle = RoleGlyph.Box / 2;

    [Fact]
    public void The_mortar_arc_is_centered_despite_being_drawn_on_the_bottom_edge()
    {
        // Raw art: 'M3 20C7 6 17 6 21 20' plus feet at y20. Its ink middle is near y15, three
        // units below the box middle, which is exactly what was visible beside the label.
        var (first, second) = RoleGlyph.Of(Role("mortar"));

        Assert.NotNull(first);
        Assert.Equal(Middle, Ink(first, second).Y, 1);
    }

    [Fact]
    public void Every_served_role_glyph_is_centered_on_the_box()
    {
        foreach (var role in Catalog().Roles)
        {
            var (first, second) = RoleGlyph.Of(role);
            if (first is null && second is null)
            {
                continue;
            }

            var center = Ink(first, second);
            Assert.True(
                System.Math.Abs(center.X - Middle) < 0.51,
                $"{role.Id} ink is off center horizontally at {center.X:0.00}");
            Assert.True(
                System.Math.Abs(center.Y - Middle) < 0.51,
                $"{role.Id} ink is off center vertically at {center.Y:0.00}");
        }
    }

    [Fact]
    public void Centering_moves_the_ink_without_resizing_it()
    {
        // A Viewbox would have centered it too, by scaling each glyph until its ink filled the box,
        // which makes a small icon huge and a wide one tiny. Sizes must stay comparable.
        var role = Role("mortar");
        var raw = RoleGlyph.Parse(role.Icon!.D1);
        var (first, _) = RoleGlyph.Of(role);

        Assert.NotNull(raw);
        Assert.NotNull(first);
        Assert.Equal(raw.Bounds.Width, first.Bounds.Width, 1);
        Assert.Equal(raw.Bounds.Height, first.Bounds.Height, 1);
    }

    [Fact]
    public void Every_role_icon_is_unique()
    {
        // The header draws roles as a glyph with no label, which only works while no two roles
        // share art. A duplicate would silently make two of the viewer's own roles the same mark.
        var seen = new Dictionary<string, string>(System.StringComparer.Ordinal);

        foreach (var role in Catalog().Roles)
        {
            var art = $"{role.Icon?.D1}|{role.Icon?.D2}";
            Assert.False(
                seen.TryGetValue(art, out var owner),
                $"{role.Id} draws the same icon as {owner}");
            seen[art] = role.Id;
        }

        Assert.Equal(Catalog().Roles.Count, seen.Count);
    }

    private static Rect Ink(Geometry? first, Geometry? second)
    {
        var ink = Rect.Empty;
        if (first is not null)
        {
            ink.Union(first.Bounds);
        }

        if (second is not null)
        {
            ink.Union(second.Bounds);
        }

        return new Rect(
            ink.X + (ink.Width / 2),
            ink.Y + (ink.Height / 2),
            0,
            0);
    }

    private static Catalog Catalog() => BundledContracts.Catalog().Current;

    private static RoleDef Role(string id) =>
        Catalog().Roles.First(r => r.Id == id);
}
