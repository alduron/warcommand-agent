using System.Text;
using System.Text.RegularExpressions;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Capture;

/// <summary>One run of text read off a frame, with the weakest glyph margin that produced it.</summary>
public sealed record ReadoutRun(
    string Text,
    double WorstMargin,
    TextBlob Blob,
    string FontFamily,
    double Score = 0);

/// <summary>
/// Turns near-white blobs into strings, and strings into a coordinate. The glyph set, the pattern,
/// the threshold and the margin floor all come from map_readout in the served profile.
/// </summary>
/// <remarks>
/// A glyph below the profile's margin floor rejects the WHOLE run rather than that character: a
/// readout with one uncertain digit is a wrong coordinate, not a partial one.
/// </remarks>
public sealed class ReadoutReader
{
    private readonly MapReadoutSection _readout;
    private readonly IReadOnlyList<GlyphAtlas> _atlases;
    private readonly Regex _anchored;
    private readonly double? _costOverride;
    private decimal? _floorOverride;
    private decimal? _boundsMax;

    /// <param name="readout">The served map_readout section: glyphs, pattern, threshold, floor.</param>
    /// <param name="fontOverride">Faces to use instead of the profile's, for a calibration sweep.</param>
    /// <param name="costOverride">Solver cost to use instead of the profile's, for tuning sweeps.</param>
    public ReadoutReader(
        MapReadoutSection readout,
        IReadOnlyList<string>? fontOverride = null,
        double? costOverride = null)
    {
        _costOverride = costOverride;

        ArgumentNullException.ThrowIfNull(readout);

        _readout = readout;
        _anchored = new Regex(readout.AnchoredPattern, RegexOptions.CultureInvariant);
        // Learned shapes win outright. They are the game's own glyphs; a rendered system font is
        // an approximation that reads a 4 as a 1 however the solver is tuned.
        _atlases = fontOverride is null && readout.Atlas.Learned.Count > 0
            ? [GlyphAtlas.FromLearned(readout.Atlas.Learned)]
            : GlyphAtlas.Render(
                fontOverride ?? readout.Atlas.FontCandidates,
                readout.Glyphs,
                readout.Atlas.FontBold);
    }

    /// <summary>The faces that were available to render. Empty means no candidate is installed.</summary>
    public IReadOnlyList<string> Fonts => [.. _atlases.Select(a => a.FontFamily)];

    /// <summary>
    /// Every blob that reads as a complete readout token, best margin first. A blob whose text does
    /// not match the anchored pattern is dropped, which is what keeps a HUD label out of a coordinate.
    /// </summary>
    public IReadOnlyList<ReadoutRun> Read(Frame frame, IReadOnlyList<TextBlob> blobs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(blobs);

        var runs = new List<ReadoutRun>();

        foreach (var blob in blobs)
        {
            foreach (var atlas in _atlases)
            {
                var run = ReadBlob(frame, blob, atlas);
                if (run is null || !_anchored.IsMatch(run.Text))
                {
                    continue;
                }

                runs.Add(run);
                break;
            }
        }

        return [.. runs.OrderByDescending(r => r.WorstMargin)];
    }

    /// <summary>
    /// The x and y halves assembled into a point, or null when the frame does not hold exactly one
    /// clean pair. Never a partial answer: one axis is not a coordinate.
    /// </summary>
    /// <param name="boundsMax">The loaded map's coord_max when known. Tighter than the envelope.</param>
    public (decimal X, decimal Y, string RawText, decimal Confidence)? ReadPoint(
        Frame frame,
        IReadOnlyList<TextBlob> blobs,
        decimal? boundsMax = null)
    {
        _boundsMax = boundsMax;

        var runs = Read(frame, blobs);

        var x = runs.FirstOrDefault(r => r.Text.StartsWith('x'));
        var y = runs.FirstOrDefault(r => r.Text.StartsWith('y'));

        if (x is null || y is null)
        {
            return null;
        }

        if (!decimal.TryParse(x.Text[1..], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var xValue)
            || !decimal.TryParse(y.Text[1..], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var yValue))
        {
            return null;
        }

        // A misread turns 108.62 into 10828.62: it satisfies the pattern, parses cleanly, and would
        // be submitted as a real point. The map is 160 units across, so the number is its own
        // evidence. This is the last gate before a wrong coordinate becomes somebody's fire mission.
        var limit = _boundsMax ?? _readout.CoordinateSanityMax;
        if (xValue < 0 || yValue < 0 || xValue > limit || yValue > limit)
        {
            return null;
        }

        var worst = (decimal)Math.Min(x.WorstMargin, y.WorstMargin);
        return (xValue, yValue, $"{x.Text} {y.Text}", worst);
    }

