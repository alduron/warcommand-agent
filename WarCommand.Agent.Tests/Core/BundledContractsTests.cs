using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The fallback 10-agent-spec.md requires: a bundled copy ships with the agent, first run with no
/// network works, and a served document that fails validation falls back to something known good.
/// Rules 2 to 4 of the served-profile contract rest on rule 1 being real.
/// </summary>
public class BundledContractsTests
{
    public static TheoryData<string> ResourceNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in BundledContracts.ResourceNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void Every_served_contract_ships_inside_the_assembly(string resourceName)
    {
        Assert.False(string.IsNullOrWhiteSpace(BundledContracts.Read(resourceName)));
    }

    [Fact]
    public void With_no_served_document_the_bundle_loads_and_validates()
    {
        var catalog = BundledContracts.Catalog();
        var profile = BundledContracts.GameProfile();
        var ballistics = BundledContracts.Ballistics();

        Assert.True(catalog.IsBundledFallback);
        Assert.True(profile.IsBundledFallback);
        Assert.True(ballistics.IsBundledFallback);
        Assert.Null(profile.ETag);
        Assert.NotEmpty(catalog.Current.RequestTypes);
        Assert.NotEmpty(ballistics.Current.Weapons);
        Assert.NotNull(profile.Current.MapReadout);
    }

    [Fact]
    public void An_invalid_served_profile_is_refused_and_the_bundle_stays_in_force()
    {
        // Its own store, never the process-wide one: adopting into that would leak into
        // every other test in the run.
        var store = BundledContracts.Load<GameProfile>(BundledContracts.GameProfileResource);
        var before = store.Current;

        var adoption = store.TryAdopt("""{"version": -1}""", "\"deadbeef\"");

        Assert.False(adoption.Adopted);
        Assert.NotEmpty(adoption.Errors);
        Assert.Same(before, store.Current);
        Assert.True(store.IsBundledFallback);
        Assert.Null(store.ETag);
    }

    [Fact]
    public void An_invalid_served_profile_never_unwinds_a_good_served_one()
    {
        var store = BundledContracts.Load<GameProfile>(BundledContracts.GameProfileResource);
        Assert.True(store.TryAdopt(BundledContracts.Read(BundledContracts.GameProfileResource), "\"v1\"").Adopted);
        var served = store.Current;

        var adoption = store.TryAdopt("{ not json", "\"v2\"");

        Assert.False(adoption.Adopted);
        Assert.Same(served, store.Current);
        Assert.Equal("\"v1\"", store.ETag);
        Assert.False(store.IsBundledFallback);

        store.RevertToBundled();
        Assert.True(store.IsBundledFallback);
        Assert.Null(store.ETag);
    }

    [Theory]
    [InlineData(BundledContracts.RequestTypesResource, "request-types.json")]
    [InlineData(BundledContracts.GameProfileResource, "game-profile.json")]
    [InlineData(BundledContracts.BallisticsResource, "ballistics.json")]
    public void The_bundle_matches_the_umbrella_source(string resourceName, string fileName)
    {
        var umbrella = ContractFixtures.UmbrellaContract(fileName);
        if (umbrella is null)
        {
            // A standalone clone has no umbrella to compare against. scripts/contracts.ps1 -Check is
            // what fails a stale bundle; this only catches it earlier when the umbrella is there.
            return;
        }

        Assert.Equal(
            umbrella.ReplaceLineEndings("\n"),
            BundledContracts.Read(resourceName).ReplaceLineEndings("\n"));
    }
}
