using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The single most test-worthy class in the agent. The named risk is a slot number reused mid
/// sentence, which claims the wrong request by voice.
/// </summary>
public class SlotAllocatorTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    [Fact]
    public void A_fresh_board_hands_out_one_through_nine_in_order()
    {
        var allocator = new SlotAllocator();
        var slots = Enumerable.Range(0, 9).Select(_ => allocator.Acquire(Guid.NewGuid(), T0)).ToList();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], slots);
        Assert.True(allocator.IsFull);
    }

    [Fact]
    public void Acquire_returns_null_when_every_digit_is_out()
    {
        var allocator = new SlotAllocator();
        for (var i = 0; i < 9; i++)
        {
            allocator.Acquire(Guid.NewGuid(), T0);
        }

        Assert.Null(allocator.Acquire(Guid.NewGuid(), T0));
    }

    [Fact]
    public void A_digit_is_never_reissued_until_the_other_eight_have_been()
    {
        // The risk row: somebody reads a digit, starts speaking, and a new request lands on it.
        var allocator = new SlotAllocator();
        var first = Guid.NewGuid();
        var released = allocator.Acquire(first, T0)!.Value;
        allocator.Release(first, T0.AddSeconds(1));

        var reissued = new List<int>();
        for (var i = 0; i < 9; i++)
        {
            reissued.Add(allocator.Acquire(Guid.NewGuid(), T0.AddSeconds(2 + i))!.Value);
        }

        Assert.Equal(9, reissued.Distinct().Count());
        Assert.DoesNotContain(released, reissued.Take(8));
        Assert.Equal(released, reissued[8]);
    }

    [Fact]
    public void A_released_digit_goes_to_the_back_of_the_reissue_queue()
    {
        var allocator = new SlotAllocator();
        var first = Guid.NewGuid();
        allocator.Acquire(first, T0);
        allocator.Acquire(Guid.NewGuid(), T0);

        allocator.Release(first, T0.AddSeconds(1));

        // Eight digits were never issued at all; 1 comes after every one of them.
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 1], allocator.ReissueOrder);
    }

    [Fact]
    public void There_is_no_quarantine_a_freed_digit_is_available_immediately()
    {
        var allocator = new SlotAllocator();
        var ids = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            allocator.Acquire(id, T0);
        }

        allocator.Release(ids[0], T0);

        // Zero dead time: a crew clearing a row every 2 s must not watch the board drain.
        Assert.Equal(1, allocator.Acquire(Guid.NewGuid(), T0));
    }

    [Fact]
    public void Acquiring_twice_returns_the_same_digit()
    {
        var allocator = new SlotAllocator();
        var id = Guid.NewGuid();

        Assert.Equal(allocator.Acquire(id, T0), allocator.Acquire(id, T0.AddSeconds(5)));
        Assert.Equal(1, allocator.Held);
    }

    [Fact]
    public void Admission_is_priority_desc_then_created_at_asc()
    {
        var allocator = new SlotAllocator();
        var old = Rows.A(priority: Priority.Normal, createdAt: T0);
        var newer = Rows.A(priority: Priority.Normal, createdAt: T0.AddSeconds(30));
        var urgentLate = Rows.A(priority: Priority.Urgent, createdAt: T0.AddSeconds(60));
        var low = Rows.A(priority: Priority.Low, createdAt: T0.AddSeconds(-60));

        var order = new List<string>();
        var overflow = new List<BoardRow> { old, newer, urgentLate, low };
        while (overflow.Count > 0)
        {
            var admitted = allocator.AdmitNext(overflow, T0);
            Assert.NotNull(admitted);
            order.Add(admitted!.Id == urgentLate.Id ? "urgent"
                : admitted.Id == old.Id ? "old"
                : admitted.Id == newer.Id ? "newer" : "low");
            overflow.RemoveAll(r => r.Id == admitted.Id);
        }

        // A danger_close mission arriving as candidate 15 gets in ahead of a fortify.
        Assert.Equal(["urgent", "old", "newer", "low"], order);
    }

    [Fact]
    public void Admission_carries_the_digit_on_the_row_it_chose()
    {
        var allocator = new SlotAllocator();
        var row = Rows.A();

        var admitted = allocator.AdmitNext([row], T0);

        Assert.Equal(1, admitted!.Slot);
        Assert.Equal(row.Id, admitted.Id);
        Assert.Equal(1, allocator.SlotOf(row.Id));
    }

    [Fact]
    public void Admission_yields_nothing_when_the_board_is_full()
    {
        var allocator = new SlotAllocator();
        for (var i = 0; i < 9; i++)
        {
            allocator.Acquire(Guid.NewGuid(), T0);
        }

        Assert.Null(allocator.AdmitNext([Rows.A()], T0));
    }

    [Fact]
    public void Reset_clears_the_reissue_order_as_well_as_the_assignments()
    {
        var allocator = new SlotAllocator();
        var id = Guid.NewGuid();
        allocator.Acquire(id, T0);
        allocator.Acquire(Guid.NewGuid(), T0);
        allocator.Release(id, T0.AddSeconds(1));

        allocator.Reset();

        // Carrying slots across a deployment change is the worst available bug here.
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], allocator.ReissueOrder);
        Assert.Equal(0, allocator.Held);
        Assert.Null(allocator.SlotOf(id));
    }

    [Fact]
    public void Releasing_something_it_never_held_does_nothing()
    {
        var allocator = new SlotAllocator();
        allocator.Release(Guid.NewGuid(), T0);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], allocator.ReissueOrder);
    }

    [Fact]
    public void Acquire_records_when_the_digit_was_taken_so_residency_can_expire()
    {
        var allocator = new SlotAllocator();
        var id = Guid.NewGuid();
        allocator.Acquire(id, T0);

        Assert.Equal(T0, allocator.AcquiredAt(id));
    }
}
