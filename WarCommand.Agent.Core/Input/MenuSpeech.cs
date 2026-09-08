using System.Globalization;
using System.Text;

namespace WarCommand.Agent.Core.Input;

/// <summary>
/// The words a surface can be driven by: the labels it is drawing, and nothing else.
/// </summary>
/// <remarks>
/// Voice is not a second interface. It selects the option the key would select, so the vocabulary
/// at any moment is exactly <see cref="MenuStateMachine.Options"/> and a match resolves to the
/// entry's own digit. A catalog alias list that the menu does not draw is not speakable here, and a
/// label the menu draws cannot fail to be speakable, because the two are the same list.
/// <para>
/// A near miss resolves rather than refusing. The candidate set is only ever the handful of lines
/// on screen, so 'armor' against a page holding AMMO is a misheard AMMO, not an unknown word: the
/// phonetic floor that keeps 'armor' out of the catalog is measured across the whole vocabulary and
/// is the wrong instrument for a six-line page.
/// </para>
/// </remarks>
public static class MenuSpeech
{
    /// <summary>
    /// How far a heard phrase may sit from a label, as a fraction of the label's length.
    /// </summary>
    /// <remarks>
    /// 0.4 admits 'armor' for AMMO, two edits over five characters. It is deliberately loose
    /// because <see cref="Match"/> also demands the winner beat the runner-up outright: on a page
    /// of six lines a loose threshold picks the right line, and on a page where two lines are that
    /// close it picks neither.
    /// </remarks>
    private const double MaxDistanceRatio = 0.4;

    /// <summary>
    /// The digit a line is drawn beside, spoken. Word forms only.
    /// </summary>
    /// <remarks>
    /// The numerals themselves are absent on purpose: '1' is not in the Vosk small model's lexicon,
    /// so it is dropped from the recognizer with a warning and only the word ever decodes.
    /// </remarks>
    private static readonly string[] DigitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>Where a spoken line routes when it is a key rather than a row.</summary>
    public static class Keys
    {
        /// <summary>Leave this level. Live on every open level, and drawn by none of them.</summary>
        public const string Back = "nav.back";

        /// <summary>Read the map. Live wherever a coordinate is being asked for.</summary>
        public const string Here = "coord.here";

        /// <summary>One digit of whatever is being typed. The suffix is the digit.</summary>
        public const string DigitPrefix = "nav.digit.";

        /// <summary>The TOOLS surface. The 0 key, which every list level answers and none draws.</summary>
        public const string Tools = "nav.tools";

        /// <summary>Move the highlight up a line. Off the top of the board this turns the page.</summary>
        public const string Up = "nav.up";

        /// <summary>Move the highlight down a line. Off the bottom of the board this turns the page.</summary>
        public const string Down = "nav.down";


        /// <summary>Take the highlighted line, for when it was walked onto rather than named.</summary>
        public const string Select = "nav.select";
    }

    /// <summary>The number words, index-aligned to the digit each one names.</summary>
    private static readonly string[] DigitLabels =
        ["ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE"];

    /// <summary>
    /// Everything an open menu can be driven by: its drawn rows, plus the keys live on it.
    /// </summary>
    /// <remarks>
    /// A key that works on a surface is part of that surface whether or not it draws as a row, and
    /// a level that draws no rows at all is not a level with nothing to say to it. Built here
    /// rather than at the composition root so one definition answers the agent, the tests, and
    /// anything else that needs to know what can be said right now.
    /// </remarks>
    public static IReadOnlyList<MenuEntry> SurfaceOf(MenuStateMachine menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (!menu.IsOpen)
        {
            return [];
        }

        var lines = new List<MenuEntry>();

        // FIRST, so the numbers belong to whatever is being typed and not to a row that happens to
        // be drawn beside that digit. A number resolves to the first line carrying it.
        if (menu.DigitsWanted > 0)
        {
            lines.AddRange(DigitLabels.Select((label, digit) => new MenuEntry
            {
                Digit = digit,
                Path = Keys.DigitPrefix + digit.ToString(CultureInfo.InvariantCulture),
                Label = label,
            }));
        }

        // BEFORE the rows, because 0 short-circuits ahead of the per-level digit switch: on a list
        // level it opens TOOLS whatever else happens to be drawn beside that digit.
        if (menu.ZeroOpensTools)
        {
            lines.Add(new MenuEntry { Digit = 0, Path = Keys.Tools, Label = "TOOLS" });
        }

        lines.AddRange(menu.Options);
        lines.Add(new MenuEntry { Digit = -1, Path = Keys.Back, Label = "BACK" });

        // The two keys that move. On the board they are also the ONLY way to turn a page, so
        // without them every row past the first nine could be said by no word at all: page two
        // draws its own lines 1 upward and the surface only ever holds the page in front of you.
        lines.Add(new MenuEntry { Digit = -1, Path = Keys.Up, Label = "UP" });
        lines.Add(new MenuEntry { Digit = -1, Path = Keys.Down, Label = "DOWN" });

        // Naming a line is the usual way, but UP and DOWN move a highlight and something has to
        // take it. Without this, walking to a line by voice left it with no way to be pressed.
        lines.Add(new MenuEntry { Digit = -1, Path = Keys.Select, Label = "SELECT" });

        if (menu.WantsCoordinate)
        {
            lines.Add(new MenuEntry { Digit = -1, Path = Keys.Here, Label = "HERE" });
        }

        return lines;
    }

