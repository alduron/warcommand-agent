using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Tags are how the rebuilt catalog carries a game with hundreds of distinct things in it, so the
/// two ways a tag can be lost are the two tests here: an unreadable name, and a digit that cannot
/// reach it.
/// </summary>
public class ModifierTagTests
{
    private static Catalog Catalog => ContractFixtures.Catalog;

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MapPoint Point => new(85.53m, 69.42m, "map_readout", "x85.53 y69.42", 0.94m);

    /// <summary>Walks the digits of a catalog menu path until the draft is on the tag level.</summary>
    private static MenuStateMachine Confirming(string menuPath)
    {
        var menu = new MenuStateMachine(MenuTree.Compile(Catalog), Catalog);
        menu.Open(T0, Point);

        var parts = menuPath.Split('.');
        menu.Digit(Catalog.MenuCategories[parts[0]], T0);
        foreach (var slot in parts.Skip(1))
        {
            menu.Digit(int.Parse(slot, System.Globalization.CultureInfo.InvariantCulture), T0);
        }

        // A two-point type stops for its second point. The tags come after it.
        while (menu.Level == MenuLevel.Coordinate)
        {
            menu.AcceptReadCoordinate(new MapPoint(12.5m, 34.5m, "map_readout", null, null), T0);
        }

        Assert.Equal(MenuLevel.Confirm, menu.Level);
        return menu;
    }

    [Theory]
    [InlineData("t21", "T-21")]
    [InlineData("box_mags", "Box+Mags")]
    [InlineData("irangefinder", "iRange")]
    [InlineData("m17s", "17s")]
    [InlineData("amr50", "AMR 50")]
    [InlineData("danger_close", "Danger close")]
    public void A_tag_draws_the_word_the_game_uses(string id, string expected)
    {
        Assert.Equal(expected, ModifierLabels.Of(id, Catalog));
    }

    /// <summary>A tag the catalog has not named still renders rather than throwing.</summary>
    [Fact]
    public void An_unnamed_tag_falls_back_to_the_derived_word()
    {
        Assert.Equal("NOT A REAL TAG", ModifierLabels.Of("not_a_real_tag", Catalog));
    }

    [Fact]
    public void A_weapon_carries_more_tags_than_one_page_of_digits()
    {
        var rifle = Catalog.RequestType("weapon_rifle")!;

        Assert.True(
            rifle.Modifiers.Count > 9,
            "if a rifle ever fits one page this test is the wrong shape, not the paging");
    }

    /// <summary>
    /// Every tag on every type is reachable by pressing a digit, walking pages with 0.
    /// </summary>
    /// <remarks>
    /// The guarantee the menu exists for. Nine digits and seventeen tags means the attachments on
    /// every weapon in the game were unreachable, and a request for a rifle could never say it
    /// needed magazines with it.
    /// </remarks>
    [Fact]
    public void Every_tag_of_every_type_is_reachable_by_a_digit()
    {
        foreach (var type in Catalog.RequestTypes)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var menu = Confirming(type.MenuPaths[0]);

            for (var page = 0; page < menu.ModifierPageCount; page++)
            {
                foreach (var entry in menu.Options)
                {
                    reached.Add(entry.Path[(entry.Path.LastIndexOf('.') + 1)..]);
                }

                menu.Digit(0, T0);
            }

            Assert.Equal(type.Modifiers.OrderBy(m => m, StringComparer.Ordinal), reached.OrderBy(m => m, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void A_tag_chosen_on_another_page_stays_chosen_and_is_counted()
    {
        var menu = Confirming("weapons.1");

        Assert.Equal(2, menu.ModifierPageCount);

        menu.Digit(1, T0);
        Assert.Equal(0, menu.ChosenOffPage);

        menu.Digit(0, T0);
        Assert.Equal(1, menu.ModifierPage);
        Assert.Equal(1, menu.ChosenOffPage);

        // Back to the first page and the choice is still there, marked.
        menu.Digit(0, T0);
        Assert.Equal(0, menu.ModifierPage);
        Assert.Contains(menu.Options, o => o.IsChosen);
    }
}
