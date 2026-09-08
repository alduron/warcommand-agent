using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Every key a user can press is either sayable or deliberately not, and the list of keys is read
/// from <see cref="BindingActions.All"/> rather than written out here.
/// </summary>
/// <remarks>
/// The reason this exists is a process failure, not a code one. Each voice gap this project has hit
/// was found by a person using the app, because every audit before this one was a hand-written list
/// of the keys already known to be broken and so could only confirm what was already known. UP and
/// DOWN were dead on every level and an audit of digits, select and back reported clean.
/// <para>
/// Enumerating the binding enum inverts that: a key added to the product with no voice decision
/// fails here, and the decision has to be written down rather than defaulted to silence.
/// </para>
/// </remarks>
public class EveryBindingHasAVoiceDecisionTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    /// <summary>
    /// The keys that are deliberately voice-free, and why. Anything not here must be sayable.
    /// </summary>
    private static readonly Dictionary<BindingAction, string> DeliberatelySilent = new()
    {
        [BindingAction.Ptt] =
            "A hold, and the one that opens the microphone. Nothing can be said before it, so a "
            + "spoken route to it could never be reached.",
        [BindingAction.Menu] =
            "The other hold. Both open the microphone and both arm the surface, so it is the same "
            + "key twice for two hands and neither can be spoken into existence.",
        [BindingAction.Panic] =
            "The kill switch. It suspends every hook, capture, draw and audio path, so a misheard "
            + "word must never be able to fire it. Binding rule 7 wants one deliberate press.",
        [BindingAction.NavCycle] =
            "It steps the range calculator, and the calculator's modes are ordinary lines on an "
            + "ordinary page. Voice names the one it wants; stepping is what a key does because a "
            + "key cannot name anything.",
        [BindingAction.Board] =
            "A global chord that cycles the overlay's own brightness, not a menu action. It has the "
            + "catalog verb 'board' at rest; inside a menu the drawn surface owns the words.",
    };

    /// <summary>Which key line answers a binding, for the ones voice does carry.</summary>
    private static readonly Dictionary<BindingAction, string> SpokenAs = new()
    {
        [BindingAction.NavUp] = MenuSpeech.Keys.Up,
        [BindingAction.NavDown] = MenuSpeech.Keys.Down,
        [BindingAction.NavBack] = MenuSpeech.Keys.Back,
        [BindingAction.NavTools] = MenuSpeech.Keys.Tools,
        [BindingAction.NavSelect] = MenuSpeech.Keys.Select,
    };

    [Fact]
    public void Every_binding_is_either_spoken_or_deliberately_silent()
    {
        var undecided = BindingActions.All
            .Where(a => !SpokenAs.ContainsKey(a) && !DeliberatelySilent.ContainsKey(a))
            .ToList();

        Assert.True(
            undecided.Count == 0,
            "these keys can be pressed and nobody has decided whether they can be said: "
            + string.Join(", ", undecided));
    }

    [Fact]
    public void A_binding_is_never_both_spoken_and_silent()
    {
        var both = SpokenAs.Keys.Intersect(DeliberatelySilent.Keys).ToList();

        Assert.True(both.Count == 0, "contradictory decisions for: " + string.Join(", ", both));
    }

    [Fact]
    public void Every_silence_carries_its_reason()
    {
        // A blank reason is how a gap gets filed as a decision.
        Assert.All(DeliberatelySilent.Values, why => Assert.True(why.Length > 40, why));
    }

    [Theory]
    [InlineData(BindingAction.NavUp)]
    [InlineData(BindingAction.NavDown)]
    [InlineData(BindingAction.NavBack)]
    [InlineData(BindingAction.NavSelect)]
    public void The_moving_keys_are_sayable_on_an_ordinary_list(BindingAction action)
    {
        var surface = MenuSpeech.SurfaceOf(Root());

        Assert.Contains(surface, line => line.Path == SpokenAs[action]);
    }

    [Fact]
    public void The_words_are_the_plain_ones_a_person_would_use()
    {
        // The bindings are named NavUp and NavSelect in code. Nobody says that, and a vocabulary
        // built from enum names would be unusable, so the spoken form is asserted here rather than
        // left to whatever the label happened to be.
        var surface = MenuSpeech.SurfaceOf(Root());

        Assert.Equal(MenuSpeech.Keys.Up, MenuSpeech.Match("up", surface)?.Path);
        Assert.Equal(MenuSpeech.Keys.Down, MenuSpeech.Match("down", surface)?.Path);
        Assert.Equal(MenuSpeech.Keys.Select, MenuSpeech.Match("select", surface)?.Path);
        Assert.Equal(MenuSpeech.Keys.Back, MenuSpeech.Match("back", surface)?.Path);
        Assert.Equal(MenuSpeech.Keys.Tools, MenuSpeech.Match("tools", surface)?.Path);

        // The vocabulary the recognizer is handed carries the plain words and nothing else, which
        // is what stops the enum name ever reaching a user. That a misheard 'navselect' still lands
        // on SELECT is the near-miss matcher doing its job, not a leak.
        var words = MenuSpeech.Vocabulary(surface);
        Assert.Contains("select", words, StringComparer.Ordinal);
        Assert.DoesNotContain("navselect", words, StringComparer.Ordinal);
        Assert.DoesNotContain("navup", words, StringComparer.Ordinal);
    }

    [Fact]
    public void Walking_to_a_line_and_taking_it_works_by_voice()
    {
        // UP and DOWN move a highlight and SELECT takes it. Without SELECT the two moving keys
        // led somewhere with no way to press it.
        var menu = Root();
        var surface = MenuSpeech.SurfaceOf(menu);

        Assert.Equal(MenuSpeech.Keys.Down, MenuSpeech.Match("down", surface)?.Path);
        menu.Scroll(1, T0);
        var landed = menu.Options[menu.Highlight];

        Assert.IsType<MenuNavigated>(menu.Select(T0));
        Assert.NotEqual(MenuLevel.Root, menu.Level);
        Assert.Equal(landed.TypeId, menu.SelectedTypeId ?? landed.TypeId);
    }

    [Fact]
    public void Tools_is_sayable_wherever_its_key_opens_it()
    {
        var menu = Root();
        Assert.True(menu.ZeroOpensTools);
        Assert.Equal(MenuSpeech.Keys.Tools, MenuSpeech.Match("tools", MenuSpeech.SurfaceOf(menu))?.Path);
    }

    [Fact]
    public void The_range_calculator_is_an_ordinary_page_with_ordinary_lines()
    {
        // It is a tool on the tools page, not a special case. Its modes are lines like any other,
        // so they are named and numbered and the speech layer knows nothing about ranges at all.
        var menu = Root();
        menu.OpenTools(T0, Context);
        menu.OpenPanel("range", T0, Context);

        var surface = MenuSpeech.SurfaceOf(menu);
        Assert.Equal("range.mode.raw", MenuSpeech.Match("sniping", surface)?.Path);
        Assert.Equal("range.mode.mortar", MenuSpeech.Match("mortar", surface)?.Path);

        // Saying one sets it outright. A key can only step; a line can be named.
        var mortar = menu.Options.Single(o => o.Path == "range.mode.mortar");
        menu.Digit(mortar.Digit, T0);
        Assert.Equal("mortar", menu.RangeMode.Id);
        Assert.True(menu.Options.Single(o => o.Path == "range.mode.mortar").IsChosen);
    }

    private static MenuContext Context => new()
    {
        OccupiedSlots = [1, 2],
        Slots = new Dictionary<int, SlotState>
        {
            [1] = new(RequestState.Open, false),
            [2] = new(RequestState.InProgress, true),
        },
        EnabledRoleIds = ["mortar", "medic"],
        RangeModes =
        [
            RangeMode.Raw,
            new("mortar", "MORTAR", 100m, 2000m),
        ],
    };

    private static MenuStateMachine Root()
    {
        var menu = new MenuStateMachine(MenuTree.Compile(ContractFixtures.Catalog), ContractFixtures.Catalog);
        menu.Open(T0, null, Context);
        return menu;
    }
}
