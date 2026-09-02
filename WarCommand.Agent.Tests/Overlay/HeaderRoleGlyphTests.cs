using System.Linq;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The header's subscribed-role line is a surface, and it drew raw ids as one dim string for nine
/// reported rounds while every fix and every test aimed at the rows. These cover the header itself.
/// </summary>
/// <remarks>
/// Per Decision_WarCommandOverlayColorIsStateOnlyWebOwnsRoleHue the role owns the glyph and its hue
/// and nothing else, so these assert the glyph and the brush key, never the label's colour.
/// </remarks>
public class HeaderRoleGlyphTests
{
    private static RoleGlyphSource Catalog() =>
        new(BundledContracts.Catalog().Current.Role);

    [Fact]
    public void Every_header_role_carries_a_served_glyph()
    {
        var header = OverlayDemo.Header;

        Assert.NotEmpty(header.Roles);
        foreach (var role in header.Roles)
        {
            Assert.False(string.IsNullOrEmpty(role.RoleId));
            Assert.NotNull(role.RoleGlyphFirst);
        }
    }

    [Fact]
    public void Every_header_role_carries_its_own_hue()
    {
        var header = new BoardHeader
        {
            Title = "61ST / ALPHA",
            RoleIds = ["mortar", "logistics", "medic"],
        }.WithGlyph(Catalog());

        foreach (var role in header.Roles)
        {
            Assert.NotEqual("RoleCommand", role.RoleBrushKey);
        }

        Assert.True(
            header.Roles.Select(r => r.RoleBrushKey).Distinct(StringComparer.Ordinal).Count() > 1,
            "The header must show that the hue means something, not one colour for every role.");
    }

    [Fact]
    public void A_header_role_reads_its_display_name_not_the_raw_id()
    {
        var header = new BoardHeader
        {
            Title = "T",
            RoleIds = ["air_support"],
        }.WithGlyph(Catalog());

        var role = Assert.Single(header.Roles);
        Assert.Equal("air_support", role.RoleId);
        Assert.DoesNotContain('_', role.Display);
    }

    [Fact]
    public void A_header_that_skipped_the_resolver_renders_no_roles_at_all()
    {
        // The failure mode this replaces drew colourless ids, which reads as the feature missing
        // rather than as a bug. Nothing is the honest render.
        var header = new BoardHeader { Title = "T", RoleIds = ["mortar"] };

        Assert.Empty(header.Roles);
    }

    [Fact]
    public void An_unknown_role_still_renders_rather_than_throwing()
    {
        var header = new BoardHeader
        {
            Title = "T",
            RoleIds = ["not_a_role"],
        }.WithGlyph(Catalog());

        var role = Assert.Single(header.Roles);
        Assert.Equal("not_a_role", role.Display);
        Assert.Null(role.RoleGlyphFirst);
        Assert.Equal("RoleCommand", role.RoleBrushKey);
    }
}
