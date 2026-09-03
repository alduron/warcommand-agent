namespace WarCommand.Agent.Capture;

/// <summary>A cluster of near-white pixels: a candidate for the readout text.</summary>
public sealed record TextBlob(int Left, int Top, int Right, int Bottom, int PixelCount)
{
    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;

    /// <summary>Ink over area. Real text sits well below a solid block and well above a stray line.</summary>
    public double Density => PixelCount / (double)Math.Max(1, Width * Height);
}

/// <summary>
/// Finds near-white text without knowing where it is. Binding rule 5 forbids a fixed rectangle for
/// the map readout, because it is anchored to the moving crosshair, so the panel is scanned instead.
/// </summary>
/// <remarks>
/// This is deliberately not OCR. It answers the question that blocks everything else: can we see
/// the readout at all, and how many candidates does a frame hold. The glyph atlas comes after a
/// real build says yes.
/// </remarks>
public static class NearWhiteScanner
{
    /// <summary>
    /// The rows of a blob the text occupies: every row carrying a meaningful share of the densest.
    /// </summary>
    /// <remarks>
    /// Shared with the reader so a learned glyph is cut from exactly the band the decoder will
    /// later match against. Two definitions of the text band would learn shapes nothing matches.
    /// </remarks>
    public static (int Top, int Bottom) BandOf(Frame frame, TextBlob blob, int threshold)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(blob);

        var perRow = new int[blob.Height];
        var densest = 0;

        for (var y = 0; y < blob.Height; y++)
        {
            for (var x = 0; x < blob.Width; x++)
            {
                if (frame.IsNearWhite(blob.Left + x, blob.Top + y, threshold))
                {
                    perRow[y]++;
                }
            }

            densest = Math.Max(densest, perRow[y]);
        }

        if (densest == 0)
        {
            return (blob.Top, blob.Bottom);
        }

        var floor = Math.Max(2, (int)(densest * 0.35));
        var top = -1;
        var bottom = -1;

        for (var y = 0; y < blob.Height; y++)
        {
            if (perRow[y] < floor)
            {
                continue;
            }

            if (top < 0)
            {
                top = y;
            }

            bottom = y;
        }

        return top < 0 ? (blob.Top, blob.Bottom) : (blob.Top + top, blob.Top + bottom);
    }

    /// <summary>
    /// Groups near-white pixels into blobs with a single-pass union of runs, then merges blobs whose
    /// boxes are close enough to be neighbouring glyphs on one line.
    /// </summary>
    public static IReadOnlyList<TextBlob> Scan(
        Frame frame,
        int threshold,
        int minHeight = 6,
        int maxHeight = 48,
        int glyphGap = 6)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var open = new List<Blob>();
        var closed = new List<Blob>();

        for (var y = 0; y < frame.Height; y++)
        {
            var runs = RunsIn(frame, y, threshold);

            foreach (var blob in open)
            {
                blob.TouchedThisRow = false;
            }

            foreach (var (left, right, count) in runs)
            {
                Blob? host = null;
                foreach (var blob in open)
                {
                    if (blob.Bottom >= y - 1 && blob.Left <= right + 1 && blob.Right >= left - 1)
                    {
                        host = blob;
                        break;
                    }
                }

                if (host is null)
                {
                    host = new Blob { Left = left, Right = right, Top = y, Bottom = y };
                    open.Add(host);
                }

                host.Left = Math.Min(host.Left, left);
                host.Right = Math.Max(host.Right, right);
                host.Bottom = y;
                host.PixelCount += count;
                host.TouchedThisRow = true;
            }

            for (var i = open.Count - 1; i >= 0; i--)
            {
                if (!open[i].TouchedThisRow && open[i].Bottom < y - 1)
                {
                    closed.Add(open[i]);
                    open.RemoveAt(i);
                }
            }
        }

        closed.AddRange(open);

        var glyphs = closed
            .Where(b => b.Height >= minHeight && b.Height <= maxHeight)
            .OrderBy(b => b.Top)
            .ThenBy(b => b.Left)
            .ToList();

        return MergeIntoLines(glyphs, glyphGap);
    }

    private static List<(int Left, int Right, int Count)> RunsIn(Frame frame, int y, int threshold)
    {
        var runs = new List<(int, int, int)>();
        var start = -1;

        for (var x = 0; x < frame.Width; x++)
        {
            if (frame.IsNearWhite(x, y, threshold))
            {
                if (start < 0)
                {
                    start = x;
                }
            }
            else if (start >= 0)
            {
                runs.Add((start, x - 1, x - start));
                start = -1;
            }
        }

        if (start >= 0)
        {
            runs.Add((start, frame.Width - 1, frame.Width - start));
        }

        return runs;
    }

    // Neighbouring glyphs on one line become one blob, which is what a pattern would be matched
    // against. Vertical overlap plus a small horizontal gap is the whole test.
    private static List<TextBlob> MergeIntoLines(List<Blob> glyphs, int glyphGap)
    {
        var lines = new List<Blob>();

        foreach (var glyph in glyphs)
        {
            var merged = false;

            foreach (var line in lines)
            {
                var overlaps = glyph.Top <= line.Bottom && glyph.Bottom >= line.Top;
                var near = glyph.Left - line.Right <= glyphGap && line.Left - glyph.Right <= glyphGap;

                if (overlaps && near)
                {
                    line.Left = Math.Min(line.Left, glyph.Left);
                    line.Right = Math.Max(line.Right, glyph.Right);
                    line.Top = Math.Min(line.Top, glyph.Top);
                    line.Bottom = Math.Max(line.Bottom, glyph.Bottom);
                    line.PixelCount += glyph.PixelCount;
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                lines.Add(new Blob
                {
                    Left = glyph.Left,
                    Right = glyph.Right,
                    Top = glyph.Top,
                    Bottom = glyph.Bottom,
                    PixelCount = glyph.PixelCount,
                });
            }
        }

        return [.. lines
            .Select(b => new TextBlob(b.Left, b.Top, b.Right, b.Bottom, b.PixelCount))
            .OrderByDescending(b => b.PixelCount)];
    }

    private sealed class Blob
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
        public int PixelCount;
        public bool TouchedThisRow;

        public int Height => Bottom - Top + 1;
    }
}
