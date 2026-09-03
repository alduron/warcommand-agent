using System.Diagnostics.CodeAnalysis;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Input;

/// <summary>
/// Push-to-talk edges. The coordinate is snapshotted by the consumer on
/// <see cref="PttDown"/> and never on <see cref="PttUp"/>: people move the mouse while talking.
/// Tap versus hold is the pure machine's decision, not this assembly's.
/// </summary>
/// <remarks>
/// Called on the hook thread. Every method must return immediately. Windows silently removes a
/// low-level hook whose callback exceeds its timeout.
/// </remarks>
public interface IPttSink
{
    /// <summary>The PTT key went down.</summary>
    void PttDown(DateTimeOffset at);

    /// <summary>The PTT key came up.</summary>
    void PttUp(DateTimeOffset at);
}

/// <summary>
/// The four key classes a panel or menu consumes. Delivered only while the consumer reports a menu
/// open; every other key passes through to the game untouched.
/// </summary>
/// <remarks>Called on the hook thread. Must return immediately.</remarks>
public interface IMenuKeySink
{
    /// <summary>A digit 0-9.</summary>
    void Digit(int digit);

    /// <summary>Escape.</summary>
    void Escape();

    /// <summary>Backspace.</summary>
    void Backspace();
}

/// <summary>
/// Navigating an open menu while a hold key is down. Bound to WASD by default: up, down, select
/// and back, all rebindable.
/// </summary>
/// <remarks>
/// Called on the hook thread. Must return immediately. Every event delivered here is also
/// SWALLOWED, and it has to be: these keys are the movement keys, so a nav key that reached the
/// game would walk the player while they read the board.
/// <para>
/// This is keyboard only. The mouse cannot do this job: Wardogs reads Raw Input, so a low-level
/// mouse hook's swallow is ignored and a left click bound here would fire the weapon.
/// </para>
/// </remarks>
public interface IMenuNavSink
{
    /// <summary>Wheel notches. Negative is up the list, positive is down.</summary>
    void Scroll(int notches);

    /// <summary>Left click. Commits the highlighted option.</summary>
    void Commit();

    /// <summary>Right click. Up one level.</summary>
    void Back();
}

/// <summary>A chord fired. Everything that is not PTT and not a menu key arrives here.</summary>
/// <remarks>Called on the hook thread. Must return immediately.</remarks>
public interface IChordSink
{
    /// <summary>The action the pressed chord is bound to. Never <see cref="BindingAction.None"/>.</summary>
    void Invoke(BindingAction action);
}

/// <summary>
/// Whether the foreground window belongs to the Wardogs process, and whether that process is running
/// at all. Implemented by <see cref="GameWindowWatcher"/>; faked in tests.
/// </summary>
public interface IForegroundProbe
{
    /// <summary>True when the foreground window belongs to a process in <c>game.process_names</c>.</summary>
    bool GameIsForeground { get; }

    /// <summary>True when such a process has a window at all.</summary>
    bool GameIsRunning { get; }
}

/// <summary>
/// Whether a WarCommand panel or menu currently owns the digits, Escape and Backspace. The state
/// lives in the pure machines; this is the composition root's one-line adapter to them.
/// </summary>
public interface IMenuGate
{
    /// <summary>True while a menu or panel is open.</summary>
    bool MenuIsOpen { get; }
}

/// <summary>A subsystem Panic switches off and on. One press moves every one of them.</summary>
/// <remarks>
/// <see cref="Resume"/> must re-derive state from its authority rather than trusting what it held
/// before the suspend. For the board that means re-seeding with
/// <c>GET /v1/deployments/{id}/board</c>.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Suspend and Resume are the words the spec uses for Panic. The agent is C# only.")]
public interface ISuspendable
{
    /// <summary>Stop. Release hooks, sessions, buffers and draws.</summary>
    void Suspend();

    /// <summary>Start again, re-deriving anything that could have changed while suspended.</summary>
    void Resume();
}

/// <summary>
/// An <see cref="ISuspendable"/> built from two calls, for a subsystem that must not take a
/// dependency on this assembly to be registered with Panic.
/// </summary>
public sealed class Suspendable(Action suspend, Action resume) : ISuspendable
{
    /// <inheritdoc />
    public void Suspend() => suspend();

    /// <inheritdoc />
    public void Resume() => resume();
}
