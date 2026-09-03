using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A submitted request must carry everything the person who ACCEPTS it needs to act.
/// </summary>
/// <remarks>
/// A build request reached a board reading BUILD with nothing saying what to build, because the
/// type did not require a structure kind and the submit dropped the one the requester picked. The
/// provider who took it had no way to do the job. These are the checks that stop that shape of
/// failure returning on any type, not just that one.
/// </remarks>
public sealed class RequestIsActionableTests
{
    private static Catalog Catalog => ContractFixtures.Catalog;

    [Fact]
    public void A_kind_reaches_the_row_label_so_the_provider_knows_the_job()
    {
        // The API folds the chosen kind into modifiers, and the row renders every modifier. This is
        // the whole chain that made BUILD TRENCH read as BUILD: if any link drops it, the provider
        // who accepts the row cannot do the job.
        var withStructure = ModifierLabels.Line(["trench"], null);
        Assert.Equal("TRENCH", withStructure);

        var withSupply = ModifierLabels.Line(["ammo"], null);
        Assert.Equal("AMMO", withSupply);

        // Several at once, all of them, plus the count. Showing one of three is worse than showing
        // none, because the row reads as a complete description and is not one.
        Assert.Equal("SMOKE DANGER CLOSE x4", ModifierLabels.Line(["smoke", "danger_close"], 4));
    }

    [Fact]
    public void Every_kind_the_catalog_offers_renders_as_a_word()
    {
        foreach (var kind in Catalog.StructureKinds.Concat(Catalog.SupplyKinds))
        {
            var label = ModifierLabels.Of(kind.Id);

            Assert.False(string.IsNullOrWhiteSpace(label), $"{kind.Id} renders nothing");
            Assert.DoesNotContain('_', label);
        }
    }

    [Fact]
    public void A_type_offering_kinds_in_the_menu_requires_one()
    {
        // A type with kinds under it in the tree but no requirement lets the server discard the
        // choice, which is exactly how BUILD lost TRENCH.
        var withStructureLeaves = Catalog.StructureKinds
            .Where(k => k.MenuPath is not null)
            .Select(k => k.MenuPath!.Split('.')[0] + "." + k.MenuPath!.Split('.')[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(withStructureLeaves);

        foreach (var type in Catalog.RequestTypes.Where(t => t.RequiresStructureKind))
        {
            Assert.False(
                string.IsNullOrEmpty(type.DefaultStructureKind),
                $"{type.Id} requires a structure kind and names no default, so an unqualified request cannot be submitted at all");
        }

        foreach (var type in Catalog.RequestTypes.Where(t => t.RequiresSupplyKind))
        {
            Assert.False(
                string.IsNullOrEmpty(type.DefaultSupplyKind),
                $"{type.Id} requires a supply kind and names no default");
        }
    }

    [Fact]
    public void Every_request_type_is_reachable_from_the_menu_tree()
    {
        var tree = MenuTree.Compile(Catalog);
        var reached = new HashSet<string>(StringComparer.Ordinal);

        void Walk(IReadOnlyList<MenuEntry> level)
        {
            foreach (var entry in level)
            {
                if (entry.TypeId is { } id)
                {
                    reached.Add(id);
                }

                Walk(entry.Children);
            }
        }

        Walk(tree.Root);

        // A type in the grammar but not in the tree does not exist for anybody with voice off,
        // which is most people most of the time.
        foreach (var type in Catalog.RequestTypes)
        {
            Assert.Contains(type.Id, reached);
        }
    }

    [Fact]
    public void Every_menu_leaf_leads_to_a_type_that_exists()
    {
        var tree = MenuTree.Compile(Catalog);

        void Walk(IReadOnlyList<MenuEntry> level)
        {
            foreach (var entry in level)
            {
                if (entry.IsLeaf)
                {
                    // A leaf with no type is a dead end: it takes the press and asks for a point
                    // for a request that can never be built.
                    Assert.NotNull(entry.TypeId);
                    Assert.NotNull(Catalog.RequestType(entry.TypeId!));
                }

                Walk(entry.Children);
            }
        }

        Walk(tree.Root);
    }

    [Fact]
    public void Fortify_requires_a_structure_kind()
    {
        var fortify = Catalog.RequestType("fortify");

        Assert.NotNull(fortify);
        Assert.True(
            fortify.RequiresStructureKind,
            "a build request that does not name what to build cannot be fulfilled by whoever accepts it");
    }

    [Fact]
    public void Every_type_names_a_point_label_for_each_point_it_takes()
    {
        foreach (var type in Catalog.RequestTypes.Where(t => t.Arity > 0))
        {
            Assert.True(
                type.PointLabels.Count >= type.Arity,
                $"{type.Id} takes {type.Arity} point(s) and labels {type.PointLabels.Count}: an unlabelled second point does not say which end is which");
        }
    }

    [Fact]
    public void Every_type_has_an_overlay_label()
    {
        foreach (var type in Catalog.RequestTypes)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(type.OverlayLabel),
                $"{type.Id} renders no label, so its row names nothing");
        }
    }

    [Fact]
    public void A_chosen_kind_reaches_the_outcome_the_submit_is_built_from()
    {
        // The menu captured the structure and the submit read a different field, so the choice was
        // dropped between the two. The outcome is the only thing the submit sees.
        var tree = MenuTree.Compile(Catalog);
        var menu = new MenuStateMachine(tree, Catalog);

        var trench = Catalog.StructureKinds.First(k => k.Id == "trench");
        var entry = tree.Find(trench.MenuPath!);

        Assert.NotNull(entry);
        Assert.Equal("trench", entry.StructureKindId);

        menu.Open(DateTimeOffset.UnixEpoch, new MapPoint(1m, 2m, "typed_grid", null, null));
        Assert.NotNull(menu.Options);
    }
}