    /// <summary>
    /// The phrases the recognizer may return on this surface: every line's label AND its number.
    /// </summary>
    /// <remarks>
    /// Both, because both are drawn. The overlay puts a digit beside every selectable line, so the
    /// digit is as much a name for that line as the label is, and a surface that accepted only one
    /// of them would be refusing something the user can see.
    /// <para>
    /// Info lines are excluded: they draw no digit and the highlight skips them, so there is
    /// nothing for saying one to do.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Vocabulary(IReadOnlyList<MenuEntry> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var words = options.Where(IsSpeakable).SelectMany(Names);

        // A level collecting digits has to be able to HEAR a number read naturally, so every word
        // one can be read with goes into the recognizer's list. Without them 'hundred' and 'forty'
        // decode to [unk] and the whole utterance is lost, however well it is understood after.
        if (options.Any(o => o.Path.StartsWith(Keys.DigitPrefix, StringComparison.Ordinal)))
        {
            words = words.Concat(NumberWords.Keys);
        }

        return [.. words
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Everything one line answers to: what it says, and what number it is drawn beside.</summary>
    private static IEnumerable<string> Names(MenuEntry entry)
    {
        yield return Normalize(entry.Label);

        // A row whose label carries its state answers to its NAME as well as to the whole line.
        // "ORIGIN  x95.06 y111.61" is the origin row, and nobody reads a coordinate aloud to press
        // it. Two spaces is how this menu already separates a row's name from what it is showing.
        if (Head(entry.Label) is { Length: > 0 } head)
        {
            yield return head;
        }

        if (entry.Digit >= 0 && entry.Digit < DigitWords.Length)
        {
            yield return DigitWords[entry.Digit];
        }
    }

    /// <summary>The texts a row answers to. No numbers: those resolve exactly, never by distance.</summary>
    private static IEnumerable<string> TextNames(MenuEntry entry)
    {
        var label = Normalize(entry.Label);
        if (label.Length > 0)
        {
            yield return label;
        }

        if (Head(entry.Label) is { Length: > 0 } head && !string.Equals(head, label, StringComparison.Ordinal))
        {
            yield return head;
        }
    }

    /// <summary>The label up to its first double space, or empty when it carries no state.</summary>
    private static string Head(string? label)
    {
        if (label is null)
        {
            return string.Empty;
        }

        var at = label.IndexOf("  ", StringComparison.Ordinal);
        return at <= 0 ? string.Empty : Normalize(label[..at]);
    }

    /// <summary>
    /// The line that was said, or null when nothing on this surface is close enough.
    /// </summary>
    /// <remarks>
    /// Exact first, then nearest. A tie resolves to nothing rather than to the first candidate: two
    /// lines equally close is the case where guessing sends a request nobody asked for.
    /// </remarks>
    public static MenuEntry? Match(string? heard, IReadOnlyList<MenuEntry> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var spoken = Normalize(heard);
        if (spoken.Length == 0)
        {
            return null;
        }

        // A number names the line it is drawn beside, and exactly: 'four' is never a near miss of
        // 'five', so this resolves before any distance is measured and never through one.
        if (DigitOf(spoken) is { } digit
            && options.FirstOrDefault(o => IsSpeakable(o) && o.Digit == digit) is { } numbered)
        {
            return numbered;
        }

        MenuEntry? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;

        foreach (var option in options.Where(IsSpeakable))
        {
            // Every text a row answers to, closest first. The digit word is deliberately not here:
            // a number resolves above, exactly, because 'four' and 'five' are one edit apart.
            foreach (var label in TextNames(option))
            {
                if (string.Equals(label, spoken, StringComparison.Ordinal))
                {
                    return option;
                }

                var distance = Distance(spoken, label);
                if (distance > Budget(spoken, label))
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    best = option;
                    bestDistance = distance;
                    tied = false;
                }
                else if (distance == bestDistance && !ReferenceEquals(best, option))
                {
                    tied = true;
                }
            }
        }

        return tied ? null : best;
    }

