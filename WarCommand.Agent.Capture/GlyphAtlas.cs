using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace WarCommand.Agent.Capture;

/// <summary>One rendered glyph, normalised to a fixed cell so a match is scale free.</summary>
public sealed class GlyphTemplate
{
    internal GlyphTemplate(string glyph, float[] cell, double aspect)
    {
        Glyph = glyph;
        Cell = cell;
        Aspect = aspect;
    }

    /// <summary>
    /// Ink width over ink height, before normalisation. The cell is square, so without this a 4 px
    /// slice and a 7 px slice look identical and the solver happily splits one digit into two.
    /// </summary>
    public double Aspect { get; }

    /// <summary>The character this template stands for, from map_readout.glyphs.</summary>
    public string Glyph { get; }

    /// <summary>Coverage per cell, 0 to 1, row major over <see cref="GlyphAtlas.CellSize"/>.</summary>
    public float[] Cell { get; }
}

/// <summary>
/// The glyph set from map_readout.glyphs, rendered from a font and normalised into fixed cells.
/// Everything about it comes from the served profile: the glyph list, the candidate typefaces and
/// the margin floor. Nothing about Wardogs is written down here.
/// </summary>
/// <remarks>
/// Normalising every glyph into the same cell is what makes ui_scales cheap. A captured run is
/// scaled into the same cell before it is compared, so one atlas answers every scale and a new
/// scale in the profile costs nothing.
/// </remarks>
public sealed class GlyphAtlas
{
    /// <summary>Cell edge in samples. Big enough to keep 3 and 8 apart, small enough to stay fast.</summary>
    public const int CellSize = 16;

    private GlyphAtlas(string fontFamily, IReadOnlyList<GlyphTemplate> templates)
    {
        FontFamily = fontFamily;
        Templates = templates;
    }

    /// <summary>The typeface these templates were rendered from.</summary>
    public string FontFamily { get; }

    public IReadOnlyList<GlyphTemplate> Templates { get; }

    /// <summary>
    /// One atlas built from the game's OWN glyphs rather than a rendered approximation.
    /// </summary>
    /// <remarks>
    /// This is the atlas that should be used whenever the profile carries learned shapes. Matching
    /// a rendered system font against a face the game ships was the single largest source of wrong
    /// digits, and no amount of solver tuning could recover from it.
    /// </remarks>
    public static GlyphAtlas FromLearned(IReadOnlyDictionary<string, IReadOnlyList<string>> learned)
    {
        ArgumentNullException.ThrowIfNull(learned);

        var templates = new List<GlyphTemplate>(learned.Count);

        foreach (var (glyph, rows) in learned)
        {
            var height = rows.Count;
            var width = height == 0 ? 0 : rows.Max(r => r.Length);
            if (width == 0 || height == 0)
            {
                continue;
            }

            var ink = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < rows[y].Length; x++)
                {
                    ink[(y * width) + x] = rows[y][x] == '.' ? 0f : 1f;
                }
            }

            var bounds = InkBounds(ink, width, height);
            if (bounds is not { } b)
            {
                continue;
            }

