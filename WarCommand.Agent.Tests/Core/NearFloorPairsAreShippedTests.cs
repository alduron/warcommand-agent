using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The near-floor pairs reach the agent, so the ambiguous-alias menu can actually appear.
/// </summary>
/// <remarks>
/// scripts/contracts.ps1 generated this file on every run and copied it nowhere, so IntentParser
/// fell back to Empty: the recognizer picked one of two near-identical words and the speaker was
/// never asked which they meant.
/// </remarks>
public sealed class NearFloorPairsAreShippedTests
{
    [Fact]
    public void The_generated_pairs_are_embedded_in_the_assembly()
    {
        var raw = BundledContracts.TryRead(BundledContracts.NearFloorPairsResource);

        Assert.False(
            string.IsNullOrWhiteSpace(raw),
            "near-floor-pairs.json is not embedded: run scripts/contracts.ps1");
    }

    [Fact]
    public void They_parse_into_something_the_parser_can_use()
    {
        var pairs = BundledContracts.NearFloorPairs();

        // Empty is the silent failure mode: it parses, it is a valid object, and it turns the
        // whole disambiguation path off.
        Assert.NotSame(WarCommand.Agent.Core.Grammar.NearFloorPairs.Empty, pairs);
    }
}
