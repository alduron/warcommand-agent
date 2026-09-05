using System.Linq;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Input;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Everything the keyboard reaches, voice reaches too.
/// </summary>
/// <remarks>
/// The rule used to run one way only: every voice feature had to be reachable by key. The reverse
/// was never stated and never held, so the panels, the range tool and the board display had no
/// spoken route at all and voice read as broken to anybody who tried to drive the overlay with it.
/// This is the other direction, and it is mechanical: both lists are read from the tables the menu
/// actually dispatches on, so a new panel or a new row verb fails here until it has a way to be
/// said.
/// </remarks>
public class VoiceKeyboardParityTests
{
    /// <summary>
    /// The MORE entry id each panel verb opens, where the two names differ. Nothing else may be
    /// mapped by hand: a panel whose id is not a verb id and is not listed here is a gap.
    /// </summary>
    private static readonly Dictionary<string, string> VerbForPanel = new(StringComparer.Ordinal)
    {
        ["roles"] = "role",
    };

    private static IReadOnlyList<string> VerbIds =>
        [.. ContractFixtures.Catalog.CommandVerbs.Select(v => v.Id)];

    [Fact]
    public void EveryMorePanelCanBeSpoken()
    {
        var missing = MenuStateMachine.MoreDigits.Keys
            .Where(panel => !VerbIds.Contains(VerbForPanel.GetValueOrDefault(panel, panel)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"MORE panels with no spoken route: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryRowVerbCanBeSpoken()
    {
        // The menu dispatches on these labels; the catalog carries the ids. Compare on the id, so
        // a renamed label does not silently pass.
        var labels = MenuStateMachine.BoardVerbList
            .Select(label => label.Replace(' ', '_').ToLowerInvariant())
            .ToList();

        var missing = labels.Where(id => !VerbIds.Contains(id)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Row verbs with no spoken route: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The board display cycle is the one control with no menu entry at all, so nothing else in
    /// this file would catch it going missing.
    /// </summary>
    [Fact]
    public void TheBoardDisplayCycleCanBeSpoken() => Assert.Contains("board", VerbIds);

    /// <summary>
    /// Panic is the deliberate exception and must stay one. A spoken kill switch cannot be trusted
    /// to fire, and the one control that has to work under stress is a key.
    /// </summary>
    [Fact]
    public void PanicIsNeverSpoken()
    {
        var everything = Grammar.Compile(ContractFixtures.Catalog, GrammarContext.Everything);
        var spoken = PositionClasses.All
            .SelectMany(everything.PhrasesFor)
            .ToList();

        Assert.DoesNotContain("panic", spoken);
    }
}
