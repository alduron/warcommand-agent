using System.Windows.Threading;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Input;

namespace WarCommand.Agent.Composition;

/// <summary>
/// Carries menu keys from the input bridge to the <see cref="MenuStateMachine"/>, and every
/// outcome back out to whoever can act on it.
/// </summary>
/// <remarks>
/// The state machine, its tree and its whole outcome vocabulary were written and tested long
/// before anything constructed them: <c>InputComposition</c> passed <c>menu: null</c>, so no digit
/// reached anything and there was no way to make a request, take one, or close one from the
/// keyboard at all. This is the missing half.
/// <para>
/// Keys arrive on the hook thread and every outcome is marshalled onto the dispatcher, because
/// each one ends in a render, an HTTP call, or both.
/// </para>
/// </remarks>
public sealed class MenuDriver : IMenuKeySink, IMenuGate
{
    private readonly Dispatcher _dispatcher;
    private readonly MenuStateMachine _menu;
    private readonly Action<MenuOutcome> _onOutcome;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Builds the driver over a compiled menu.</summary>
    /// <param name="dispatcher">The UI dispatcher. Every outcome hops onto it.</param>
    /// <param name="menu">The machine. One per agent, rebuilt when the catalog changes.</param>
    /// <param name="onOutcome">Where a navigation, a request or a board verb goes.</param>
    /// <param name="clock">Injected so the idle timeout is testable.</param>
    public MenuDriver(
        Dispatcher dispatcher,
        MenuStateMachine menu,
        Action<MenuOutcome> onOutcome,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(onOutcome);

        _dispatcher = dispatcher;
        _menu = menu;
        _onOutcome = onOutcome;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The machine, for a renderer that draws what it holds.</summary>
    public MenuStateMachine Menu => _menu;

    /// <summary>
    /// Rebuilds the bridge's armed-key table. Set by the composition root once the bridge exists.
    /// </summary>
    /// <remarks>
    /// Bare digits are hooked only while the menu is open, so the table has to be rebuilt the
    /// instant the menu opens or closes, synchronously, before the next key arrives. Rendering can
    /// wait for the dispatcher; arming cannot.
    /// </remarks>
    public Action? Rearm { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Read by <c>ArmedKeys</c> to decide whether bare digits are hooked at all, so this is what
    /// keeps the digit row inert everywhere else on the machine while the menu is closed.
    /// </remarks>
    public bool MenuIsOpen => _menu.IsOpen;

    /// <summary>Push-to-talk went down: open at the root, with the coordinate snapshot if there is one.</summary>
    public void Open(MapPoint? snapshot, MenuContext context) =>
        Raise(_menu.Open(_clock(), snapshot, context));

    /// <summary>The game window went away. The menu goes with it.</summary>
    public void FocusLost() => Raise(_menu.FocusLost(_clock()));

    /// <summary>Push-to-talk came up. A tap latches; a hold commits or discards.</summary>
    public void KeyUp() => Raise(_menu.KeyUp(_clock()));

    /// <inheritdoc />
    public void Digit(int digit) => Raise(_menu.Digit(digit, _clock()));

    /// <inheritdoc />
    public void Escape() => Raise(_menu.Escape(_clock()));

    /// <inheritdoc />
    public void Backspace() => Raise(_menu.Backspace(_clock()));

    /// <summary>The idle timeout, driven by the same tick that redraws the board.</summary>
    public void Tick() => Raise(_menu.Tick(_clock()));

    private void Raise(MenuOutcome outcome)
    {
        Rearm?.Invoke();

        if (outcome is MenuNothing)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() => _onOutcome(outcome));
    }
}