    /// <summary>
    /// What the decoder saw, with the floor and the pattern both ignored. Diagnostic only: it says
    /// how the run was segmented and what each glyph scored, which is the difference between a
    /// wrong font, a bad split and a floor set too high.
    /// </summary>
    public string Explain(Frame frame, TextBlob blob)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(blob);

        var band = TextBand(frame, blob);

        var report = new StringBuilder();
        report.Append(FormattableString.Invariant(
            $"{blob.Width}x{blob.Height} band {band.Bottom - band.Top + 1}: "));

        foreach (var atlas in _atlases)
        {
            var run = ReadBlobIgnoringFloor(frame, blob, atlas);
            report.Append(run is null
                ? FormattableString.Invariant($"[{atlas.FontFamily}] no legal split  ")
                : FormattableString.Invariant($"[{atlas.FontFamily}] '{run.Text}' margin {run.WorstMargin:F2}  "));
        }

        return report.ToString();
    }

    /// <summary>
    /// Decodes a run by solving the split and the readout's shape together.
    /// </summary>
    /// <remarks>
    /// Cutting at empty columns does not work: at 15 px the glyphs touch, so a six character run
    /// like x95.06 split into three lumps and everything after the first was noise. Instead this
    /// walks the shape the readout is KNOWN to have, an axis letter then digits then a point then
    /// two digits, and for each step tries every plausible glyph width, keeping the split that
    /// scores best overall. The grammar does most of the work: a width that cannot lead to a legal
    /// readout is never considered, so the decoder cannot invent a string the pattern would reject.
    /// </remarks>
    /// <summary>
    /// Decodes a run by trying each plausible CHARACTER COUNT and keeping the best average match.
    /// </summary>
    /// <remarks>
    /// A readout is set in one size, so its glyphs advance by a near constant pitch. Letting the
    /// solver choose widths freely meant a 7 character run could be tiled as 9 narrow ones that
    /// scored slightly higher in total, which is how y108.62 became y10828.62 while the run beside
    /// it decoded perfectly. Fixing the pitch per attempt removes that freedom, and comparing
    /// attempts by AVERAGE score rather than total stops a longer split winning by having more
    /// terms to add up.
    /// </remarks>
    private ReadoutRun? ReadBlob(Frame frame, TextBlob blob, GlyphAtlas atlas)
    {
        // NOT trimmed here. Trimming the blob before the attempts double-trims: the band is then
        // recomputed on an already-trimmed box, the width shifts, and the pitch every glyph
        // boundary is derived from shifts with it. It broke a run that decoded perfectly without it.
        ReadoutRun? best = null;
        var bestMean = double.NegativeInfinity;

        // The line height fixes what a character advance CAN be for this font, so a split implying
        // an impossible pitch is rejected before it is ever scored. Without this the solver read a
        // six character run as five, dropping a digit, because five wrong glyphs happened to score
        // slightly better than six right ones against an approximated typeface.
        var band = TextBand(frame, blob);
        var lineHeight = Math.Max(1, band.Bottom - band.Top + 1);
        var minPitch = lineHeight * _readout.Atlas.PitchRatioMin;
        var maxPitch = lineHeight * _readout.Atlas.PitchRatioMax;

        for (var count = MinGlyphs; count <= MaxGlyphs; count++)
        {
            var pitch = blob.Width / (double)count;
            if (pitch < 3)
            {
                break;
            }

            if (pitch < minPitch || pitch > maxPitch)
            {
                continue;
            }

            var run = ReadBlobAtPitch(frame, blob, atlas, pitch);
            if (run is null || run.Text.Length != count)
            {
                continue;
            }

            var mean = run.WorstMargin + (run.Score / run.Text.Length);
            if (mean <= bestMean)
            {
                continue;
            }

            bestMean = mean;
            best = run;
        }

        return best is null || (decimal)best.WorstMargin < (_floorOverride ?? _readout.GlyphMarginFloor)
            ? null
            : best;
    }

    /// <summary>Shortest and longest legal readout: x1.23 through x12345.67.</summary>
    private const int MinGlyphs = 5;

    private const int MaxGlyphs = 10;

    private ReadoutRun? ReadBlobAtPitch(Frame frame, TextBlob blob, GlyphAtlas atlas, double pitch)
    {
        // The text band, not the whole blob: the crosshair sits inside the y run's box and its ink
        // invents column structure the solver would otherwise try to explain.
        var band = TextBand(frame, blob);

        // Widths hold to the pitch this attempt assumes. A full stop is the one glyph much narrower
        // than its advance, so the floor is loose while the ceiling is tight.
        var minWidth = Math.Max(2, (int)(pitch * 0.30));
        var maxWidth = Math.Max(minWidth + 1, (int)Math.Ceiling(pitch * 1.15));

        var width = blob.Width;
        if (width < 4)
        {
            return null;
        }

        // best[column, state] is the best total score reaching that column in that state.
        var states = Enum.GetValues<Shape>().Length;
        var best = new double[width + 1, states];
        var from = new (int Column, Shape State, string Glyph, double Margin)[width + 1, states];

        for (var c = 0; c <= width; c++)
        {
            for (var st = 0; st < states; st++)
            {
                best[c, st] = double.NegativeInfinity;
            }
        }

        best[0, (int)Shape.Axis] = 0;

        var ink = InkColumns(frame, blob, band);

        for (var c = 0; c < width; c++)
        {
            for (var st = 0; st < states; st++)
            {
                if (double.IsNegativeInfinity(best[c, st]))
                {
                    continue;
                }

                // Step over the empty columns BETWEEN characters. Without this the solver has to
                // tile every column with a glyph, so ordinary letter spacing means no legal split
                // exists at all and the whole run is discarded.
                for (var gap = 1; gap <= MaxGap && c + gap <= width; gap++)
                {
                    if (ink[c + gap - 1])
                    {
                        break;
                    }

                    if (best[c, st] > best[c + gap, st])
                    {
                        best[c + gap, st] = best[c, st];
                        from[c + gap, st] = (c, (Shape)st, string.Empty, 1.0);
                    }
                }

                if (!ink[c])
                {
                    continue;
                }

                for (var w = minWidth; w <= maxWidth && c + w <= width; w++)
                {
                    var candidate = CellFor(frame, blob, band, blob.Left + c, blob.Left + c + w - 1);
                    var match = atlas.Best(candidate.Cell, candidate.Aspect);
                    if (match is not { } hit)
                    {
                        continue;
                    }

                    foreach (var (next, allowed) in Transitions((Shape)st))
                    {
                        if (!allowed(hit.Template.Glyph))
                        {
                            continue;
                        }

                        // A per-glyph cost stops the solver preferring many weak narrow glyphs to
                        // a few strong ones, which is the failure mode of an unconstrained split.
                        var score = best[c, st] + hit.Score - (_costOverride ?? _readout.Atlas.GlyphCost);
                        if (score <= best[c + w, (int)next])
                        {
                            continue;
                        }

                        best[c + w, (int)next] = score;
                        from[c + w, (int)next] = (c, (Shape)st, hit.Template.Glyph, hit.Margin);
                    }
                }
            }
        }

        if (double.IsNegativeInfinity(best[width, (int)Shape.Done]))
        {
            return null;
        }

        var text = new Stack<string>();
        var worst = 1.0;
        var column = width;
        var state = Shape.Done;

        while (column > 0)
        {
            var step = from[column, (int)state];
            if (step.Glyph.Length > 0)
            {
                text.Push(step.Glyph);
                worst = Math.Min(worst, step.Margin);
            }

            column = step.Column;
            state = step.State;
        }

        // The floor rejects the WHOLE run, never one character: a readout with one uncertain digit
        // is a wrong coordinate, not a partial one.
        return new ReadoutRun(
            string.Concat(text),
            worst,
            blob,
            atlas.FontFamily,
            best[width, (int)Shape.Done]);
    }

    /// <summary>
    /// The shape a readout takes: an axis, an optional minus, digits, a point, two decimals.
    /// </summary>
    /// <remarks>
    /// Whether the game ever shows a negative coordinate is unknown, so the minus is accepted and
    /// never required. Accepting one costs nothing; being unable to read one would be a coordinate
    /// silently dropped in whatever corner of the map produces it.
    /// </remarks>
    private enum Shape
    {
        Axis,
        Sign,
        Integer,
        Fraction1,
        Fraction2,
        Done,
    }



    /// <summary>Widest run of empty columns treated as letter spacing rather than the end of a run.</summary>
    private const int MaxGap = 3;

    private static bool IsDigit(string glyph) => glyph.Length == 1 && glyph[0] is >= '0' and <= '9';

    private static (Shape Next, Func<string, bool> Allowed)[] Transitions(Shape state) => state switch
    {
        // The axis letter, then either a minus or straight into the digits.
        Shape.Axis => [(Shape.Sign, g => g is "x" or "y")],

        Shape.Sign => [(Shape.Integer, IsDigit), (Shape.Sign, g => g == "-")],

        // More integer digits, or the decimal point.
        Shape.Integer => [(Shape.Integer, IsDigit), (Shape.Fraction1, g => g == ".")],

        Shape.Fraction1 => [(Shape.Fraction2, IsDigit)],
        Shape.Fraction2 => [(Shape.Done, IsDigit)],
        _ => [],
    };

    /// <summary>
    /// The blob narrowed to the columns carrying ink within its text band, and to the band itself.
    /// </summary>
    private TextBlob? TrimToBand(Frame frame, TextBlob blob)
    {
        var band = TextBand(frame, blob);

        var left = -1;
        var right = -1;

        for (var x = 0; x < blob.Width; x++)
        {
            var lit = false;
            for (var y = band.Top; y <= band.Bottom && !lit; y++)
            {
                lit = frame.IsNearWhite(blob.Left + x, y, _readout.NearWhiteThreshold);
            }

            if (!lit)
            {
                continue;
            }

            if (left < 0)
            {
                left = x;
            }

            right = x;
        }

        return left < 0
            ? null
            : new TextBlob(blob.Left + left, band.Top, blob.Left + right, band.Bottom, blob.PixelCount);
    }

    /// <summary>
    /// The rows the text actually occupies, taken from the ROW ink profile: every row carrying a
    /// meaningful share of the densest row, first to last.
    /// </summary>
    /// <remarks>
    /// Not the median column extent. A thin part of a glyph, a full stop or the waist of a 7,
    /// occupies very few rows, so the median column height is far shorter than the line and a band
    /// built from it crops the tops off the digits. A crosshair line crossing the run contributes
    /// only a pixel or two per row, so it falls below the share and is excluded.
    /// </remarks>
    private (int Top, int Bottom) TextBand(Frame frame, TextBlob blob)
    {
        var perRow = new int[blob.Height];
        var densest = 0;

        for (var y = 0; y < blob.Height; y++)
        {
            for (var x = 0; x < blob.Width; x++)
            {
                if (frame.IsNearWhite(blob.Left + x, blob.Top + y, _readout.NearWhiteThreshold))
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

        // A descender is a couple of columns, so its rows carry little ink and fall below the
        // share. That matters: y108.62 descends and x97.56 does not, so using the full box made the
        // y run's glyph window half again too wide and the solver split 7 characters into 10.
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
    /// The typical vertical extent of the ink, column by column. Robust to a few tall columns,
    /// which is what a crosshair or a tick mark touching the run looks like.
    /// </summary>
    private int MedianColumnHeight(Frame frame, TextBlob blob)
    {
        var heights = new List<int>(blob.Width);

        for (var x = 0; x < blob.Width; x++)
        {
            var top = -1;
            var bottom = -1;

            for (var y = blob.Top; y <= blob.Bottom; y++)
            {
                if (!frame.IsNearWhite(blob.Left + x, y, _readout.NearWhiteThreshold))
                {
                    continue;
                }

                if (top < 0)
                {
                    top = y;
                }

                bottom = y;
            }

            if (top >= 0)
            {
                heights.Add(bottom - top + 1);
            }
        }

        if (heights.Count == 0)
        {
            return blob.Height;
        }

        heights.Sort();
        return heights[heights.Count / 2];
    }

    /// <summary>True per column of the blob when that column holds any near-white ink.</summary>
    private bool[] InkColumns(Frame frame, TextBlob blob, (int Top, int Bottom) band)
    {
        var columns = new bool[blob.Width];

        for (var x = 0; x < blob.Width; x++)
        {
            for (var y = band.Top; y <= band.Bottom; y++)
            {
                if (frame.IsNearWhite(blob.Left + x, y, _readout.NearWhiteThreshold))
                {
                    columns[x] = true;
                    break;
                }
            }
        }

        return columns;
    }

    /// <summary>The solver's answer with the margin floor ignored. Diagnostics only.</summary>
    private ReadoutRun? ReadBlobIgnoringFloor(Frame frame, TextBlob blob, GlyphAtlas atlas)
    {
        var saved = _floorOverride;
        try
        {
            _floorOverride = 0m;
            return ReadBlob(frame, blob, atlas);
        }
        finally
        {
            _floorOverride = saved;
        }
    }

    private (float[] Cell, double Aspect) CellFor(
        Frame frame,
        TextBlob blob,
        (int Top, int Bottom) band,
        int left,
        int right)
    {
        var width = right - left + 1;
        var height = band.Bottom - band.Top + 1;
        var ink = new float[width * height];

        var top = height;
        var bottom = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var lit = frame.IsNearWhite(left + x, band.Top + y, _readout.NearWhiteThreshold);
                ink[(y * width) + x] = lit ? 1f : 0f;

                if (!lit)
                {
                    continue;
                }

                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        // Trimmed to the glyph's own ink vertically as well, so a comma and a full stop do not both
        // normalise into the same cell as a digit.
        return bottom < top
            ? (new float[GlyphAtlas.CellSize * GlyphAtlas.CellSize], 0)
            : (GlyphAtlas.Resample(ink, width, 0, top, width, bottom - top + 1),
                width / (double)(bottom - top + 1));
    }
}