            templates.Add(new GlyphTemplate(
                glyph,
                Resample(ink, width, b.Left, b.Top, b.Width, b.Height),
                b.Width / (double)b.Height));
        }

        return new GlyphAtlas("learned", templates);
    }

    /// <summary>Every typeface installed on this machine, for a calibration sweep.</summary>
    public static IReadOnlyList<string> InstalledFamilies()
    {
        using var installed = new InstalledFontCollection();
        return [.. installed.Families.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Renders one atlas per candidate face. A face the machine lacks is skipped.</summary>
    public static IReadOnlyList<GlyphAtlas> Render(
        IReadOnlyList<string> fontCandidates,
        IReadOnlyList<string> glyphs,
        bool bold)
    {
        ArgumentNullException.ThrowIfNull(fontCandidates);
        ArgumentNullException.ThrowIfNull(glyphs);

        var installed = new InstalledFontCollection();
        var available = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var atlases = new List<GlyphAtlas>();

        foreach (var family in fontCandidates)
        {
            if (!available.Contains(family))
            {
                continue;
            }

            var templates = glyphs
                .Select(g => RenderTemplate(family, g, bold))
                .ToList();

            atlases.Add(new GlyphAtlas(family, templates));
        }

        return atlases;
    }

    /// <summary>
    /// Cosine similarity against every template, weighted by how close the candidate's width is to
    /// the template's. Empty when the atlas is empty.
    /// </summary>
    /// <param name="aspect">Candidate ink width over ink height. Non-positive skips the weighting.</param>
    public (GlyphTemplate Template, double Score, double Margin)? Best(float[] cell, double aspect = 0)
    {
        ArgumentNullException.ThrowIfNull(cell);

        if (Templates.Count == 0)
        {
            return null;
        }

        GlyphTemplate? best = null;
        var bestScore = double.NegativeInfinity;
        var runnerUp = double.NegativeInfinity;

        foreach (var template in Templates)
        {
            var score = Similarity(cell, template.Cell) * AspectWeight(aspect, template.Aspect);
            if (score > bestScore)
            {
                runnerUp = bestScore;
                bestScore = score;
                best = template;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        // The margin the profile's floor is compared against: how much better the winner is than
        // the next best. A high score with no margin is 3 against 8, which is the failure to catch.
        var margin = double.IsNegativeInfinity(runnerUp) ? 1.0 : Math.Max(0, bestScore - runnerUp);
        return (best, bestScore, margin);
    }

    /// <summary>
    /// How much a width mismatch costs. A glyph twice as wide as the template scores near nothing,
    /// which is what stops one wide digit being read as two narrow ones.
    /// </summary>
    private static double AspectWeight(double candidate, double template)
    {
        if (candidate <= 0 || template <= 0)
        {
            return 1;
        }

        var ratio = candidate > template ? candidate / template : template / candidate;
        return 1.0 / (1.0 + (2.5 * (ratio - 1.0) * (ratio - 1.0)));
    }

    // Rendered large, then reduced to the cell, so the shape survives rather than the pixel grid.
    private static GlyphTemplate RenderTemplate(string family, string glyph, bool bold)
    {
        const int box = 96;

        using var bitmap = new Bitmap(box, box, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var font = new Font(family, box * 0.6f, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat(StringFormat.GenericTypographic);

            graphics.DrawString(glyph, font, brush, new PointF(box * 0.1f, box * 0.1f), format);
        }

        var (ink, width, height) = Coverage(bitmap);
        var bounds = InkBounds(ink, width, height);

        return bounds is not { } b
            ? new GlyphTemplate(glyph, new float[CellSize * CellSize], 1)
            : new GlyphTemplate(
                glyph,
                Resample(ink, width, b.Left, b.Top, b.Width, b.Height),
                b.Width / (double)b.Height);
    }

    /// <summary>
    /// Reads the rendered glyph out in one locked copy.
    /// </summary>
    /// <remarks>
    /// GetPixel per pixel is roughly 700,000 marshalled calls to build the atlas: seconds of work,
    /// on whatever thread first asks for a coordinate. LockBits makes it one copy per glyph.
    /// </remarks>
    private static (float[] Ink, int Width, int Height) Coverage(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var ink = new float[width * height];

        var locked = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var stride = locked.Stride;
            var bytes = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    // BGRA, and the glyph is drawn white on black, so any channel is the coverage.
                    ink[(y * width) + x] = bytes[row + (x * 4) + 2] / 255f;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return (ink, width, height);
    }

    /// <summary>The tight box around the ink, or null when the glyph rendered blank.</summary>
    internal static (int Left, int Top, int Width, int Height)? InkBounds(float[] ink, int width, int height)
    {
        var left = width;
        var right = -1;
        var top = height;
        var bottom = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (ink[(y * width) + x] <= 0.35f)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? null
            : (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>Box filter from an arbitrary sub-rectangle into the fixed cell.</summary>
    internal static float[] Resample(float[] ink, int stride, int left, int top, int width, int height)
    {
        var cell = new float[CellSize * CellSize];

        for (var cy = 0; cy < CellSize; cy++)
        {
            var y0 = top + (cy * height / CellSize);
            var y1 = Math.Max(y0 + 1, top + ((cy + 1) * height / CellSize));

            for (var cx = 0; cx < CellSize; cx++)
            {
                var x0 = left + (cx * width / CellSize);
                var x1 = Math.Max(x0 + 1, left + ((cx + 1) * width / CellSize));

                var sum = 0f;
                var count = 0;

                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        sum += ink[(y * stride) + x];
                        count++;
                    }
                }

                cell[(cy * CellSize) + cx] = count == 0 ? 0f : sum / count;
            }
        }

        return cell;
    }

    private static double Similarity(float[] a, float[] b)
    {
        double dot = 0;
        double na = 0;
        double nb = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
