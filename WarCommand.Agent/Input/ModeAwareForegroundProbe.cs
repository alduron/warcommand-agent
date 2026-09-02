using WarCommand.Agent.Core.Settings;
using WarCommand.Agent.Input;

namespace WarCommand.Agent.Composition;

/// <summary>
/// The hotkey gate. Answers from the game while the overlay mirrors it, and answers yes while the
/// overlay is Always on.
/// </summary>
/// <remarks>
/// Binding rule 6 is unchanged: every binding except Panic is inert unless the game is the
/// foreground window. This supplies the ANSWER rather than removing the question, which is what
/// <see cref="IForegroundProbe"/> is for.
/// <para>
/// Always on means the user has said they are running the overlay without Wardogs in front of
/// them, and Wardogs is not out, so without this every binding is inert on every machine and the
/// keyboard does nothing at all. Mirroring keeps the strict answer, because mirroring is the mode
/// for somebody who has the game.
/// </para>
/// </remarks>
public sealed class ModeAwareForegroundProbe : IForegroundProbe
{
    private readonly IForegroundProbe _game;
    private readonly Func<OverlayMode> _mode;

    /// <summary>Creates the gate over the real probe.</summary>
    /// <param name="game">The game window watcher. Consulted in every mode but Always on.</param>
    /// <param name="mode">Read live, so changing the mode arms or disarms without a restart.</param>
    public ModeAwareForegroundProbe(IForegroundProbe game, Func<OverlayMode> mode)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(mode);

        _game = game;
        _mode = mode;
    }

    private bool Standalone => _mode() == OverlayMode.AlwaysOn;

    /// <inheritdoc />
    public bool GameIsRunning => Standalone || _game.GameIsRunning;

    /// <inheritdoc />
    public bool GameIsForeground => Standalone || _game.GameIsForeground;
}
