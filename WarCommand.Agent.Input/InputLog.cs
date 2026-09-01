namespace WarCommand.Agent.Input;

/// <summary>
/// Everything this assembly is allowed to say. An enum rather than a message, so there is no channel
/// through which a key code could reach a log even by accident.
/// </summary>
public enum InputEvent
{
    /// <summary>Never emitted. Present so the enum has a zero value.</summary>
    None = 0,

    /// <summary>The low-level keyboard hook was installed in our own process.</summary>
    KeyboardHookInstalled,

    /// <summary>The low-level keyboard hook was removed.</summary>
    KeyboardHookRemoved,

    /// <summary>The low-level mouse hook was installed in our own process.</summary>
    MouseHookInstalled,

    /// <summary>The low-level mouse hook was removed.</summary>
    MouseHookRemoved,

    /// <summary>Windows removed a hook for exceeding its callback timeout, and it was reinstalled.</summary>
    HookReinstalled,

    /// <summary>Panic engaged. Every subsystem suspended.</summary>
    PanicEngaged,

    /// <summary>Panic released. Every subsystem resumed and state re-derived from the server.</summary>
    PanicReleased,

    /// <summary>Hotkey processing enabled, the game window having been found.</summary>
    HotkeysEnabled,

    /// <summary>Hotkey processing disabled. Panic is unaffected.</summary>
    HotkeysDisabled,

    /// <summary>A window belonging to a process in game.process_names appeared.</summary>
    GameWindowFound,

    /// <summary>That window went away.</summary>
    GameWindowLost,

    /// <summary>The game holds the display exclusively. The borderless-windowed prompt was raised.</summary>
    ExclusiveFullscreenDetected,

    /// <summary>A rebind capture opened.</summary>
    RebindStarted,

    /// <summary>A rebind capture applied a chord.</summary>
    RebindCaptured,

    /// <summary>A rebind was refused because another WarCommand binding holds the chord.</summary>
    RebindRefusedConflict,

    /// <summary>A rebind capture hit its five second abort.</summary>
    RebindAborted,
}

/// <summary>
/// The only logging channel in this assembly. It takes an <see cref="InputEvent"/> and nothing else:
/// no string, no object, no number. <c>Note($"...{code}")</c> does not compile, which is the
/// mechanism behind "never log a key code", rather than a comment asking nobody to.
/// </summary>
public interface IInputLog
{
    /// <summary>Records that something happened. There is no parameter a key code could ride in on.</summary>
    void Note(InputEvent inputEvent);
}

/// <summary>Discards every event. The default when the composition root wires no log.</summary>
public sealed class NullInputLog : IInputLog
{
    /// <summary>The shared instance.</summary>
    public static NullInputLog Instance { get; } = new();

    /// <inheritdoc />
    public void Note(InputEvent inputEvent)
    {
        // Nothing is recorded.
    }
}
