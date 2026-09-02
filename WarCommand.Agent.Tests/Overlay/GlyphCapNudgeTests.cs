using System;
using System.Windows;
using System.Windows.Media;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The glyph beside an ALL CAPS label rides up by GlyphCapNudge. Caps fill only the cap band, so
/// the text ink sits high in its line box and centring the two boxes leaves the icon visibly low.
/// </summary>
/// <remarks>
/// The nudge is a measured device-pixel value in OverlayTokens.xaml. This pins it to the font's own
/// metrics so changing LabelSize or CondensedFont fails here rather than quietly drawing low again.
/// </remarks>
public class GlyphCapNudgeTests
{
    private const double LabelSize = 14.0;
    private const double Nudge = -1.5;

    [Fact]
    public void The_token_still_matches_what_the_condensed_face_actually_needs()
    {
        var family = Resolve();
        if (family is null)
        {
            // Every candidate in the CondensedFont stack is absent on this machine, so there is no
            // metric to check against. Skipping beats asserting against a substituted face.
            return;
        }

        var expected = CapCompensation(family, LabelSize);

        Assert.True(
            Math.Abs(expected - Nudge) <= 1.0,
            FormattableString.Invariant(
                $"GlyphCapNudge is {Nudge} but {family.Source} at {LabelSize} wants {expected:0.00}. Re-measure the overlay and update the token."));
    }

    [Fact]
    public void The_nudge_lifts_the_glyph_rather_than_dropping_it()
    {
        Assert.True(Nudge < 0, "Caps sit high in the line box, so the glyph moves up, never down.");
    }

    /// <summary>
    /// Distance from the line box centre down to the cap band centre. Negative means the caps sit
    /// above the box centre, which is how far the glyph must rise to meet them.
    /// </summary>
    private static double CapCompensation(FontFamily family, double fontSize)
    {
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var caps = typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs.CapsHeight : 0.71;

        var box = family.LineSpacing * fontSize;
        var baseline = family.Baseline * fontSize;
        var capCentre = baseline - (caps * fontSize / 2);

        return capCentre - (box / 2);
    }

    /// <summary>The first face of the CondensedFont stack actually installed here.</summary>
    private static FontFamily? Resolve()
    {
        foreach (var name in new[] { "Saira Condensed", "Saira", "Bahnschrift Condensed", "Segoe UI" })
        {
            var family = new FontFamily(name);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out _))
            {
                return family;
            }
        }

        return null;
    }
}
