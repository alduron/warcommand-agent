using WarCommand.Agent.Core.Abstractions;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Capture;

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
    public bool IsAvailable => _enabled() && _gameWindow() is not null;

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
        var ladder = readout.NearWhiteLadder.Count > 0
            ? readout.NearWhiteLadder
            : [readout.NearWhiteThreshold];

        foreach (var threshold in ladder)
        {
            var candidates = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx);
            if (cursor is { } near)
            {
                candidates = [.. candidates
                    .Where(b => Near(b, near.X, near.Y, readout))
                    .OrderBy(b => Distance(b, near.X, near.Y))];
            }

            if (reader.ReadPoint(frame, candidates, _mapBounds()) is { } found)
            {
                return new MapPoint(found.X, found.Y, Id, found.RawText, Confidence(found.Confidence));
            }
        }

        // Both halves or nothing, at every threshold on the ladder. One axis is not a coordinate,
        // and a half read that silently kept the previous value would be the worst outcome
        // available.
        LastRefusal = "NO COORDS";
        return null;
    }

    /// <summary>
    /// A confidence for request_points.confidence, TWO decimal places, derived from agreement.
    /// </summary>
    /// <remarks>
    /// The raw glyph margin is not a confidence and must never be sent as one. Two things were
    /// wrong with doing that: the API's Confidence type takes two decimal places and a margin like
    /// 0.008 is three, so every screen-read submit failed validation outright; and a correct decode
    /// of this font scores 0.01 to 0.07, which sits far under point_confidence.floor of 0.55, so a
    /// good reading would have been treated as a poor one on every board that rendered it.
    /// <para>
    /// What this number actually means: several frames of a stationary readout decoded to the same
    /// string, it satisfied the grammar, and it fell inside the map's bounds. The margin only
    /// nudges it. It is a statement about agreement, not a probability.
    /// </para>
    /// </remarks>
    private static decimal Confidence(decimal margin) =>
        Math.Round(Math.Clamp(0.75m + (margin * 3m), 0m, 1m), 2);

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
