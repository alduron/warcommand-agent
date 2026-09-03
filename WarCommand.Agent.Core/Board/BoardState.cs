using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Board;

/// <summary>
/// Whatever holds the pending draft, so a deployment hop can abort it before any row is dropped.
/// Satisfied by the PTT state machine.
/// </summary>
public interface IDraftOwner
{
    /// <summary>Discards the pending draft. True when there was one.</summary>
    bool AbortDraft(DateTimeOffset now);
}

/// <summary>What one <see cref="BoardState.Tick"/> changed.</summary>
public sealed record BoardTick(
    IReadOnlyList<BoardRow> Demoted,
    IReadOnlyList<BoardRow> Admitted,
    IReadOnlyList<BoardRow> Expired)
{
    public static BoardTick Empty { get; } = new([], [], []);

    public bool Changed => Demoted.Count > 0 || Admitted.Count > 0 || Expired.Count > 0;
}

/// <summary>What the deployment hop did, in the order 10-agent-spec.md requires.</summary>
public sealed record DeploymentChange(Guid DeploymentId, bool DraftAborted, int RowsDropped);

/// <summary>
/// One viewer's board for one deployment: which rows exist, which hold a digit, and which sit in
/// overflow. Pure; <c>now</c> is always a parameter.
/// </summary>
/// <remarks>
/// The four row-returning frames are upserts that reacquire a slot, never deltas. Board order is
/// ascending by slot and never re-sorted. Nothing survives a deployment change.
/// </remarks>
public sealed class BoardState
{
    private readonly Dictionary<Guid, BoardRow> _rows = [];
    private readonly HashSet<Guid> _passed = [];
    private readonly HashSet<Guid> _mutedRequesters = [];
    private readonly HashSet<Guid> _demoted = [];
    private readonly TimeSpan _lowPriorityResidency;

    public BoardState(Guid viewerParticipantId, GrammarRulesDef rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ViewerParticipantId = viewerParticipantId;
        Allocator = new SlotAllocator(rules.MaxSlots);
        _lowPriorityResidency = TimeSpan.FromSeconds(rules.LowPrioritySlotResidencyS);
    }

    public Guid ViewerParticipantId { get; }

    /// <summary>Null until the first deployment is entered. An empty board is not a fault.</summary>
    public Guid? DeploymentId { get; private set; }

    public SlotAllocator Allocator { get; }

    /// <summary>
    /// Rows holding a digit, ascending by slot. Never re-sorted for priority or age.
    /// </summary>
    /// <remarks>
    /// Excludes anything drawn in <see cref="Yours"/>. A row the viewer claimed keeps its digit so
    /// 'done 4' still reaches it, but it draws once, at the foot, not twice.
    /// </remarks>
    public IReadOnlyList<BoardRow> Rows =>
        [.. _rows.Values
            .Where(r => r.HoldsSlot && Visible(r) && !r.RendersInYours(ViewerParticipantId))
            .OrderBy(r => r.Slot!.Value)];

    /// <summary>
    /// Open rows with no digit, in admission order. Visible in the overflow line and not claimable
    /// by voice. A demoted row is here: only its claim on a digit expired.
    /// </summary>
    public IReadOnlyList<BoardRow> Overflow =>
        [.. _rows.Values
            .Where(r => Visible(r) && r.IsOpen && !r.HoldsSlot)
            .Order(AdmissionOrder.Instance)];