    /// <summary>What one number word is worth, and how it may join the one before it.</summary>
    private enum NumberKind
    {
        /// <summary>Not a number word at all.</summary>
        None,

        /// <summary>0 to 9. Joins a tens or a hundred, and starts a new number after anything else.</summary>
        Unit,

        /// <summary>10 to 19. Never absorbs what follows, because nobody says nineteen four.</summary>
        Teen,

        /// <summary>20 to 90. Absorbs a following unit: forty three is one number.</summary>
        Tens,

        /// <summary>Multiplies what came before it by a hundred.</summary>
        Hundred,

        /// <summary>Ends the number without contributing to it. The decimal point.</summary>
        Break,

        /// <summary>Carries nothing and ends nothing. 'A hundred AND seven' is still 107.</summary>
        Filler,
    }

    /// <summary>Every word a number can be read with.</summary>
    /// <remarks>
    /// People read a grid every way there is: 'one zero seven point four three', 'a hundred and
    /// seven forty three', 'ten seven forty three'. They are the same five digits and all of them
    /// have to work, because the alternative is telling a gun crew how to talk.
    /// </remarks>
    private static readonly Dictionary<string, (int Value, NumberKind Kind)> NumberWords =
        new(StringComparer.Ordinal)
        {
            ["zero"] = (0, NumberKind.Unit),
            ["oh"] = (0, NumberKind.Unit),
            ["nought"] = (0, NumberKind.Unit),
            ["one"] = (1, NumberKind.Unit),
            ["two"] = (2, NumberKind.Unit),
            ["three"] = (3, NumberKind.Unit),
            ["four"] = (4, NumberKind.Unit),
            ["five"] = (5, NumberKind.Unit),
            ["six"] = (6, NumberKind.Unit),
            ["seven"] = (7, NumberKind.Unit),
            ["eight"] = (8, NumberKind.Unit),
            ["nine"] = (9, NumberKind.Unit),
            ["ten"] = (10, NumberKind.Teen),
            ["eleven"] = (11, NumberKind.Teen),
            ["twelve"] = (12, NumberKind.Teen),
            ["thirteen"] = (13, NumberKind.Teen),
            ["fourteen"] = (14, NumberKind.Teen),
            ["fifteen"] = (15, NumberKind.Teen),
            ["sixteen"] = (16, NumberKind.Teen),
            ["seventeen"] = (17, NumberKind.Teen),
            ["eighteen"] = (18, NumberKind.Teen),
            ["nineteen"] = (19, NumberKind.Teen),
            ["twenty"] = (20, NumberKind.Tens),
            ["thirty"] = (30, NumberKind.Tens),
            ["forty"] = (40, NumberKind.Tens),
            ["fourty"] = (40, NumberKind.Tens),
            ["fifty"] = (50, NumberKind.Tens),
            ["sixty"] = (60, NumberKind.Tens),
            ["seventy"] = (70, NumberKind.Tens),
            ["eighty"] = (80, NumberKind.Tens),
            ["ninety"] = (90, NumberKind.Tens),
            ["hundred"] = (100, NumberKind.Hundred),
            ["point"] = (0, NumberKind.Break),
            ["dot"] = (0, NumberKind.Break),
            ["decimal"] = (0, NumberKind.Break),
            ["and"] = (0, NumberKind.Filler),
            ["a"] = (0, NumberKind.Filler),
        };

