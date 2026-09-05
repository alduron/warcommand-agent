using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Capture;

/// <summary>One ladder rung's decode of ONE HALF of a readout. The unit the rungs vote with.</summary>
internal readonly record struct Vote(string Text, decimal Margin);

/// <summary>
/// Reads the coordinate under the crosshair off the screen. One
/// <see cref="ICoordinateSource"/> among several, never the mechanism.
/// </summary>
/// <remarks>
/// It answers null far more often than it answers a point, and that is the design. The readout sits
/// wherever the player put it, so a busy background can weld a decimal point to the digit beside it
/// and no threshold separates them. Rather than guess, this returns nothing and the surface asks for
/// another press somewhere clearer. A wrong coordinate is a fire mission on the wrong grid; a
/// refused one costs a second.
/// </remarks>
public sealed class MapReadoutCoordinateSource : ICoordinateSource
{
    private readonly Func<GameProfile> _profile;
    private readonly Func<nint?> _gameWindow;
    private readonly Func<bool> _enabled;
    private readonly Func<decimal?> _mapBounds;

    private ReadoutReader? _reader;
    private string _readerFor = string.Empty;
    private bool _suspended;

    /// <param name="profile">The live profile. Re-read every call so a served change takes effect.</param>
    /// <param name="gameWindow">The game's window handle, or null when it is not running.</param>
    /// <param name="enabled">Screen capture is opt-in and off by default.</param>
    /// <param name="mapBounds">The loaded map's coord_max when known, for the sanity gate.</param>
    public MapReadoutCoordinateSource(
        Func<GameProfile> profile,
        Func<nint?> gameWindow,
        Func<bool> enabled,
        Func<decimal?>? mapBounds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameWindow);
        ArgumentNullException.ThrowIfNull(enabled);

