using System.Globalization;
using WarCommand.Agent.Core.Input;

namespace WarCommand.Agent.Overlay;

/// <summary>One offered digit and what it does.</summary>
public sealed record MenuOptionViewModel(string Digit, string Label);

/// <summary>
/// The menu as the surface draws it: a title, the digits on offer, and the line being typed.
/// </summary>
/// <remarks>
/// A projection, never the machine itself. The overlay renders what it is handed and holds no menu
/// state of its own, so the only thing that can decide what a digit means is the state machine.
/// </remarks>
public sealed record MenuViewModel
{
    /// <summary>Nothing is open. The board draws normally.</summary>
    public static MenuViewModel Closed { get; } = new() { IsOpen = false, Title = string.Empty };

    public bool IsOpen { get; init; }

    /// <summary>Where you are: REQUEST, BOARD, the selected type, the slot being acted on.</summary>
    public required string Title { get; init; }

    /// <summary>The digits on offer, in order. Empty on a level that takes typed digits instead.</summary>
    public IReadOnlyList<MenuOptionViewModel> Options { get; init; } = [];

    /// <summary>The coordinate or invite code being typed, already spaced for reading.</summary>
    public string? Typed { get; init; }

    /// <summary>Builds the projection from the machine.</summary>
    public static MenuViewModel From(MenuStateMachine menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (!menu.IsOpen)
        {
            return Closed;
        }

        return new MenuViewModel
        {
            IsOpen = true,
            Title = TitleFor(menu),
            Options =
            [
                .. menu.Options.Select(o => new MenuOptionViewModel(
                    o.Digit.ToString(CultureInfo.InvariantCulture),
                    o.Label)),
            ],
            Typed = menu.Digits.Count == 0
                ? null
                : string.Concat(menu.Digits.Select(d => d.ToString(CultureInfo.InvariantCulture))),
        };
    }

    private static string TitleFor(MenuStateMachine menu) => menu.Level switch
    {
        MenuLevel.Root => "REQUEST",
        MenuLevel.Branch => menu.Selection?.Label.ToUpperInvariant() ?? "REQUEST",
        MenuLevel.Coordinate => "COORDINATE",
        MenuLevel.Confirm => menu.SelectedTypeId is { } type ? type.ToUpperInvariant() : "CONFIRM",
        MenuLevel.Board => "BOARD  PICK A SLOT",
        MenuLevel.BoardAction => "SLOT  PICK A VERB",
        MenuLevel.More => "MORE",
        MenuLevel.Join => "JOIN CODE",
        _ => string.Empty,
    };
}
