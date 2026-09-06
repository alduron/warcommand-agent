using System.Windows.Threading;
using WarCommand.Agent.Client.Realtime;
using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Realtime;
using Xunit;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// Every word the overlay can put up has a guaranteed way of coming down.
/// </summary>
/// <remarks>
/// Reported: status updates got stuck on the part of the header that shows the join code. Two
/// causes, both structural. The status was a single nullable string, so two independent sources
/// overwrote each other's word in either direction; and it was written into the header's right
/// cell, which is the cell the six-digit invite code lives in, so a word nobody took down covered
/// the digits somebody was about to read out loud.
/// <para>
/// Status is its own strip now, items stack, and a condition cannot be raised without naming how
/// it leaves. These tests walk EVERY declared condition rather than the two that exist today, so a
/// key added later without an exit fails here rather than sticking to somebody's overlay.
/// </para>
/// </remarks>
public sealed class NothingStaysOnTheOverlayTests
{
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static IEnumerable<HeaderCondition> EveryCondition => Enum.GetValues<HeaderCondition>();

    /// <summary>A renewed condition comes down on its own once its source stops renewing.</summary>
    [Fact]
    public void Every_renewed_condition_drops_when_its_source_goes_quiet()
    {
        foreach (var key in EveryCondition)
        {
            var conditions = new HeaderConditions();
            conditions.Raise(key, "WORD", StatusSeverity.Warn, ConditionExit.Renewed, T0);
            Assert.NotEmpty(conditions.Items());

            // Inside the grace it stands: an ordinary gap in the cadence must not blink it off.
            Assert.False(conditions.Sweep(T0 + HeaderConditions.RenewalGrace - TimeSpan.FromSeconds(1)));
            Assert.NotEmpty(conditions.Items());

            Assert.True(conditions.Sweep(T0 + HeaderConditions.RenewalGrace));
            Assert.Empty(conditions.Items());
        }
    }

    /// <summary>A session condition comes down with the session, whichever way the session ends.</summary>
    [Fact]
    public void Every_session_condition_drops_with_the_session()
    {
        foreach (var key in EveryCondition)
        {
            var conditions = new HeaderConditions();
            conditions.Raise(key, "WORD", StatusSeverity.Warn, ConditionExit.Session, T0);

            Assert.True(conditions.ClearAll());
            Assert.Empty(conditions.Items());
        }
    }

    /// <summary>Two sources at once, and neither erases the other.</summary>
    [Fact]
    public void Two_conditions_stand_together_rather_than_replacing_each_other()
    {
        var conditions = new HeaderConditions();
        conditions.Raise(HeaderCondition.BoardStale, "STALE", StatusSeverity.Warn, ConditionExit.Renewed, T0);
        conditions.Raise(HeaderCondition.AnotherDevice, "DEVICE", StatusSeverity.Warn, ConditionExit.Session, T0);

        Assert.Equal(2, conditions.Items().Count);

        // Clearing one leaves the other exactly where it was. The single-string version dropped
        // both, whichever source happened to clear first.
        conditions.Clear(HeaderCondition.BoardStale);
        Assert.Equal(["DEVICE"], conditions.Items().Select(item => item.Text));
    }

    /// <summary>Worst first, so the loudest thing wrong is the one the eye lands on.</summary>
    [Fact]
    public void The_strip_reads_worst_first()
    {
        var conditions = new HeaderConditions();
        conditions.Raise(HeaderCondition.BoardStale, "STALE", StatusSeverity.Warn, ConditionExit.Renewed, T0);
        conditions.Raise(HeaderCondition.AnotherDevice, "DEVICE", StatusSeverity.Fault, ConditionExit.Session, T0);

        Assert.Equal(["DEVICE", "STALE"], conditions.Items().Select(item => item.Text));
    }

    // --- the composed surface -------------------------------------------------------------------

    /// <summary>The three moments the agent stops being able to vouch for what it was told.</summary>
    [Theory]
    [InlineData("disconnect")]
    [InlineData("hop")]
    [InlineData("detach")]
    public void The_strip_is_empty_after_anything_that_ends_the_session(string ending)
    {
        var (observer, presenter) = Build();

        observer.OnAnotherDeviceOnBoard(present: true);
        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        Assert.Equal(2, presenter.Status.Count);

        switch (ending)
        {
            case "disconnect":
                observer.OnConnectionStateChanged(RealtimeConnectionState.Reconnecting);
                break;
            case "hop":
                observer.OnBoardCleared(BoardClearReason.DeploymentEntered);
                break;
            default:
                observer.Detach();
                break;
        }

        Assert.Empty(presenter.Status);
    }