    /// <summary>
    /// The digits an utterance names, however it was read aloud, or empty when it names none.
    /// </summary>
    /// <remarks>
    /// Read as numbers rather than substituted word for word, because 'one hundred seven' is 107
    /// and 'one' plus 'seven' is 17. A word that is not part of a number aborts the whole thing:
    /// half a grid is worse than none.
    /// </remarks>
    public static string DigitsIn(string? heard)
    {
        var words = Normalize(heard).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        var digits = new StringBuilder();
        var running = 0;
        var open = false;
        var last = NumberKind.None;

        foreach (var word in words)
        {
            if (word.All(char.IsAsciiDigit))
            {
                Flush();
                digits.Append(word);
                last = NumberKind.None;
                continue;
            }

            if (!NumberWords.TryGetValue(word, out var number))
            {
                return string.Empty;
            }

            switch (number.Kind)
            {
                case NumberKind.Filler:
                    continue;

                case NumberKind.Break:
                    Flush();
                    last = NumberKind.None;
                    continue;

                case NumberKind.Hundred:
                    running = (open && running > 0 ? running : 1) * 100;
                    open = true;
                    break;

                case NumberKind.Tens when open && last == NumberKind.Hundred:
                    running += number.Value;
                    break;

                // A unit joins a tens or fills out a hundred. After anything else it is its own
                // number, which is what makes 'one zero seven' three digits and not seventeen.
                case NumberKind.Unit when open && last is NumberKind.Tens or NumberKind.Hundred:
                    running += number.Value;
                    break;

                default:
                    Flush();
                    running = number.Value;
                    open = true;
                    break;
            }

            last = number.Kind;
        }

        Flush();
        return digits.ToString();

        void Flush()
        {
            if (open)
            {
                digits.Append(running.ToString(CultureInfo.InvariantCulture));
            }

            running = 0;
            open = false;
        }
    }

    /// <summary>
    /// One utterance read as a RUN of lines, for a level that is collecting several in a row.
    /// </summary>
    /// <remarks>
    /// Nobody says a grid one digit at a time with a pause between each, and the recognizer does
    /// not hear it that way either: eight numbers said at speed come back as one utterance of eight
    /// words. Matched whole, that reached nothing, so the coordinate page ignored everything said
    /// to it.
    /// <para>
    /// Every word must land or none does. A partial run is a misheard sentence, and typing half of
    /// a grid is worse than typing none of it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<MenuEntry> MatchRun(string? heard, IReadOnlyList<MenuEntry> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Numbers first, and however they were read. 'one hundred seven point four three' is the
        // same eight digits as 'one zero seven four three', so both type the same grid.
        if (DigitsIn(heard) is { Length: > 1 } spoken)
        {
            var typed = new List<MenuEntry>(spoken.Length);
            foreach (var digit in spoken)
            {
                if (Match(digit.ToString(), options) is not { } key)
                {
                    return [];
                }

                typed.Add(key);
            }

            return typed;
        }

        var words = Normalize(heard).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return [];
        }

        var run = new List<MenuEntry>(words.Length);
        foreach (var word in words)
        {
            if (Match(word, options) is not { } line)
            {
                return [];
            }

            run.Add(line);
        }

        return run;
    }

    /// <summary>Lower case, letters and single spaces. What both sides of a comparison are.</summary>
    /// <remarks>
    /// Labels are drawn upper case and carry punctuation the recognizer never emits, so comparing
    /// raw text would miss every multi-word line.
    /// </remarks>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLower(character, CultureInfo.InvariantCulture));
                continue;
            }

            pendingSpace = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// How many edits separate a heard phrase from a label and still count as the same line.
    /// </summary>
    /// <remarks>
    /// Measured against the LONGER of the two. Scaling by the label alone makes a short label
    /// unreachable from a longer mishearing: AMMO is four characters, so it would forgive one edit
    /// and refuse 'armor' at two, which is the exact case this exists for.
    /// </remarks>
    private static int Budget(string spoken, string label) =>
        Math.Max(1, (int)Math.Floor(Math.Max(spoken.Length, label.Length) * MaxDistanceRatio));

    /// <summary>A line the highlight can land on and a digit can press.</summary>
    private static bool IsSpeakable(MenuEntry entry) => !entry.IsInfo;

    /// <summary>
    /// The digit a phrase names, or null when it names none.
    /// </summary>
    /// <remarks>
    /// Accepts the numeral as well as the word. The recognizer only ever returns the word, because
    /// the numerals are outside the model's lexicon, but a caller reading a typed or generated
    /// phrase should not have to know that.
    /// </remarks>
    private static int? DigitOf(string spoken)
    {
        var word = Array.IndexOf(DigitWords, spoken);
        if (word >= 0)
        {
            return word;
        }

        return spoken.Length == 1 && char.IsAsciiDigit(spoken[0]) ? spoken[0] - '0' : null;
    }

    /// <summary>Levenshtein distance, two rows rather than a full matrix.</summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