        _profile = profile;
        _gameWindow = gameWindow;
        _enabled = enabled;
        _mapBounds = mapBounds ?? (() => null);
    }

    /// <inheritdoc />
    public string Id => "map_readout";

    /// <inheritdoc />
    public int Priority { get; init; }

    /// <inheritdoc />
    public bool IsAvailable => !_suspended && _enabled() && _gameWindow() is not null;

    /// <summary>
    /// Panic: no frame is grabbed until Resume. Binding rule 7 - one press stops every capture.
    /// </summary>
    /// <remarks>
    /// Registered as the ScreenCapture subsystem. It was a no-op placeholder long after this class
    /// was built, so Panic left the only capture path in the product running.
    /// </remarks>
    public void Suspend()
    {
        _suspended = true;
        LastRefusal = "SUSPENDED";
    }

    /// <summary>Capture is live again.</summary>
    public void Resume() => _suspended = false;

    /// <summary>
    /// Builds the atlas ahead of the first read, off whatever thread the caller is on.
    /// </summary>
    /// <remarks>
    /// Rendering thirteen glyphs across six faces is real work. Doing it lazily means the first key
    /// press that asks for a coordinate pays for it, which reads as the feature hanging.
    /// </remarks>
    public void Warm() => _ = ReaderFor(_profile().MapReadout);

    /// <summary>Why the last read produced nothing, for the surface to render. Never a coordinate.</summary>
    public string? LastRefusal { get; private set; }

    /// <summary>What each axis was read as, and how many times. For the probe, never for a surface.</summary>
    internal string? LastVoteSummary { get; private set; }

    /// <inheritdoc />
    public Task<MapPoint?> TryReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    /// <summary>
    /// Reads the map, and only answers when several frames agree EXACTLY.
    /// </summary>
    /// <remarks>
    /// One frame is not evidence. A decode can be plausible and wrong: it satisfies the grammar,
    /// sits inside the map bounds and parses cleanly, and forwarding it puts a fire mission on the
    /// wrong grid. map_readout.corroboration_frames samples are taken across
    /// corroboration_window_ms and every one must produce the same string. A readout the user is
    /// hovering does not change in 60ms, so disagreement means the decode is unstable, not that the
    /// coordinate moved.
    /// </remarks>
    public MapPoint? Read()
    {
        if (_suspended)
        {
            LastRefusal = "SUSPENDED";
            return null;
        }

        var readout = _profile().MapReadout;
        var frames = Math.Max(1, readout.CorroborationFrames);
        var gap = Math.Max(0, readout.CorroborationWindowMs / frames);

        MapPoint? agreed = null;

        for (var i = 0; i < frames; i++)
        {
            if (i > 0 && gap > 0)
            {
                Thread.Sleep(gap);
            }

            var sample = ReadOnce();
            if (sample is null)
            {
                return null;
            }

            if (agreed is null)
            {
                agreed = sample;
                continue;
            }

            if (!string.Equals(agreed.RawText, sample.RawText, StringComparison.Ordinal))
            {
                // Two samples of a stationary readout disagreed, so at least one is wrong and
                // there is no way to tell which. Refuse and let the user read again.
                LastRefusal = "UNSTABLE READ";
                return null;
            }
        }

        return agreed;
    }

    /// <summary>One frame, no corroboration. The probe's read, so it exercises this path exactly.</summary>
    internal MapPoint? ReadOnceForProbe() => ReadOnce();

    private MapPoint? ReadOnce()
    {
        LastRefusal = null;

        if (!_enabled())
        {
            LastRefusal = "CAPTURE OFF";
            return null;
        }

        if (_gameWindow() is not { } hwnd)
        {
            LastRefusal = "NO GAME WINDOW";
            return null;
        }

        var profile = _profile();
        var readout = profile.MapReadout;

        // The cursor FIRST: reading it after the grab smears the crosshair offset by however far
        // the mouse travelled during the copy.
        var cursor = GameWindow.CursorInClient(hwnd);

        // Captured AROUND THE CURSOR, never around the screen. The readout is anchored to the
        // moving crosshair, so the window that looks for it has to move with the crosshair too. A
        // fixed centre panel clipped it the moment the cursor neared the edge of the map, and the
        // failure looked like a decode problem when nothing had been captured at all.
        var client = GameWindow.ClientRectOnScreen(hwnd);
        var panel = Around(client, cursor, readout.SearchRadiusPx);
        var frame = DesktopFrameGrabber.Grab(panel);

        if (frame is null)
        {
            LastRefusal = "CAPTURE FAILED";
            return null;
        }

        // Blob coordinates are window-relative, so the cursor has to be too.
        if (cursor is { } c)
        {
            cursor = (c.X - (panel.Left - client.Left), c.Y - (panel.Top - client.Top));
        }

        var reader = ReaderFor(readout);

        // Down the ladder until a complete pair decodes. The readout dims towards the edges of the
        // map, so a single threshold reads the middle and goes blind at the border on text that is
        // perfectly legible; the black outline round the glyphs is what makes a lower one safe.
        var fixedRungs = readout.NearWhiteLadder.Count > 0
            ? readout.NearWhiteLadder
            : [readout.NearWhiteThreshold];

        var ladder = Rungs(fixedRungs, readout, frame, cursor);

        // EVERY rung is read, and the two halves vote SEPARATELY. Two things were wrong with
        // stopping at the first rung that produced a pair. A dim readout, where the top rung erodes
        // a glyph into a different but still legal one, was answered by the eroded reading before
        // the rungs that can see the text were tried; that reading is stable, so every re-read
        // returned the same wrong number and looked exactly like a cache. And the halves sit apart
        // on a map that GRADES, so they are often not equally bright: MEASURED on a live client,
        // x99.90 decoded only at 150 and y107.61 only at 179 to 195, so demanding both from one
        // rung refused a reading in which each half was clean at its own.
        //
        // Nothing here holds a coordinate or a frame between calls.
        var xVotes = new List<Vote>();
        var yVotes = new List<Vote>();

        // Each candidate run is decoded at ITS OWN ink as well as at the rung that found it. The
        // readout greys out by distance to the edge of the map, so the two halves of one reading
        // are at different points on that gradient and a threshold measured across the whole panel
        // is still a guess for either of them. A run's own peak is not a guess.
        //
        // The same box turns up at rung after rung, so the work is keyed by box and threshold and
        // each pair is solved once.
        var work = new Dictionary<(int Left, int Top, int Right, int Bottom, int Threshold), TextBlob>();

        foreach (var threshold in ladder)
        {
            var candidates = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx);
            if (cursor is { } near)
            {
                // Nearest first, and only a handful of them. A rung low enough to flood the panel
                // returns terrain as well as text, and every extra blob is a full solve against
                // the atlas: without this cap the cheapest rung on the ladder is also the slowest,
                // on a path that runs corroboration_frames times per key press.
                candidates = [.. candidates
                    .Where(b => Near(b, near.X, near.Y, readout))
                    .OrderBy(b => Distance(b, near.X, near.Y))
                    .Take(Math.Max(2, readout.ExpectedMatchesPerFrame * 6))];
            }

            foreach (var blob in candidates)
            {
                foreach (var t in ThresholdsFor(frame, blob, readout, threshold))
                {
                    work[(blob.Left, blob.Top, blob.Right, blob.Bottom, t)] = blob;
                }
            }
        }

        foreach (var ((_, _, _, _, threshold), blob) in work)
        {
            // The threshold goes through with the blob. Finding a run at one value and then reading
            // its ink at another is how the ladder came to be decoration.
            foreach (var run in reader.Read(frame, [blob], threshold))
            {
                var vote = new Vote(run.Text, (decimal)run.WorstMargin);
                if (run.Text.StartsWith('x'))
                {
                    xVotes.Add(vote);
                }
                else
                {
                    yVotes.Add(vote);
                }
            }
        }

        if (xVotes.Count == 0 || yVotes.Count == 0)
        {
            // Both halves or nothing, at every threshold on the ladder. One axis is not a
            // coordinate, and a half read that silently kept the previous value would be the worst
            // outcome available.
            LastRefusal = "NO COORDS";
            return null;
        }

        LastVoteSummary = Summarise("x", xVotes) + "  |  " + Summarise("y", yVotes);

        if (Consensus(xVotes) is not { } xAgreed || Consensus(yVotes) is not { } yAgreed)
        {
            LastRefusal = "AMBIGUOUS READ";
            return null;
        }

        var margin = Math.Min(xAgreed.Vote.Margin, yAgreed.Vote.Margin);
        var agreeing = Math.Min(xAgreed.Agreeing, yAgreed.Agreeing);
        var opposed = xAgreed.Opposed || yAgreed.Opposed;
        if (reader.PointFrom(xAgreed.Vote.Text, yAgreed.Vote.Text, margin, _mapBounds()) is not { } point)
        {
            LastRefusal = "NO COORDS";
            return null;
        }

        // Agreement across rungs is the evidence, so it carries the confidence, and the weaker half
        // sets it. One rung agreeing with nothing is the dim reading only the bottom of the ladder
        // can see: still answered, because refusing there is the blindness the ladder exists to
        // fix, but marked as the weaker reading it is.
        return new MapPoint(
            point.X,
            point.Y,
            Id,
            point.RawText,
            Confidence(margin, agreeing, opposed));
    }

    /// <summary>One axis' readings and their counts, best first.</summary>
    private static string Summarise(string axis, List<Vote> votes) =>
        votes.Count == 0
            ? axis + ": none"
            : axis + ": " + string.Join(", ", votes
                .GroupBy(v => v.Text, StringComparer.Ordinal)
                .Select(g => (Text: g.Key, Count: g.Count(), Best: g.Max(v => v.Margin)))
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.Best)
                .Select(g => FormattableString.Invariant($"{g.Text} x{g.Count} m{g.Best:F2}")));

    /// <summary>
    /// What to read one candidate run at: the rung that found it, plus fractions of that run's OWN
    /// brightest ink.
    /// </summary>
    /// <remarks>
    /// MEASURED on a live client at the bottom right corner of the map: the readout greys out by
    /// distance to the edge, continuously, so no single value serves the whole panel and the two
    /// halves of one reading are not equally bright. A run's own peak is the one number that is
    /// about that run.
    /// </remarks>
    private static IEnumerable<int> ThresholdsFor(
        Frame frame,
        TextBlob blob,
        MapReadoutSection readout,
        int rung)
    {
        yield return rung;

        if (readout.NearWhiteRelativeRatios.Count == 0)
        {
            yield break;
        }

        var peak = frame.PeakNearWhite(blob.Left, blob.Top, blob.Width, blob.Height);

        foreach (var ratio in readout.NearWhiteRelativeRatios)
        {
            var derived = (int)Math.Round(peak * ratio, MidpointRounding.AwayFromZero);
            if (derived >= readout.NearWhiteFloor && derived <= 255 && derived != rung)
            {
                yield return derived;
            }
        }
    }

    /// <summary>
    /// The fixed rungs plus the ones derived from this frame's own brightness, high to low.
    /// </summary>
    /// <remarks>
    /// A fixed ladder guesses where the text sits on the map's gradient. A derived rung measures
    /// it: the readout's core is the brightest neutral thing beside the crosshair, so a fraction of
    /// that peak lands on the glyph body wherever the gradient has dimmed it to. The peak is taken
    /// from a box around the CURSOR rather than the whole panel, because the panel is wide enough
    /// to hold a pure white HUD element that would drag every derived rung back up to the fixed
    /// ones and buy nothing.
    /// </remarks>
    internal static IReadOnlyList<int> Rungs(
        IReadOnlyList<int> fixedRungs,
        MapReadoutSection readout,
        Frame frame,
        (int X, int Y)? cursor)
    {
        var rungs = new List<int>(fixedRungs);

        if (readout.NearWhiteRelativeRatios.Count > 0 && cursor is { } at)
        {
            var reach = Math.Max(1, readout.SearchRadiusPx / 2);
            var peak = frame.PeakNearWhite(at.X - reach, at.Y - reach, reach * 2, reach * 2);

            foreach (var ratio in readout.NearWhiteRelativeRatios)
            {
                var rung = (int)Math.Round(peak * ratio, MidpointRounding.AwayFromZero);
                if (rung >= readout.NearWhiteFloor && rung <= 255 && !rungs.Contains(rung))
                {
                    rungs.Add(rung);
                }
            }
        }

        rungs.Sort();
        rungs.Reverse();
        return rungs;
    }

    /// <summary>
    /// The reading of one axis that the most ladder rungs agree on, or null when two readings tie.
    /// </summary>
    /// <remarks>
    /// The rungs are evidence, not a search that stops at the first hit. A tie is the case worth
    /// refusing: two rungs read the same pixels as two different coordinates and nothing in the
    /// frame says which is right, so answering either one is a coin toss on somebody's grid.
    /// </remarks>
    internal static (Vote Vote, int Agreeing, bool Opposed)? Consensus(IReadOnlyList<Vote> votes)
    {
        ArgumentNullException.ThrowIfNull(votes);

        var tally = votes
            .GroupBy(v => v.Text, StringComparer.Ordinal)
            .Select(g => (Text: g.Key, Count: g.Count(), Best: g.Max(v => v.Margin)))
            .OrderByDescending(g => g.Best)
            .ThenByDescending(g => g.Count)
            .ToList();

        if (tally.Count == 0)
        {
            return null;
        }

        // MARGIN FIRST, count second. Counting rungs looks like corroboration and is not: the extra
        // votes come from thresholds that thicken or erode the strokes, and a thickened 6 closes
        // into an 8, so the distorted reading is produced by MORE rungs than the true one. MEASURED
        // twice against a readout the owner then read off the screen: x96.60 at margin 0.14 lost to
        // x96.80 at margin 0.12 on a count of 2 to 1, and 96.60 was what the screen said. The
        // margin is the glyphs' own evidence and it was right both times.
        //
        // Only a tie on BOTH is a coin toss worth refusing.
        if (tally.Count > 1 && tally[0].Best == tally[1].Best && tally[0].Count == tally[1].Count)
        {
            return null;
        }

        var winner = votes
            .Where(v => string.Equals(v.Text, tally[0].Text, StringComparison.Ordinal))
            .OrderByDescending(v => v.Margin)
            .First();

        return (winner, tally[0].Count, tally.Count > 1);
    }

    /// <summary>
    /// A confidence for request_points.confidence, TWO decimal places, derived from agreement.
    /// </summary>
    /// <remarks>
    /// The raw glyph margin is not a confidence and must never be sent as one. Two things were
    /// wrong with doing that: the API's Confidence type takes two decimal places and a margin like
    /// 0.008 is three, so every screen-read submit failed validation outright; and a correct decode
    /// of this font scores well under point_confidence.floor, so a good reading would have been
    /// treated as a poor one on every board that rendered it.
    /// <para>
    /// What this number means: how much of the ladder agreed, whether anything disagreed, and how
    /// far the winning glyphs beat their runners up. A CONTESTED axis is the case worth marking
    /// down, and it used to be marked up: an x half split between x96.85 and x96.65 reported 1.00,
    /// because only the winner's margin reached this. Nothing read off a screen reaches 1.00 now.
    /// </para>
    /// </remarks>
    private static decimal Confidence(decimal margin, int agreeing, bool opposed)
    {
        var seat = opposed ? 0.60m : agreeing > 1 ? 0.75m : 0.65m;
        return Math.Round(Math.Min(0.95m, seat + Math.Min(margin, 0.20m)), 2);
    }

    /// <summary>The square of the given radius around the cursor, clipped to the client rect.</summary>
    private static CaptureArea Around(CaptureArea client, (int X, int Y)? cursor, int radius)
    {
        if (client.IsEmpty || cursor is not { } at || radius <= 0)
        {
            return client;
        }

        var left = Math.Max(0, at.X - radius);
        var top = Math.Max(0, at.Y - radius);
        var right = Math.Min(client.Width, at.X + radius);
        var bottom = Math.Min(client.Height, at.Y + radius);

        return right <= left || bottom <= top
            ? client
            : new CaptureArea(client.Left + left, client.Top + top, right - left, bottom - top);
    }

    private ReadoutReader ReaderFor(MapReadoutSection readout)
    {
        // The atlas is expensive to render and the profile rarely changes, so it is rebuilt only
        // when the fields it is built from actually move.
        var key = string.Join('|', readout.Atlas.FontCandidates)
            + readout.Atlas.FontBold
            + string.Concat(readout.Glyphs)
            + readout.AnchoredPattern;

        if (_reader is not null && string.Equals(key, _readerFor, StringComparison.Ordinal))
        {
            return _reader;
        }

        _reader = new ReadoutReader(readout);
        _readerFor = key;
        return _reader;
    }

    private static bool Near(TextBlob blob, int x, int y, MapReadoutSection readout) =>
        Distance(blob, x, y) <= readout.SearchRadiusPx;

    private static double Distance(TextBlob blob, int x, int y)
    {
        var cx = blob.Left + (blob.Width / 2.0);
        var cy = blob.Top + (blob.Height / 2.0);
        return Math.Sqrt(((cx - x) * (cx - x)) + ((cy - y) * (cy - y)));
    }
}