    /// <summary>The reported bug, at the surface: the code stays readable whatever is wrong.</summary>
    [Fact]
    public void Nothing_a_source_says_can_reach_the_join_code()
    {
        var (observer, presenter) = Build();

        observer.OnAnotherDeviceOnBoard(present: true);
        observer.OnBoardStalenessChanged(stale: true, drainAgeSeconds: 90);
        observer.SetFault("ACCEPT REFUSED");

        Assert.Equal("921585", presenter.Header!.Right);
        Assert.Equal(3, presenter.Status.Count);
    }

    /// <summary>
    /// A banner about work in flight becomes the truth if the work never lands.
    /// </summary>
    /// <remarks>
    /// The backstop behind every other recovery path. "re-reading the board" describes something
    /// happening; if it is not happening the overlay must say so and try again, not hold the
    /// sentence.
    /// </remarks>
    [Fact]
    public void A_banner_about_work_in_flight_does_not_outlive_the_work()
    {
        var reloads = 0;
        var (observer, presenter) = Build(onConfigChanged: () => reloads++);

        observer.OnBoardCleared(BoardClearReason.DeploymentEntered);
        Assert.Equal("Switching deployment", presenter.EmptyState!.Value.Title);

        // Nothing comes back. No frame, no reload, no board.
        observer.ExpireNotice(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal("Board unreachable", presenter.EmptyState!.Value.Title);
        Assert.Equal("retrying", presenter.EmptyState!.Value.Hint);

        // And "retrying" is true: it asked for the read again rather than settling.
        Assert.Equal(1, reloads);

        observer.ExpireNotice(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Equal(2, reloads);
    }

    /// <summary>
    /// The retry backs off while the reads keep failing.
    /// </summary>
    /// <remarks>
    /// A config read fans out to a request per group. Repeating it every twelve seconds against an
    /// API that is refusing is how a rate limit becomes permanent: the read that would clear it is
    /// the read holding it open. The banner is already honest after the first escalation, so
    /// slowing down costs the reader nothing.
    /// </remarks>
    [Fact]
    public void A_retry_that_keeps_failing_slows_down()
    {
        var reloads = 0;
        var now = DateTimeOffset.UnixEpoch;
        var (observer, _) = Build(onConfigChanged: () => reloads++, serverNow: () => now);

        observer.OnBoardCleared(BoardClearReason.DeploymentEntered);

        // The promise: honest within twelve seconds, whatever else is true.
        observer.ExpireNotice(now.AddSeconds(12));
        Assert.Equal(1, reloads);

        // The second ask is not twelve seconds later.
        observer.ExpireNotice(now.AddSeconds(24));
        Assert.Equal(1, reloads);

        observer.ExpireNotice(now.AddSeconds(40));
        Assert.Equal(2, reloads);

        // Still asking, just not in a burst: an hour of a dead API is not hundreds of reads.
        observer.ExpireNotice(now.AddHours(1));
        Assert.Equal(3, reloads);
    }

    /// <summary>A board arriving disarms the watchdog, so a healthy hop never escalates.</summary>
    [Fact]
    public void A_hop_that_lands_never_escalates()
    {
        var reloads = 0;
        var (observer, _) = Build(onConfigChanged: () => reloads++);
        var catalog = BundledContracts.Catalog().Current;

        observer.OnBoardCleared(BoardClearReason.DeploymentEntered);

        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, T0, draft: null);
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / BRAVO" });

        observer.ExpireNotice(DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(0, reloads);
    }

    private static (BoardRealtimeObserver Observer, BoardPresenter Presenter) Build(
        Action? onConfigChanged = null,
        Func<DateTimeOffset>? serverNow = null)
    {
        var catalog = BundledContracts.Catalog().Current;
        var presenter = new BoardPresenter();
        var observer = new BoardRealtimeObserver(
            Dispatcher.CurrentDispatcher,
            presenter,
            () => catalog,
            _ => { },
            _ => { },
            onConfigChanged ?? (() => { }),
            _ => { },
            serverNow: serverNow);

        var board = new BoardState(Viewer, catalog.GrammarRules);
        board.EnterDeployment(Deployment, T0, draft: null);
        observer.Attach(board, Viewer, new BoardHeader { Title = "61ST / ALPHA", Right = "921585" });
        return (observer, presenter);
    }
}
