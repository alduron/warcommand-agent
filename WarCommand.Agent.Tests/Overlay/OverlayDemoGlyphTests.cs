using System.Linq;
using WarCommand.Agent.Dev;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The demo board is the only way anybody sees the overlay before Wardogs ships, per
/// Caveat_WarCommandOverlayDemoIsTheOnlyWayToSeeTheSurface. A demo row that never went through the
/// glyph resolver draws no role icon and no role hue, which is indistinguishable from the feature
/// not existing.
/// </summary>
public class OverlayDemoGlyphTests
{
    [Fact]
    public void Every_demo_row_carries_a_served_glyph()
    {
        foreach (var row in Rows())
        {
            Assert.False(string.IsNullOrEmpty(row.RoleId), row.TicketCode);
            Assert.NotNull(row.RoleGlyphFirst);
        }
    }

    [Fact]
    public void Every_demo_row_carries_its_roles_own_hue()
    {
        foreach (var row in Rows())
        {
            Assert.NotEqual("RoleCommand", row.RoleBrushKey);
        }

        // Two hues on the board at once, so the demo actually shows that the colour means something.
        Assert.True(Rows().Select(r => r.RoleBrushKey).Distinct(StringComparer.Ordinal).Count() > 1);
    }

    private static IReadOnlyList<BoardRowViewModel> Rows() =>
        [.. OverlayDemo.Rows, .. OverlayDemo.SecondaryStrip];
}
