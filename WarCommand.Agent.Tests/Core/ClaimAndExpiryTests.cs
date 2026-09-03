using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// What happens to a row when somebody takes it, and when nobody does.
/// </summary>
/// <remarks>
/// Both were reported from a live two-machine test: a provider accepted a job and watched it vanish
/// off their own overlay, and rows the web board had finished sat at zero on the overlay forever.
/// </remarks>
public sealed class ClaimAndExpiryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly Guid Viewer = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static BoardState Board(Guid viewer) =>
        new(viewer, ContractFixtures.Catalog.GrammarRules);

    private static BoardRow Row(Guid requester, DateTimeOffset expiresAt) =>
        Rows.A() with
        {
            Id = Guid.NewGuid(),
            RequestedByParticipantId = requester,
            CreatedAt = T0,
            ExpiresAt = expiresAt,
            State = RequestState.Open,
        };

    [Fact]
    public void The_claimant_keeps_the_row_and_keeps_its_digit()
    {
        var board = Board(Viewer);
        var row = Row(Other, T0.AddSeconds(120));
        board.Upsert(row, T0);

        var claimed = board.ApplyClaim(row.Id, Viewer, "GHOST", row.Version + 1, T0);

        // It moves into YOURS rather than leaving. The handler used to Remove for everyone, so the
        // provider watched the job they had just accepted disappear.
        Assert.NotNull(claimed);
        Assert.Contains(board.Yours, r => r.Id == row.Id);

        // And it KEEPS its digit: done, start, release and copy all address a row by it.
        Assert.NotNull(claimed.Slot);
    }

    [Fact]
    public void The_requester_keeps_the_row_without_a_digit()
    {
        var board = Board(Viewer);
        var row = Row(Viewer, T0.AddSeconds(120));
        board.Upsert(row, T0);

        var claimed = board.ApplyClaim(row.Id, Other, "BEAR", row.Version + 1, T0);

        // Somebody took your request, which is news rather than a job: you keep sight of it, and
        // you get no digit because there is nothing for you to do to it.
        Assert.NotNull(claimed);
        Assert.Contains(board.Yours, r => r.Id == row.Id);
        Assert.Null(claimed.Slot);
        Assert.Equal("BEAR", claimed.ClaimantCallsign);
    }

    [Fact]
    public void Everybody_else_loses_the_row_and_counts_it_instead()
    {
        var board = Board(Viewer);
        var row = Row(Other, T0.AddSeconds(120));
        board.Upsert(row, T0);

        var claimed = board.ApplyClaim(row.Id, Guid.NewGuid(), "WOLF", row.Version + 1, T0);

        Assert.Null(claimed);
        Assert.DoesNotContain(board.Rows, r => r.Id == row.Id);
        Assert.Empty(board.Yours);
    }

    [Fact]
    public void A_row_that_runs_out_of_time_leaves_on_its_own()
    {
        var board = Board(Viewer);
        var row = Row(Other, T0.AddSeconds(120));
        board.Upsert(row, T0);

        // Still there while it has time.
        Assert.Empty(board.Tick(T0.AddSeconds(119)).Expired);
        Assert.Contains(board.Rows, r => r.Id == row.Id);

        // Gone the moment it does not. The tick only demoted low-priority rows before, so a row the
        // web board had already finished sat at zero on the overlay until something else moved it.
        var tick = board.Tick(T0.AddSeconds(120));

        Assert.Contains(tick.Expired, r => r.Id == row.Id);
        Assert.DoesNotContain(board.Rows, r => r.Id == row.Id);
        Assert.True(tick.Changed);
    }

    [Fact]
    public void A_claimed_row_never_expires()
    {
        var board = Board(Viewer);
        var row = Row(Other, T0.AddSeconds(120));
        board.Upsert(row, T0);
        board.ApplyClaim(row.Id, Viewer, "GHOST", row.Version + 1, T0);

        // ttl measures the wait for a taker, not the life of the job. A claimed row outliving its
        // ttl is a provider still working, and taking it off them mid-job is the worst outcome.
        var tick = board.Tick(T0.AddSeconds(600));

        Assert.Empty(tick.Expired);
        Assert.Contains(board.Yours, r => r.Id == row.Id);
    }

    [Fact]
    public void Expiring_a_row_returns_its_digit_to_the_back_of_the_queue()
    {
        var board = Board(Viewer);
        var first = Row(Other, T0.AddSeconds(120));
        board.Upsert(first, T0);
        var freed = board.Rows.Single(r => r.Id == first.Id).Slot;

        board.Tick(T0.AddSeconds(121));

        var second = Row(Other, T0.AddSeconds(300));
        board.Upsert(second, T0.AddSeconds(121));
        var next = board.Rows.Single(r => r.Id == second.Id).Slot;

        // The new row gets a digit, so expiry really did free the allocator. It is NOT the freed
        // one: allocation is least-recently-released, so a digit is not reissued until the other
        // eight have been, and a provider who glanced away does not find 1 meaning something new.
        Assert.NotNull(next);
        Assert.NotEqual(freed, next);
    }
}
