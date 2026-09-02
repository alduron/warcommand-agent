using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The glyph is served catalog data, so it is untrusted string input on the UI thread.
/// </summary>
public class RoleGlyphTests
{
    private static RoleDef Role(string? d1, string d2 = "", string? group = "fire") => new()
    {
        Id = "mortar",
        Display = "Mortar",
        TicketPrefix = "MTR",
        ColorGroup = group,
        Icon = d1 is null ? null : new RoleIconDef { D1 = d1, D2 = d2 },
    };

    [Fact]
    public void A_valid_path_becomes_frozen_geometry()
    {
        var geometry = RoleGlyph.Parse("M3 20C7 6 17 6 21 20");

        Assert.NotNull(geometry);
        Assert.True(geometry!.IsFrozen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_to_draw_is_null(string? d)
    {
        Assert.Null(RoleGlyph.Parse(d));
    }

    [Fact]
    public void An_unparseable_path_draws_nothing_rather_than_throwing()
    {
        Assert.Null(RoleGlyph.Parse("not a path"));
    }

    [Fact]
    public void Every_bundled_role_parses()
    {
        var catalog = BundledContracts.Catalog().Current;

        foreach (var role in catalog.Roles)
        {
            var (first, second) = RoleGlyph.Of(role);
            Assert.True(first is not null, role.Id);
            Assert.True(second is null || second.IsFrozen, role.Id);
        }
    }

    [Theory]
    [InlineData("fire", "RoleFire")]
    [InlineData("recon", "RoleRecon")]
    [InlineData("move", "RoleMove")]
    [InlineData("build", "RoleBuild")]
    [InlineData("medic", "RoleMedic")]
    [InlineData("command", "RoleCommand")]
    public void A_group_picks_its_brush(string group, string key)
    {
        Assert.Equal(key, RoleGlyph.BrushKey(group));
    }

    [Fact]
    public void A_group_the_overlay_does_not_know_falls_back_rather_than_crashing()
    {
        Assert.Equal("RoleCommand", RoleGlyph.BrushKey("something_new"));
        Assert.Equal("RoleCommand", RoleGlyph.BrushKey(null));
    }

    [Fact]
    public void A_role_with_no_icon_draws_nothing()
    {
        var (first, second) = RoleGlyph.Of(Role(null));

        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void The_source_parses_each_role_once()
    {
        var calls = 0;
        var source = new RoleGlyphSource(_ =>
        {
            calls++;
            return Role("M0 0h24");
        });

        source.Geometry("mortar");
        source.Geometry("mortar");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invalidate_makes_the_next_frame_see_a_new_catalog()
    {
        var calls = 0;
        var source = new RoleGlyphSource(_ =>
        {
            calls++;
            return Role("M0 0h24");
        });

        source.Geometry("mortar");
        source.Invalidate();
        source.Geometry("mortar");

        Assert.Equal(2, calls);
    }
}
