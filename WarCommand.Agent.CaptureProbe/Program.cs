using System.Globalization;
using System.Text.RegularExpressions;
using WarCommand.Agent.Capture;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.CaptureProbe;

/// <summary>
/// The dev probe for the two things nobody can answer without a running copy of Wardogs: whether
/// the map readout is visible to an out-of-process capture, and whether a low-level hook can keep
/// mouse input out of the game while a hold key is down.
/// </summary>
/// <remarks>
/// Every fact about the game comes from the bundled game profile, never from a constant here, so
/// tuning a threshold or a process name is a contract edit exactly as it is in the agent.
/// Prints derived numbers only: no frame is ever written to disk, under any switch, per binding
/// rule 3, so there is deliberately no --save.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        DesktopFrameGrabber.MakeDpiAware();

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        return command switch
        {
            "list" => List(),
            "scan" => Scan(args),
            "read" => Read(args),
            "calibrate" => Calibrate(args),
            "snap" => Snap(args),
            "sweep" => Sweep(args),
            "learn" => Learn(args),
            "suppress" => Suppress(args),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("WarCommand capture probe");
        Console.WriteLine();
        Console.WriteLine("  list                                    every visible window, to find the game");
        Console.WriteLine("  scan [--process N] [--title T]          capture the client rect, find near-white text");
        Console.WriteLine("       [--threshold N] [--frames 1]");
        Console.WriteLine("       [--delay 5] [--top 12]");
        Console.WriteLine("       [--cursor] [--radius 400]           rank blobs by distance to the crosshair");
        Console.WriteLine("  read [--process N] [--threshold N]      decode the readout under the crosshair");
        Console.WriteLine("       [--delay 5] [--frames 3] [--radius 400]");
        Console.WriteLine("  calibrate --expect y108.62 --expect x97.56");
        Console.WriteLine("                                          sweep every installed font for one that reads the truth");
        Console.WriteLine("  snap --dir <path>                       save candidate MASKS for offline tuning");
        Console.WriteLine("  sweep --dir <path> --expect y108.62     tune the solver against saved masks");
        Console.WriteLine("  learn --mask <file> <text>              cut the game's own glyphs out of a known run");
        Console.WriteLine("  suppress [--key Mouse4] [--seconds 20]  can a hook keep the mouse out of the game");
        Console.WriteLine();
        Console.WriteLine("Defaults for the process names, the readout pattern and the near-white threshold");
        Console.WriteLine("all come from the bundled game-profile.json.");
        return 0;
    }

    private static int List()
    {
        var windows = GameWindow.Enumerate()
            .OrderByDescending(w => w.ClientWidth * w.ClientHeight)
            .ToList();

        Console.WriteLine($"{windows.Count} visible windows, largest first");
        Console.WriteLine();

        foreach (var w in windows.Take(25))
        {
            var shape = w.CoversItsMonitor
                ? (w.IsBorderless ? "borderless-fullscreen" : "maximised")
                : "windowed";
            Console.WriteLine(
                $"  {w.ProcessName,-28} {w.ClientWidth,5}x{w.ClientHeight,-5} {shape,-22} {Trim(w.Title, 40)}");
        }

        Console.WriteLine();
        Console.WriteLine("Take the game's process name and pass it: scan --process <name>");
        return 0;
    }

    private static int Scan(string[] args)
    {
        var profile = BundledContracts.GameProfile().Current;
        var readout = profile.MapReadout;

        var process = Option(args, "--process");
        var title = Option(args, "--title");
        var threshold = IntOption(args, "--threshold", readout.NearWhiteThreshold);
        var frames = IntOption(args, "--frames", 1);
        var delay = IntOption(args, "--delay", 5);
        var top = IntOption(args, "--top", 12);
        var byCursor = Flag(args, "--cursor");
        var radius = IntOption(args, "--radius", 400);
        var interval = IntOption(args, "--interval", 0);

        var target = Locate(profile, process, title);
        if (target is null)
        {
            Console.WriteLine($"No window matched {string.Join(", ", profile.Game.ProcessNames)}.");
            Console.WriteLine("Run 'list' and pass --process or --title.");
            return 1;
        }

        Console.WriteLine(
            $"target  {target.ProcessName}  {target.ClientWidth}x{target.ClientHeight}  \"{Trim(target.Title, 50)}\"");
        Console.WriteLine(
            $"shape   {(target.IsBorderless ? "borderless" : "bordered")}, " +
            $"{(target.CoversItsMonitor ? "covers its monitor" : "does not cover its monitor")}");

        if (!target.IsBorderless || !target.CoversItsMonitor)
        {
            Console.WriteLine("note    not borderless-fullscreen. The overlay assumes borderless; capture may still work.");
        }

        Console.WriteLine();
        Console.WriteLine($"Open the map and hover the crosshair over a point. Capturing in {delay}s...");
        Thread.Sleep(delay * 1000);

        var pattern = new Regex(readout.Pattern, RegexOptions.CultureInvariant);
        var area = GameWindow.ClientRectOnScreen(target.Handle);

        for (var i = 1; i <= frames; i++)
        {
            // The cursor is read FIRST. Reading it after the grab smears the offset by however far
            // the mouse travelled during the copy, which is exactly the measurement being taken.
            var cursor = byCursor ? GameWindow.CursorInClient(target.Handle) : null;

            var frame = DesktopFrameGrabber.Grab(area);
            if (frame is null)
            {
                Console.WriteLine($"frame {i}: capture FAILED. Black or refused: the game is likely exclusive fullscreen.");
                continue;
            }

            var lit = CountNearWhite(frame, threshold);
            var share = 100.0 * lit / (frame.Width * frame.Height);
            var blobs = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx);

            Console.WriteLine();
            Console.WriteLine(
                $"frame {i}:  {frame.Width}x{frame.Height}, {lit} near-white pixels at threshold {threshold} ({share:F3}%)");

            if (lit == 0)
            {
                Console.WriteLine("  NOTHING near-white. Either the capture came back black, or the threshold is too high.");
                continue;
            }

            if (byCursor && cursor is null)
            {
                Console.WriteLine("  cursor is outside the game window, so nothing can be ranked against it.");
                continue;
            }

            var ranked = cursor is { } c
                ? blobs
                    .Select(b => (Blob: b, Distance: DistanceTo(b, c.X, c.Y)))
                    .Where(p => p.Distance <= radius)
                    .OrderBy(p => p.Distance)
                    .ToList()
                : [.. blobs.Select(b => (Blob: b, Distance: -1.0))];

            if (cursor is { } at)
            {
                Console.WriteLine(
                    $"  cursor at {at.X},{at.Y}. {ranked.Count} of {blobs.Count} blobs within {radius}px, nearest first:");
            }
            else
            {
                Console.WriteLine($"  {blobs.Count} text-shaped blobs, {top} largest:");
            }

            Console.WriteLine("      x     y    w    h    px  density   dist     dx     dy");

            foreach (var (b, distance) in ranked.Take(top))
            {
                var shown = distance < 0 ? "     -" : $"{distance,6:F0}";
                var offset = cursor is { } origin
                    ? $"{b.Left - origin.X,6} {b.Top - origin.Y,6}"
                    : "     -      -";
                Console.WriteLine(
                    $"  {b.Left,5} {b.Top,5} {b.Width,4} {b.Height,4} {b.PixelCount,5}  {b.Density:F2}  {shown} {offset}");
            }

            var readoutSized = blobs
                .Where(b => b.Height is >= 8 and <= 32 && b.Width is >= 20 and <= 240)
                .ToList();

            Console.WriteLine();
            Console.WriteLine(
                $"  {readoutSized.Count} of those are readout-sized, and the profile expects "
                + $"{readout.ExpectedMatchesPerFrame} per frame.");
            Console.WriteLine($"  pattern to match once the atlas exists: {pattern}");

            if (interval > 0 && i < frames)
            {
                Thread.Sleep(interval);
            }
        }

        Console.WriteLine();
        Console.WriteLine("If blobs land where the readout is, the atlas has something to read.");
        return 0;
    }

    /// <summary>
    /// The whole coordinate path end to end: capture, find near-white runs near the crosshair,
    /// decode them against the atlas, and print what came out with its weakest glyph margin.
    /// </summary>
    private static int Read(string[] args)
    {
        var profile = BundledContracts.GameProfile().Current;
        var readout = profile.MapReadout;

        var threshold = IntOption(args, "--threshold", readout.NearWhiteThreshold);
        var frames = IntOption(args, "--frames", 3);
        var delay = IntOption(args, "--delay", 5);
        var radius = IntOption(args, "--radius", 400);

        var target = Locate(profile, Option(args, "--process"), Option(args, "--title"));
        if (target is null)
        {
            Console.WriteLine($"No window matched {string.Join(", ", profile.Game.ProcessNames)}.");
            return 1;
        }

        var reader = new ReadoutReader(readout);
        if (reader.Fonts.Count == 0)
        {
            Console.WriteLine("No candidate font is installed. Add one to map_readout.atlas.font_candidates.");
            return 1;
        }

        Console.WriteLine($"target  {target.ProcessName}  {target.ClientWidth}x{target.ClientHeight}");
        Console.WriteLine($"atlas   {string.Join(", ", reader.Fonts)}  floor {readout.GlyphMarginFloor}");
        Console.WriteLine();
        Console.WriteLine($"Open the map and hover a point. Reading in {delay}s...");
        Thread.Sleep(delay * 1000);

        var area = GameWindow.ClientRectOnScreen(target.Handle);

        for (var i = 1; i <= frames; i++)
        {
            var cursor = GameWindow.CursorInClient(target.Handle);
            var frame = DesktopFrameGrabber.Grab(area);
            if (frame is null)
            {
                Console.WriteLine($"frame {i}: capture FAILED.");
                continue;
            }

            var blobs = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx);
            if (cursor is { } at)
            {
                blobs = [.. blobs
                    .Where(b => Distance(b, at.X, at.Y) <= radius)
                    .OrderBy(b => Distance(b, at.X, at.Y))];
            }

            var runs = reader.Read(frame, blobs);
            var point = reader.ReadPoint(frame, blobs);

            Console.WriteLine();
            Console.WriteLine($"frame {i}: {blobs.Count} candidates near the crosshair, {runs.Count} decoded");

            foreach (var run in runs.Take(6))
            {
                Console.WriteLine(
                    $"  '{run.Text}'  margin {run.WorstMargin:F3}  {run.FontFamily}  at {run.Blob.Left},{run.Blob.Top}");
            }

            if (Flag(args, "--why"))
            {
                foreach (var blob in blobs.Take(IntOption(args, "--top", 6)))
                {
                    Console.WriteLine($"  {reader.Explain(frame, blob)}");
                }
            }

            Console.WriteLine(point is { } p
                ? $"  POINT  x{p.X} y{p.Y}   worst margin {p.Confidence:F3}   raw '{p.RawText}'"
                : "  no complete x and y pair this frame");
        }

        Console.WriteLine();
        Console.WriteLine("Nothing decoded means the atlas font is wrong, or the floor is too high.");
        Console.WriteLine("Try --threshold lower to catch dimmer text, and see map_readout.atlas.");
        return 0;
    }

    private static double Distance(TextBlob blob, int x, int y) => DistanceTo(blob, x, y);

    /// <summary>
    /// Sweeps every installed typeface against a readout the user has read off the screen, and
    /// reports which faces reproduce it.
    /// </summary>
    /// <remarks>
    /// The typeface is a fact about the game and nobody knows what it is. Guessing three candidates
    /// and iterating cost several rounds; with the true string in hand the machine can answer in
    /// one pass, and the answer goes into map_readout.atlas.font_candidates as data.
    /// </remarks>
    private static int Calibrate(string[] args)
    {
        var profile = BundledContracts.GameProfile().Current;
        var readout = profile.MapReadout;

        var expected = args
            .Select((a, i) => (a, i))
            .Where(p => string.Equals(p.a, "--expect", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.i + 1 < args.Length)
            .Select(p => args[p.i + 1])
            .ToList();

        if (expected.Count == 0)
        {
            Console.WriteLine("Give me what the game shows: calibrate --expect y108.62 --expect x97.56");
            return 1;
        }

        var target = Locate(profile, Option(args, "--process"), Option(args, "--title"));
        if (target is null)
        {
            Console.WriteLine("The game window was not found.");
            return 1;
        }

        var threshold = IntOption(args, "--threshold", readout.NearWhiteThreshold);
        var radius = IntOption(args, "--radius", 400);
        var delay = IntOption(args, "--delay", 8);

        var faces = GlyphAtlas.InstalledFamilies();
        Console.WriteLine($"sweeping {faces.Count} installed faces for {string.Join(" and ", expected)}");
        Console.WriteLine($"Keep the cursor exactly where it is. Reading in {delay}s...");
        Thread.Sleep(delay * 1000);

        var cursor = GameWindow.CursorInClient(target.Handle);
        var frame = DesktopFrameGrabber.Grab(GameWindow.ClientRectOnScreen(target.Handle));
        if (frame is null || cursor is not { } at)
        {
            Console.WriteLine("capture failed, or the cursor is outside the game window.");
            return 1;
        }

        var blobs = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx)
            .Where(b => DistanceTo(b, at.X, at.Y) <= radius)
            .OrderBy(b => DistanceTo(b, at.X, at.Y))
            .ToList();

        Console.WriteLine($"{blobs.Count} candidates near the crosshair");
        Console.WriteLine();

        var hits = new List<(string Face, int Matched, double Margin)>();

        foreach (var face in faces)
        {
            var reader = new ReadoutReader(readout, [face]);
            var decoded = reader.Read(frame, blobs).Select(r => r.Text).ToList();
            var matched = expected.Count(e => decoded.Contains(e, StringComparer.OrdinalIgnoreCase));
            if (matched == 0)
            {
                continue;
            }

            var margin = reader.Read(frame, blobs)
                .Where(r => expected.Contains(r.Text, StringComparer.OrdinalIgnoreCase))
                .Select(r => r.WorstMargin)
                .DefaultIfEmpty(0)
                .Min();

            hits.Add((face, matched, margin));
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("No installed face reproduced it. What the candidates actually are:");
            Console.WriteLine();

            var probe = new ReadoutReader(readout);
            foreach (var blob in blobs)
            {
                Console.WriteLine(
                    $"  {blob.Width,3}x{blob.Height,-3} at {blob.Left},{blob.Top}  "
                    + $"dx {blob.Left - at.X,5} dy {blob.Top - at.Y,5}  ink {blob.PixelCount,4}");
                Console.WriteLine($"      {probe.Explain(frame, blob)}");
            }

            Console.WriteLine();
            Console.WriteLine("A run of 7 characters should be roughly 30-40 wide and 13-15 tall.");
            Console.WriteLine("If no candidate is that shape, the scanner is not isolating the readout.");
            return 0;
        }

        Console.WriteLine("face                             matched  worst margin");
        foreach (var hit in hits.OrderByDescending(h => h.Matched).ThenByDescending(h => h.Margin).Take(15))
        {
            Console.WriteLine($"  {hit.Face,-30} {hit.Matched,7}  {hit.Margin,12:F3}");
        }

        Console.WriteLine();
        Console.WriteLine("Put the winner first in map_readout.atlas.font_candidates.");
        return 0;
    }

    /// <summary>
    /// Saves the near-white MASK of each candidate near the crosshair, so the decoder can be tuned
    /// without a human holding a cursor still for every trial.
    /// </summary>
    /// <remarks>
    /// A mask, never a frame. Binding rule 3 keeps screen frames off disk and off the wire; what
    /// lands here is the 1-bit shape the scanner already isolated, with no game imagery in it.
    /// </remarks>
    private static int Snap(string[] args)
    {
        var profile = BundledContracts.GameProfile().Current;
        var readout = profile.MapReadout;
        var dir = Option(args, "--dir") ?? "masks";
        var delay = IntOption(args, "--delay", 8);
        var radius = IntOption(args, "--radius", 300);
        var threshold = IntOption(args, "--threshold", readout.NearWhiteThreshold);

        var target = Locate(profile, Option(args, "--process"), Option(args, "--title"));
        if (target is null)
        {
            Console.WriteLine("The game window was not found.");
            return 1;
        }

        Console.WriteLine($"Hover the point. Capturing in {delay}s...");
        Thread.Sleep(delay * 1000);

        var cursor = GameWindow.CursorInClient(target.Handle);
        var frame = DesktopFrameGrabber.Grab(GameWindow.ClientRectOnScreen(target.Handle));
        if (frame is null || cursor is not { } at)
        {
            Console.WriteLine("capture failed, or the cursor is outside the game window.");
            return 1;
        }

        Directory.CreateDirectory(dir);

        var blobs = NearWhiteScanner.Scan(frame, threshold, glyphGap: readout.GlyphGapPx)
            .Where(b => DistanceTo(b, at.X, at.Y) <= radius)
            .OrderBy(b => DistanceTo(b, at.X, at.Y))
            .ToList();

        // Several thresholds from ONE capture. A higher threshold thins the strokes, which is what
        // separates characters the anti-aliased outline has bridged together, and asking a human to
        // hold a cursor still once per threshold is not a debugging loop anybody finishes.
        int[] thresholds = [threshold, 245, 250, 252, 254];

        var written = 0;
        foreach (var t in thresholds.Distinct().Order())
        {
            var perT = NearWhiteScanner.Scan(frame, t, glyphGap: readout.GlyphGapPx)
                .Where(b => DistanceTo(b, at.X, at.Y) <= radius)
                .OrderBy(b => DistanceTo(b, at.X, at.Y))
                .ToList();

            var index = 0;
            foreach (var blob in perT)
            {
                var name = Path.Combine(dir, $"t{t}-blob-{index:00}-{blob.Width}x{blob.Height}.mask");
                File.WriteAllLines(name, frame.MaskOf(blob, t));
                index++;
                written++;
            }

            Console.WriteLine($"  threshold {t}: {perT.Count} candidates");
        }

        Console.WriteLine($"{written} masks written to {dir}");
        return 0;
    }

    /// <summary>Sweeps the solver's per-glyph cost against saved masks and a known true reading.</summary>
    private static int Sweep(string[] args)
    {
        var readout = BundledContracts.GameProfile().Current.MapReadout;
        var dir = Option(args, "--dir") ?? "masks";

        var expected = args
            .Select((a, i) => (a, i))
            .Where(p => string.Equals(p.a, "--expect", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.i + 1 < args.Length)
            .Select(p => args[p.i + 1])
            .ToList();

        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"No masks in {dir}. Run snap first.");
            return 1;
        }

        var masks = Directory.GetFiles(dir, "*.mask").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Console.WriteLine($"{masks.Count} masks, looking for {string.Join(" and ", expected)}");
        Console.WriteLine();

        for (var cost = 0.40; cost <= 0.76; cost += 0.02)
        {
            var decoded = new List<string>();

            foreach (var file in masks)
            {
                var frame = Frame.FromMask(File.ReadAllLines(file));
                var blob = new TextBlob(0, 0, frame.Width - 1, frame.Height - 1, 1);
                var reader = new ReadoutReader(readout, costOverride: cost);
                decoded.AddRange(reader.Read(frame, [blob]).Select(r => r.Text));
            }

            var matched = expected.Count(e => decoded.Contains(e, StringComparer.Ordinal));
            var flag = matched == expected.Count ? "  <== ALL" : string.Empty;
            Console.WriteLine($"  cost {cost:F2}  matched {matched}/{expected.Count}  {string.Join(" ", decoded.Take(4))}{flag}");
        }

        return 0;
    }

    /// <summary>
    /// Cuts a run whose text is known into its characters and emits them as atlas templates.
    /// </summary>
    /// <remarks>
    /// The game ships its own typeface. Sweeping every installed face found none that reproduces
    /// the readout, so each decode was matching approximated shapes and picking wrong digits: a 4
    /// read as a 1. Given a run the user has read off the screen, the ink clusters ARE the
    /// characters, so the shapes can be taken from the game itself and matched against exactly.
    /// </remarks>
    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static int Learn(string[] args)
    {
        var readout = BundledContracts.GameProfile().Current.MapReadout;

        var samples = new List<(string File, string Text)>();
        for (var i = 0; i + 2 < args.Length; i++)
        {
            if (string.Equals(args[i], "--mask", StringComparison.OrdinalIgnoreCase))
            {
                samples.Add((args[i + 1], args[i + 2]));
            }
        }

        if (samples.Count == 0)
        {
            Console.WriteLine("learn --mask <file> <text> [--mask <file> <text> ...] [--out <file>]");
            return 1;
        }

        var learned = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (file, text) in samples)
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"missing: {file}");
                return 1;
            }

            var cut = CutGlyphs(File.ReadAllLines(file), readout.NearWhiteThreshold);

            if (cut.Count != text.Length)
            {
                Console.WriteLine(
                    $"{Path.GetFileName(file)}: {cut.Count} clusters, but '{text}' has {text.Length} characters.");
                Console.WriteLine("A run only teaches its glyphs when it segments cleanly. Capture another.");
                return 1;
            }

            for (var i = 0; i < cut.Count; i++)
            {
                var glyph = text[i].ToString();
                if (learned.ContainsKey(glyph))
                {
                    continue;
                }

                learned[glyph] = cut[i];
                Console.WriteLine($"  '{glyph}'  {cut[i][0].Length}x{cut[i].Count}");
            }
        }

        var missing = readout.Glyphs.Where(g => !learned.ContainsKey(g)).ToList();
        Console.WriteLine();
        Console.WriteLine($"learned {learned.Count} of {readout.Glyphs.Count}");
        if (missing.Count > 0)
        {
            Console.WriteLine($"still missing: {string.Join(" ", missing)}");
        }

        var outFile = Option(args, "--out") ?? "learned-glyphs.json";
        File.WriteAllText(
            outFile,
            System.Text.Json.JsonSerializer.Serialize(learned, Indented));

        Console.WriteLine($"written to {outFile}");
        return 0;
    }

    /// <summary>The ink clusters of a mask, inside its text band, each as its own rows.</summary>
    private static List<List<string>> CutGlyphs(IReadOnlyList<string> rows, int threshold)
    {
        var frame = Frame.FromMask(rows);
        var blob = new TextBlob(0, 0, frame.Width - 1, frame.Height - 1, 1);
        var band = NearWhiteScanner.BandOf(frame, blob, threshold);

        var lit = new bool[frame.Width];
        for (var x = 0; x < frame.Width; x++)
        {
            for (var y = band.Top; y <= band.Bottom && !lit[x]; y++)
            {
                lit[x] = frame.IsNearWhite(x, y, threshold);
            }
        }

        var cuts = new List<List<string>>();
        var start = -1;

        for (var x = 0; x <= frame.Width; x++)
        {
            var on = x < frame.Width && lit[x];
            if (on && start < 0)
            {
                start = x;
            }
            else if (!on && start >= 0)
            {
                var glyph = new List<string>();
                for (var y = band.Top; y <= band.Bottom; y++)
                {
                    var row = new char[x - start];
                    for (var c = start; c < x; c++)
                    {
                        row[c - start] = frame.IsNearWhite(c, y, threshold) ? '#' : '.';
                    }

                    glyph.Add(new string(row));
                }

                cuts.Add(glyph);
                start = -1;
            }
        }

        return cuts;
    }

    private static int Suppress(string[] args)
    {
        var key = Option(args, "--key") ?? "Mouse4";
        var seconds = IntOption(args, "--seconds", 20);

        var (code, label) = key.ToLowerInvariant() switch
        {
            "mouse3" => (0x04, "Mouse3"),
            "mouse4" => (0x05, "Mouse4"),
            "mouse5" => (0x06, "Mouse5"),
            "rightalt" => (0xA5, "RightAlt"),
            "capslock" => (0x14, "CapsLock"),
            _ => (0, string.Empty),
        };

        if (code == 0)
        {
            Console.WriteLine("--key must be Mouse3, Mouse4, Mouse5, RightAlt or CapsLock.");
            Console.WriteLine("Mouse1 and Mouse2 are deliberately absent: taking fire or ADS from the game is never a control.");
            return 1;
        }

        Console.WriteLine($"suppression probe, hold key {label}");
        Console.WriteLine($"elevated: {IsElevated()}   a non-elevated hook cannot block input to an elevated game");
        Console.WriteLine();

        SuppressionProbe.Run(code, label, seconds);
        return 0;
    }

    private static WindowCandidate? Locate(GameProfile profile, string? process, string? title)
    {
        var windows = GameWindow.Enumerate();

        if (process is not null)
        {
            return windows.FirstOrDefault(
                w => w.ProcessName.Contains(process, StringComparison.OrdinalIgnoreCase));
        }

        if (title is not null)
        {
            return windows.FirstOrDefault(
                w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        }

        return GameWindow.Find(profile.Game.ProcessNames);
    }

    private static double DistanceTo(TextBlob blob, int x, int y)
    {
        var cx = blob.Left + (blob.Width / 2.0);
        var cy = blob.Top + (blob.Height / 2.0);
        return Math.Sqrt(((cx - x) * (cx - x)) + ((cy - y) * (cy - y)));
    }

    private static bool Flag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int CountNearWhite(Frame frame, int threshold)
    {
        var count = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                if (frame.IsNearWhite(x, y, threshold))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntOption(string[] args, string name, int fallback) =>
        int.TryParse(Option(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "~";
}
