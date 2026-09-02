using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Media;
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

    /// <summary>
    /// The tokens are merged into BoardView, not into App.xaml, so an application-only lookup found
    /// nothing and every role on the board drew the neutral grey. It reads as a board with no role
    /// colour rather than as a missing resource, which is why nobody caught it by looking.
    /// </summary>
    [Fact]
    public void The_brush_converter_resolves_the_role_hues_with_no_application_resources()
    {
        OnStaThread(() =>
        {
            var converter = new RoleBrushConverter();

            var fire = Brush(converter, "RoleFire");
            var medic = Brush(converter, "RoleMedic");
            var move = Brush(converter, "RoleMove");

            Assert.Equal(Color.FromRgb(0xE5, 0x6A, 0x5C), fire.Color);
            Assert.Equal(Color.FromRgb(0x58, 0xB1, 0x5A), medic.Color);
            Assert.Equal(Color.FromRgb(0x5A, 0x9F, 0xE6), move.Color);
        });
    }

    /// <summary>Every group the catalog ships resolves, and no two of them collide.</summary>
    [Fact]
    public void Every_shipped_colour_group_draws_a_hue_of_its_own()
    {
        OnStaThread(() =>
        {
            var converter = new RoleBrushConverter();
            var catalog = BundledContracts.Catalog().Current;

            var byGroup = catalog.Roles
                .Select(r => r.ColorGroup)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(g => g ?? "null", g => Brush(converter, RoleGlyph.BrushKey(g)).Color);

            Assert.DoesNotContain("fire", byGroup.Keys.Where(k => byGroup[k] == Fallback));
            Assert.Equal(byGroup.Count, byGroup.Values.Distinct().Count());
        });
    }

    [Fact]
    public void An_unknown_key_falls_back_to_the_neutral_hue_rather_than_throwing()
    {
        OnStaThread(() =>
        {
            Assert.Equal(Fallback, Brush(new RoleBrushConverter(), "NotAKey").Color);
        });
    }

    private static readonly Color Fallback = Color.FromRgb(0xB4, 0xB6, 0xB8);

    private static SolidColorBrush Brush(RoleBrushConverter converter, string key) =>
        Assert.IsAssignableFrom<SolidColorBrush>(
            converter.Convert(key, typeof(System.Windows.Media.Brush), null, CultureInfo.InvariantCulture));

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (failure is not null)
        {
            throw new InvalidOperationException("The STA body threw.", failure);
        }
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
