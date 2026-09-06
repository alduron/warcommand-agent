using System.Windows.Threading;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// A status word takes itself off the strip.
/// </summary>
/// <remarks>
/// It used to be cleared by exactly one thing, a hold opening, and a hold needs the game to be the
/// foreground window. "NO GAME WINDOW  NOT READ, TRY AGAIN" could therefore never be cleared by
/// definition: the one condition that removed it was the one the message said was absent. Every
/// refusal, every failed read and every confirmation stuck to the overlay for the rest of the
/// session, so the header described something that had stopped being true minutes earlier.
/// </remarks>
public sealed class NoticesClearThemselvesTests
{
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static (BoardRealtimeObserver Observer, BoardPresenter Presenter) Build(
        string? inviteCode = null)
    {
        var catalog = BundledContracts.Catalog().Current;
        var presenter = new BoardPresenter();
        var observer = new BoardRealtimeObserver(
            Dispatcher.CurrentDispatcher,
            presenter,
            () => catalog,
            _ => { },
            _ => { },
            () => { },
            _ => { });

        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, T0, draft: null);
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA", Right = inviteCode });
        return (observer, presenter);
    }

    /// <summary>Everything the strip is showing, in the order it draws.</summary>
    private static string[] Strip(BoardPresenter presenter) =>
        [.. presenter.Status.Select(item => item.Text)];

    [Fact]
    public void A_notice_is_gone_a_few_seconds_later_with_nobody_touching_anything()
    {
        var (observer, presenter) = Build();

        observer.SetFault("NO GAME WINDOW  NOT READ, TRY AGAIN");
        Assert.Equal(["NO GAME WINDOW  NOT READ, TRY AGAIN"], Strip(presenter));

        // Still there while it is worth reading.
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.NotEmpty(presenter.Status);

        // Gone on its own. No hold, no keypress, no game window: none of those are available to
        // somebody the message is telling that the game window is missing.
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Empty(presenter.Status);
    }

    /// <summary>The join code cell is the join code cell. Status never reaches it.</summary>
    [Fact]
    public void A_status_word_never_covers_the_join_code()
    {
        var (observer, presenter) = Build("921585");

        observer.SetFault("ACCEPT REFUSED");
        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);

        Assert.Equal("921585", presenter.Header!.Right);
        Assert.Contains("ACCEPT REFUSED", Strip(presenter));
    }

    [Fact]
    public void A_standing_condition_is_not_a_notice_and_waits_for_its_own_signal()
    {
        var (observer, presenter) = Build();

        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));

        // The board really is stale and still is. Timing this one out on the notice deadline would
        // replace a true warning with a clean strip while the rows on screen went on being wrong.
        Assert.Equal(["BOARD MAY BE STALE"], Strip(presenter));

        observer.OnBoardStalenessChanged(stale: false, drainAgeSeconds: 0);
        Assert.Empty(presenter.Status);
    }

    /// <summary>
    /// A notice and a condition STACK. Neither hides the other, which is the whole point of the
    /// strip: the single-string header showed one of two true things and gave no sign of the other.
    /// </summary>
    [Fact]
    public void A_notice_stands_beside_a_condition_rather_than_covering_it()
    {
        var (observer, presenter) = Build();

        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.SetFault("ACCEPT REFUSED");

        // Worst first: a refusal outranks a warning, and both are on screen.
        Assert.Equal(["ACCEPT REFUSED", "BOARD MAY BE STALE"], Strip(presenter));

        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));

        // The one-off goes; the thing that is still true stays exactly where it was.
        Assert.Equal(["BOARD MAY BE STALE"], Strip(presenter));
    }

    [Fact]
    public void Clearing_a_notice_by_hand_still_works()
    {
        var (observer, presenter) = Build();

        observer.SetFault("SUBMIT FAILED");
        observer.SetFault(null);

        Assert.Empty(presenter.Status);
    }
}