    /// <summary>
    /// Held rows this viewer is one half of: they took it, or they asked for it. The YOURS section
    /// at the foot of the board. No digit, and never re-sorted into the claimable queue.
    /// </summary>
    public IReadOnlyList<BoardRow> Yours =>
        [.. _rows.Values
            .Where(r => Visible(r) && r.RendersInYours(ViewerParticipantId))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)];

    /// <summary>
    /// How many held rows this viewer has no part in. Rendered as the single IN PROGRESS count and
    /// never as rows: enough to know work is already running before calling the same target again.
    /// </summary>
    public int InProgressCount =>
        _rows.Values.Count(r => Visible(r) && r.CountsAsInProgress(ViewerParticipantId));

    /// <summary>Every row the board is tracking, hidden ones included.</summary>
    public IReadOnlyCollection<BoardRow> All => _rows.Values;

    /// <summary>Requesters hidden on this board only, for the rest of the match.</summary>
    public IReadOnlyCollection<Guid> MutedRequesters => _mutedRequesters;

    /// <summary>Rows whose low-priority residency expired. Still open; only the digit went.</summary>
    public IReadOnlyCollection<Guid> DemotedRequestIds => _demoted;

    public BoardRow? ById(Guid requestId) => _rows.TryGetValue(requestId, out var row) ? row : null;

    /// <summary>The row a spoken digit names, or null when that slot is empty.</summary>
    public BoardRow? BySlot(int slot) => _rows.Values.FirstOrDefault(r => r.Slot == slot && Visible(r));

    /// <summary>
    /// Upsert by id. Every row-returning frame lands here and reacquires a digit; a row claimed by
    /// somebody else loses its digit; a terminal row leaves. Returns the stored row, or null when
    /// the row was dropped or is hidden on this board.
    /// </summary>
    public BoardRow? Upsert(BoardRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsTerminal)
        {
            Remove(row.Id, now);
            return null;
        }

        var hidden = _passed.Contains(row.Id) || _mutedRequesters.Contains(row.RequestedByParticipantId);
        if (hidden)
        {
            Allocator.Release(row.Id, now);
            _rows[row.Id] = row.WithoutSlot() with { Muted = _mutedRequesters.Contains(row.RequestedByParticipantId) };
            Admit(now);
            return null;
        }

        // A row held by anybody but the viewer leaves the slot board and is not claimable by digit.
        if (row.IsHeld && !row.IsClaimedBy(ViewerParticipantId))
        {
            Allocator.Release(row.Id, now);
            _rows[row.Id] = row.WithoutSlot();
            Admit(now);
            return _rows[row.Id];
        }

        var slot = _demoted.Contains(row.Id) ? Allocator.SlotOf(row.Id) : Allocator.Acquire(row.Id, now);
        var stored = slot is { } digit ? row.WithSlot(digit) : row.WithoutSlot();
        _rows[row.Id] = stored;
        return stored;
    }

    /// <summary>Drops a row and frees its digit. True when the row was there.</summary>
    /// <summary>
    /// Applies a claim to a row already on this board. Returns the stored row, or null when it left.
    /// </summary>
    /// <remarks>
    /// request.claimed carries no body, so it cannot be upserted: it is a thin notice that the row
    /// is taken. For the two people IN the row, the claimant and the requester, it becomes a YOURS
    /// row and stays. For everyone else it leaves and becomes one of the IN PROGRESS count.
    /// <para>
    /// The handler used to Remove for everybody, so a provider watched the job they had just
    /// accepted vanish off their own overlay.
    /// </para>
    /// </remarks>
    public BoardRow? ApplyClaim(
        Guid requestId,
        Guid claimantParticipantId,
        string claimantCallsign,
        int version,
        DateTimeOffset now)
    {
        if (!_rows.TryGetValue(requestId, out var row))
        {
            return null;
        }

        var claimed = row with
        {
            State = RequestState.Claimed,
            ClaimantParticipantId = claimantParticipantId,
            ClaimantCallsign = claimantCallsign,
            Version = version,
        };

        // The claimant KEEPS its digit: done, start, release and copy all address a row by it.
        // Nobody else does, including the requester, who has nothing to do to it.
        if (!claimed.IsClaimedBy(ViewerParticipantId))
        {
            Allocator.Release(requestId, now);
            claimed = claimed.WithoutSlot();
        }

        // KEPT even for a viewer with no part in it. It draws no row, but IN PROGRESS counts rows
        // that are still here: removing it made the count decay towards zero while the work was
        // actually running, so a busy board read as an idle one.
        _rows[requestId] = claimed;
        Admit(now);
        return claimed.RendersInYours(ViewerParticipantId) ? claimed : null;
    }

    /// <summary>
    /// Moves a row along without changing who holds it: start, rounds away, adjust.
    /// </summary>
    /// <remarks>
    /// The VERSION is the point. Every transition is a conditional update on it, so a row whose
    /// local version lags the server's rejects every later command with a 409 the user never sees.
    /// </remarks>
    public BoardRow? ApplyProgress(Guid requestId, RequestState state, int version)
    {
        if (!_rows.TryGetValue(requestId, out var row))
        {
            return null;
        }

        var moved = row with { State = state, Version = version };
        _rows[requestId] = moved;
        return moved;
    }

    public bool Remove(Guid requestId, DateTimeOffset now)
    {
        if (!_rows.Remove(requestId))
        {
            return false;
        }

        Allocator.Release(requestId, now);
        _demoted.Remove(requestId);
        _passed.Remove(requestId);
        Admit(now);
        return true;
    }

    /// <summary>
    /// Hides one row on this board only. Client-side, no server state change, and it comes back on
    /// agent restart.
    /// </summary>
    public bool Pass(Guid requestId, DateTimeOffset now)
    {
        if (!_rows.TryGetValue(requestId, out var row))
        {
            return false;
        }

        _passed.Add(requestId);
        Allocator.Release(requestId, now);
        _rows[requestId] = row.WithoutSlot();
        Admit(now);
        return true;
    }

    /// <summary>
    /// Hides every request from one requester on this board for the rest of the match. Returns how
    /// many rows went. Client-side; it affects nobody else.
    /// </summary>
    public int MuteRequester(Guid participantId, DateTimeOffset now)
    {
        _mutedRequesters.Add(participantId);
        var hidden = 0;
        foreach (var row in _rows.Values.Where(r => r.RequestedByParticipantId == participantId).ToList())
        {
            Allocator.Release(row.Id, now);
            _rows[row.Id] = row.WithoutSlot() with { Muted = true };
            hidden++;
        }

        Admit(now);
        return hidden;
    }

    /// <summary>The <c>clear</c> verb un-mutes everyone. Passed rows stay passed.</summary>
    public void ClearMutes(DateTimeOffset now)
    {
        if (_mutedRequesters.Count == 0)
        {
            return;
        }

        _mutedRequesters.Clear();
        foreach (var row in _rows.Values.Where(r => r.Muted).ToList())
        {
            _rows[row.Id] = row with { Muted = false };
        }

        Admit(now);
    }

    /// <summary>
    /// Expires low-priority slot residency, then fills every freed digit from overflow. A demoted
    /// row stays open: only its claim on a digit expired.
    /// </summary>
    public BoardTick Tick(DateTimeOffset now)
    {
        // A row whose time ran out LEAVES, without waiting to be told. The tick used to handle only
        // low-priority demotion, so an open row that reached zero sat there at zero forever whenever
        // the expiry frame never arrived, while the web board showed it long gone. The overlay has
        // the expiry time in its hand; relying on a frame to act on it is trusting a message that
        // may not come.
        // EVERY row, not just the ones holding a digit. Rows iterates slot holders only, so an
        // overflow, demoted, passed or muted row past its expiry stayed forever: the overflow count
        // stayed inflated, and Admit could hand a bright digit to a request that had already
        // expired, which the server then refuses.
        var expired = new List<BoardRow>();
        foreach (var row in _rows.Values.Where(r => r.IsOpen && now >= r.ExpiresAt).ToList())
        {
            Remove(row.Id, now);
            expired.Add(row);
        }

        var demoted = new List<BoardRow>();
        foreach (var row in Rows)
        {
            if (row.Priority != Priority.Low || !row.IsOpen)
            {
                continue;
            }

            var since = Allocator.AcquiredAt(row.Id);
            if (since is null || now - since.Value < _lowPriorityResidency)
            {
                continue;
            }

            Allocator.Release(row.Id, now);
            _demoted.Add(row.Id);
            var dropped = row.WithoutSlot();
            _rows[row.Id] = dropped;
            demoted.Add(dropped);
        }

        var admitted = Admit(now);
        return demoted.Count == 0 && admitted.Count == 0 && expired.Count == 0
            ? BoardTick.Empty
            : new BoardTick(demoted, admitted, expired);
    }

    /// <summary>
    /// The deployment hop, in the order 10-agent-spec.md requires: abort the pending draft, drop
    /// every row, reset the allocator including its reissue order. The caller then revalidates over
    /// HTTPS with the id from the frame.
    /// </summary>
    public DeploymentChange EnterDeployment(Guid deploymentId, DateTimeOffset now, IDraftOwner? draft)
    {
        var aborted = draft?.AbortDraft(now) ?? false;
        var dropped = _rows.Count;

        _rows.Clear();
        _passed.Clear();
        _mutedRequesters.Clear();
        _demoted.Clear();
        Allocator.Reset();
        DeploymentId = deploymentId;

        return new DeploymentChange(deploymentId, aborted, dropped);
    }

    /// <summary>
    /// What <c>accept next</c> resolves to: highest priority, then lowest slot, skipping rows the
    /// viewer cannot service.
    /// </summary>
    public BoardRow? AcceptNext(Func<BoardRow, bool>? canService = null)
    {
        var claimable = Rows.Where(r => r.ClaimableByDigit && (canService?.Invoke(r) ?? true));
        return claimable
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Slot!.Value)
            .FirstOrDefault();
    }

    private List<BoardRow> Admit(DateTimeOffset now)
    {
        List<BoardRow>? admitted = null;
        while (!Allocator.IsFull)
        {
            var candidates = AdmissionCandidates;
            if (candidates.Count == 0)
            {
                break;
            }

            var row = Allocator.AdmitNext(candidates, now);
            if (row is null)
            {
                break;
            }

            _rows[row.Id] = row;
            (admitted ??= []).Add(row);
        }

        return admitted ?? [];
    }

    /// <summary>Overflow minus the rows whose residency expired. A demoted digit does not come back.</summary>
    private IReadOnlyList<BoardRow> AdmissionCandidates =>
        [.. Overflow.Where(r => !_demoted.Contains(r.Id))];

    private bool Visible(BoardRow row) =>
        !row.Muted && !_passed.Contains(row.Id) && !_mutedRequesters.Contains(row.RequestedByParticipantId);
}
