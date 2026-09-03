using System.Globalization;

namespace WarCommand.Agent.Core.Input;

/// <summary>
/// Everything the header's hint cell is allowed to look at. One flat record so the resolver stays
/// pure and the whole hint table is one test.
/// </summary>
public sealed record HintState
{
    /// <summary>The key held to work the overlay, which is the Menu key, not push-to-talk.</summary>
    public string? PttLabel { get; init; }

    /// <summary>Panic is engaged. Nothing draws in game, but the window still renders.</summary>
    public bool Suspended { get; init; }

    /// <summary>Where the menu is. <see cref="MenuLevel.Closed"/> when it is not up.</summary>
    public MenuLevel MenuLevel { get; init; } = MenuLevel.Closed;

    /// <summary>A panel owns the digits: roles, help, match, people, restart.</summary>
    public bool PanelOpen { get; init; }

    /// <summary>A two-point draft is waiting for its second point.</summary>
    public bool AwaitingSecondPoint { get; init; }

    /// <summary>The one-time link prompt is showing.</summary>
    public bool LinkPromptPending { get; init; }

    /// <summary>Same deployment across a game-session boundary, and nothing has been touched since.</summary>
    public bool SameMatchDoubt { get; init; }

    /// <summary>In a group, on no board. An ordinary state, and one with an answer.</summary>
    public bool OnNoDeployment { get; init; }
}

/// <summary>
/// The one line of key help on the overlay, resolved from state rather than fixed. Four bindings
/// is few enough to learn; the routes through the menu are not, so the header names the one route
/// that is worth knowing right now and the menu draws its own digits once it is open.
/// </summary>
public static class OverlayHint
{
    /// <summary>Drawn while no key has been chosen. The product does nothing until one is.</summary>
    public const string NoPttKey = "NO MENU KEY  TRAY > SETTINGS";

    /// <summary>The steady state, and the only route anybody has to learn.</summary>
    public const string HelpMarker = "?";

    /// <summary>The header's right-hand hint cell, or null when there is nothing worth saying.</summary>
    public static string Resolve(HintState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Suspended)
        {
            return "PANIC  SAME KEY RESUMES";
        }

        if (string.IsNullOrEmpty(state.PttLabel))
        {
            return NoPttKey;
        }

        var ptt = state.PttLabel;

        // An open menu or panel draws its own entries, so the hint stops naming routes and names
        // the three keys that are not on screen: the ones that move you back out.
        if (state.MenuLevel != MenuLevel.Closed)
        {
            return state.MenuLevel switch
            {
                MenuLevel.Coordinate or MenuLevel.Join => "BACKSPACE FIXES  ESC CLOSES",
                MenuLevel.Confirm => "RELEASE SENDS  ESC DISCARDS",
                _ => "BACKSPACE UP  ESC CLOSES",
            };
        }

        if (state.PanelOpen)
        {
            return "0 PAGES  ESC CLOSES";
        }

        if (state.AwaitingSecondPoint)
        {
            return $"TAP {ptt} FOR POINT 2";
        }

        if (state.LinkPromptPending)
        {
            return $"LINK ACCOUNT  {Route(ptt, "link")}";
        }

        if (state.OnNoDeployment)
        {
            return $"NO MATCH  {Route(ptt, "match")}";
        }

        if (state.SameMatchDoubt)
        {
            return $"SAME MATCH?  {Route(ptt, "match")}";
        }

        return $"HOLD {ptt}  {HelpMarker}";
    }

    /// <summary>
    /// 'Mouse5 0 0 3'. The keys in the order they are pressed, which is the only notation that
    /// survives being read at a glance by somebody who has never opened the menu.
    /// </summary>
    public static string Route(string pttLabel, string moreEntryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pttLabel);
        ArgumentException.ThrowIfNullOrEmpty(moreEntryId);

        var digit = MenuStateMachine.MoreDigits.TryGetValue(moreEntryId, out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(moreEntryId), "not an entry on the MORE page");

        return $"{pttLabel} 0 0 {digit}";
    }
}
