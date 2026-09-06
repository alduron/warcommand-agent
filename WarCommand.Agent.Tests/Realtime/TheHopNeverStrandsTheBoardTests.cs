using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;
using Xunit;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// Swapping deployments must never leave the overlay on the banner that says it is swapping.
/// </summary>
/// <remarks>
/// Reported: "I swapped deployments and my overlay says SWITCHING DEPLOYMENT re-reading the board
/// and it appears to be stuck." Every <c>deployment.entered</c> clears the board and paints that
/// banner, and the re-read behind it was skipped whenever the standing id already matched the
/// frame's, which is exactly what an entry made over HTTPS leaves behind. Nothing else was coming,
/// so the banner stayed until the two minute config fallback happened to fire.
/// </remarks>
public class TheHopNeverStrandsTheBoardTests
{
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>The reported bug, as one predicate.</summary>
    [Fact]
    public void Standing_on_the_frames_deployment_with_no_board_still_re_reads()
    {
        Assert.True(BoardRealtimeObserver.NeedsConfigReload(Deployment, Deployment, hasBoard: false));
    }

    [Fact]
    public void A_frame_for_a_new_deployment_always_re_reads()
    {
        Assert.True(BoardRealtimeObserver.NeedsConfigReload(Guid.NewGuid(), Deployment, hasBoard: true));
        Assert.True(BoardRealtimeObserver.NeedsConfigReload(null, Deployment, hasBoard: true));
    }

    /// <summary>The case the guard exists for: a redundant frame costs no HTTP round trip.</summary>
    [Fact]
    public void A_frame_for_the_board_already_held_re_reads_nothing()
    {
        Assert.False(BoardRealtimeObserver.NeedsConfigReload(Deployment, Deployment, hasBoard: true));
    }

    [Fact]
    public void The_cleared_board_names_the_reason_it_was_cleared()
    {
        var presenter = new BoardPresenter();
        var observer = Observer(presenter);

        observer.OnBoardCleared(BoardClearReason.DeploymentEntered);
        Assert.Equal("Switching deployment", presenter.EmptyState!.Value.Title);

        observer.OnBoardCleared(BoardClearReason.DeploymentClosed);
        Assert.Equal("Deployment closed", presenter.EmptyState!.Value.Title);

        // A replay-buffer miss is not a hop. It used to say the match had changed when it had not.
        observer.OnBoardCleared(BoardClearReason.ResyncRequired);
        Assert.Equal("Reconnected", presenter.EmptyState!.Value.Title);
    }

    /// <summary>Clearing drops the board, which is what makes the re-read obligatory.</summary>
    [Fact]
    public void Clearing_the_board_leaves_nothing_to_render_into()
    {
        var presenter = new BoardPresenter();
        var observer = Observer(presenter);
        var catalog = BundledContracts.Catalog().Current;
        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, T0, draft: null);
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA" });

        Assert.NotNull(observer.Board);

        observer.OnBoardCleared(BoardClearReason.DeploymentEntered);

        Assert.Null(observer.Board);
        Assert.True(BoardRealtimeObserver.NeedsConfigReload(Deployment, Deployment, observer.Board is not null));
    }

    private static BoardRealtimeObserver Observer(BoardPresenter presenter) => new(
        Dispatcher.CurrentDispatcher,
        presenter,
        () => BundledContracts.Catalog().Current,
        _ => { },
        _ => { },
        () => { },
        _ => { });
}
