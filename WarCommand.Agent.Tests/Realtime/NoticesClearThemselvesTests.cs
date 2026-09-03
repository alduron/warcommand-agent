using System.Windows.Threading;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// A status word on the header takes itself off.
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

    private static (BoardRealtimeObserver Observer, BoardPresenter Presenter) Build()
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
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA" });
        return (observer, presenter);
    }

    [Fact]
    public void A_notice_is_gone_a_few_seconds_later_with_nobody_touching_anything()
    {
        var (observer, presenter) = Build();

        observer.SetFault("NO GAME WINDOW  NOT READ, TRY AGAIN");
        Assert.Equal("NO GAME WINDOW  NOT READ, TRY AGAIN", presenter.Header!.Fault);

        // Still there while it is worth reading.
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.NotNull(presenter.Header!.Fault);

        // Gone on its own. No hold, no keypress, no game window: none of those are available to
        // somebody the message is telling that the game window is missing.
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Null(presenter.Header!.Fault);
    }

    [Fact]
    public void A_standing_condition_is_not_a_notice_and_waits_for_its_own_signal()
    {
        var (observer, presenter) = Build();

        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));

        // The board really is stale and still is. Timing this one out would replace a true warning
        // with a clean header while the rows on screen went on being wrong.
        Assert.Equal("BOARD MAY BE STALE", presenter.Header!.Fault);

        observer.OnBoardStalenessChanged(stale: false, drainAgeSeconds: 0);
        Assert.Null(presenter.Header!.Fault);
    }

    [Fact]
    public void A_notice_covers_a_condition_and_uncovers_it_again()
    {
        var (observer, presenter) = Build();

        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.SetFault("ACCEPT REFUSED");
        Assert.Equal("ACCEPT REFUSED", presenter.Header!.Fault);

        observer.ExpireNotice(DateTimeOffset.UtcNow.AddSeconds(30));

        // The one-off goes; the thing that is still true comes back.
        Assert.Equal("BOARD MAY BE STALE", presenter.Header!.Fault);
    }

    [Fact]
    public void Clearing_a_notice_by_hand_still_works()
    {
        var (observer, presenter) = Build();

        observer.SetFault("SUBMIT FAILED");
        observer.SetFault(null);

        Assert.Null(presenter.Header!.Fault);
    }
}
