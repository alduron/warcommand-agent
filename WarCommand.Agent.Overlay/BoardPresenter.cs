namespace WarCommand.Agent.Overlay;

/// <summary>
/// Fans one board render out to every surface drawing it: the window's Board tab and, when it is
/// up, the in-game overlay.
/// </summary>
/// <remarks>
/// The composition root renders once. Two surfaces that were rendered separately would drift the
/// first time one of the call sites was missed, and the overlay is the one nobody is looking at
/// while they debug the window.
/// </remarks>
public sealed class BoardPresenter
{
    private readonly List<BoardView> _views = [];

    /// <summary>Creates a presenter over the surfaces that exist at startup.</summary>
    public BoardPresenter(params BoardView[] views)
    {
        ArgumentNullException.ThrowIfNull(views);
        _views.AddRange(views);
    }

    /// <summary>The last render, replayed into a surface that joins late.</summary>
    private Action<BoardView>? _last;

    private BoardHeader? _header;
    private string? _status;

    /// <summary>
    /// Adds a surface and brings it up to date. The overlay is built after the first render, so
    /// without the replay it would draw an empty panel until the next five-second poll.
    /// </summary>
    public void Add(BoardView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _views.Add(view);

        if (_header is { } header)
        {
            view.SetHeader(header);
        }

        if (_status is { } status)
        {
            view.SetStatus(status);
        }

        _last?.Invoke(view);
    }

    /// <summary>Renders the two header lines on every surface.</summary>
    public void SetHeader(BoardHeader header)
    {
        _header = header;
        Each(v => v.SetHeader(header));
    }

    /// <summary>The build line. Drawn on the window only; the overlay has no room for it.</summary>
    public void SetStatus(string text)
    {
        _status = text;
        Each(v => v.SetStatus(text));
    }

    /// <summary>The cold-start state, on every surface.</summary>
    public void ShowEmptyState(string title, string hint)
    {
        _last = v => v.ShowEmptyState(title, hint);
        Each(_last);
    }

    /// <summary>One snapshot of the board, on every surface.</summary>
    public void RenderBoard(
        IReadOnlyList<BoardRowViewModel> rows,
        IReadOnlyList<BoardRowViewModel> secondaryStrip,
        int overflowCount,
        int overflowUrgentCount)
    {
        _last = v => v.RenderBoard(rows, secondaryStrip, overflowCount, overflowUrgentCount);
        Each(_last);
    }

    private void Each(Action<BoardView> action)
    {
        foreach (var view in _views)
        {
            action(view);
        }
    }
}
