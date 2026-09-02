using WarCommand.Agent.Core.Input;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The header's one line of key help. Four bindings are learnable; the menu routes are not, so the
/// hint has to name the route that matters right now and nothing else.
/// </summary>
public class OverlayHintTests
{
    [Fact]
    public void The_steady_state_names_the_users_own_key_and_the_help_marker()
    {
        Assert.Equal("HOLD Mouse5  ?", OverlayHint.Resolve(new HintState { PttLabel = "Mouse5" }));
    }

    [Fact]
    public void No_chosen_key_outranks_everything_except_panic()
    {
        Assert.Equal(OverlayHint.NoPttKey, OverlayHint.Resolve(new HintState { OnNoDeployment = true }));

        Assert.Equal(
            "PANIC  SAME KEY RESUMES",
            OverlayHint.Resolve(new HintState { Suspended = true }));
    }

    [Theory]
    [InlineData(MenuLevel.Root, "BACKSPACE UP  ESC CLOSES")]
    [InlineData(MenuLevel.More, "BACKSPACE UP  ESC CLOSES")]
    [InlineData(MenuLevel.Coordinate, "BACKSPACE FIXES  ESC CLOSES")]
    [InlineData(MenuLevel.Join, "BACKSPACE FIXES  ESC CLOSES")]
    [InlineData(MenuLevel.Confirm, "RELEASE SENDS  ESC DISCARDS")]
    public void An_open_menu_draws_its_own_digits_so_the_hint_names_the_way_out(MenuLevel level, string expected)
    {
        Assert.Equal(expected, OverlayHint.Resolve(new HintState { PttLabel = "Mouse5", MenuLevel = level }));
    }

    [Fact]
    public void A_state_with_an_answer_names_the_keys_that_reach_it()
    {
        Assert.Equal(
            "NO MATCH  Mouse5 0 0 3",
            OverlayHint.Resolve(new HintState { PttLabel = "Mouse5", OnNoDeployment = true }));

        Assert.Equal(
            "SAME MATCH?  Mouse5 0 0 3",
            OverlayHint.Resolve(new HintState { PttLabel = "Mouse5", SameMatchDoubt = true }));

        Assert.Equal(
            "LINK ACCOUNT  Mouse5 0 0 8",
            OverlayHint.Resolve(new HintState { PttLabel = "Mouse5", LinkPromptPending = true }));

        Assert.Equal(
            "TAP Mouse5 FOR POINT 2",
            OverlayHint.Resolve(new HintState { PttLabel = "Mouse5", AwaitingSecondPoint = true }));
    }

    [Fact]
    public void A_route_is_read_off_the_menu_rather_than_written_out_twice()
    {
        Assert.Equal(2, MenuStateMachine.MoreDigits["roles"]);
        Assert.Equal("Mouse5 0 0 2", OverlayHint.Route("Mouse5", "roles"));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayHint.Route("Mouse5", "opacity"));
    }
}
