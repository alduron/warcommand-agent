using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Board;

/// <summary>
/// Owns the claimable digits 1..<see cref="MaxSlots"/> on one viewer's board. Allocation is least
/// recently released; admission from overflow is (priority DESC, created_at ASC). Board order is
/// neither of those: it is always ascending by slot.
/// </summary>
/// <remarks>
/// There is no quarantine. <c>now</c> is a parameter on every mutator and the allocator never calls
/// a clock.
/// </remarks>
public sealed class SlotAllocator
{
    /// <summary>Nine digits, because zero is excluded and letters were rejected.</summary>
    public const int DefaultMaxSlots = 9;

    private readonly List<FreeSlot> _free = [];
    private readonly Dictionary<Guid, int> _held = [];
    private readonly Dictionary<Guid, DateTimeOffset> _acquiredAt = [];
    private long _sequence;

    public SlotAllocator(int maxSlots = DefaultMaxSlots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSlots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSlots, DefaultMaxSlots);
        MaxSlots = maxSlots;
        Reset();
    }

    public int MaxSlots { get; }

    /// <summary>How many digits are currently assigned.</summary>
    public int Held => _held.Count;

    public bool IsFull => _free.Count == 0;

    /// <summary>Free digits in the order they will be reissued. Least recently released first.</summary>
    public IReadOnlyList<int> ReissueOrder => [.. Ordered().Select(f => f.Slot)];

    /// <summary>The digit this request answers to, or null when it holds none.</summary>
    public int? SlotOf(Guid requestId) => _held.TryGetValue(requestId, out var slot) ? slot : null;

    /// <summary>When the request took its digit. Null when it holds none. Drives slot residency.</summary>
    public DateTimeOffset? AcquiredAt(Guid requestId) =>
        _acquiredAt.TryGetValue(requestId, out var at) ? at : null;

    /// <summary>
    /// Least-recently-released free slot 1..<see cref="MaxSlots"/>. Returns null when full, and the
    /// digit already held when the request has one.
    /// </summary>
    public int? Acquire(Guid requestId, DateTimeOffset now)
    {
        if (_held.TryGetValue(requestId, out var existing))
        {
            return existing;
        }

        if (_free.Count == 0)
        {
            return null;
        }

        var next = Ordered().First();
        _free.Remove(next);
        _held[requestId] = next.Slot;
        _acquiredAt[requestId] = now;
        return next.Slot;
    }

    /// <summary>Releases immediately. The digit goes to the back of the reissue queue.</summary>
    public void Release(Guid requestId, DateTimeOffset now)
    {
        if (!_held.Remove(requestId, out var slot))
        {
            return;
        }

        _acquiredAt.Remove(requestId);
        _free.Add(new FreeSlot(slot, now, _sequence++));
    }

    /// <summary>
    /// Gives one freed digit to the highest-ranking overflow candidate by
    /// (priority DESC, created_at ASC), and returns that row carrying its new slot. Null when the
    /// board is full or nothing was admissible.
    /// </summary>
    /// <remarks>
    /// The sketch in 10-agent-spec.md returns the digit; the row is returned instead because a bare
    /// digit does not tell the caller which candidate received it.
    /// </remarks>
    public BoardRow? AdmitNext(IReadOnlyList<BoardRow> overflow, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        if (_free.Count == 0)
        {
            return null;
        }

        foreach (var row in overflow.Order(AdmissionOrder.Instance))
        {
            if (_held.ContainsKey(row.Id))
            {
                continue;
            }

            var slot = Acquire(row.Id, now);
            if (slot is { } digit)
            {
                return row.WithSlot(digit);
            }
        }

        return null;
    }

    /// <summary>
    /// Drops every assignment and restores the reissue order to 1..<see cref="MaxSlots"/> ascending.
    /// Called on a deployment change: nothing from the old match may reclaim a digit.
    /// </summary>
    public void Reset()
    {
        _free.Clear();
        _held.Clear();
        _acquiredAt.Clear();
        _sequence = 0;
        for (var slot = 1; slot <= MaxSlots; slot++)
        {
            _free.Add(new FreeSlot(slot, null, _sequence++));
        }
    }

    private IEnumerable<FreeSlot> Ordered() =>
        _free.OrderBy(f => f.ReleasedAt ?? DateTimeOffset.MinValue).ThenBy(f => f.Sequence);

    private readonly record struct FreeSlot(int Slot, DateTimeOffset? ReleasedAt, long Sequence);
}

/// <summary>
/// The admission key, (priority DESC, created_at ASC), ties broken on id so it is total. Never a
/// board sort: the board sorts by slot ascending and nothing reorders it.
/// </summary>
public sealed class AdmissionOrder : IComparer<BoardRow>
{
    public static AdmissionOrder Instance { get; } = new();

    public int Compare(BoardRow? x, BoardRow? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var byPriority = y.Priority.CompareTo(x.Priority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        var byAge = x.CreatedAt.CompareTo(y.CreatedAt);
        return byAge != 0 ? byAge : x.Id.CompareTo(y.Id);
    }
}
