using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Resource key to brush. A DynamicResource key cannot itself be a binding, so the lookup happens
/// here and OverlayTokens stays the one place the role hues are written down.
/// </summary>
/// <remarks>
/// The tokens are merged into BoardView, not into App.xaml, so an application-only lookup finds
/// nothing and every role renders the neutral grey. It reads as a board that has no role colour at
/// all rather than as a missing resource, which is why this loads the dictionary itself.
/// </remarks>
public sealed class RoleBrushConverter : IValueConverter
{
    private const string TokensUri = "/WarCommand.Agent.Overlay;component/Theme/OverlayTokens.xaml";

    private static readonly SolidColorBrush Fallback = Frozen(0xB4, 0xB6, 0xB8);

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, Brush>? _tokens;

    /// <summary>
    /// The role hues, loaded once and only once they actually load.
    /// </summary>
    /// <remarks>
    /// Deliberately not a Lazy. The pack URI needs WPF's resource plumbing to be up, so the first
    /// caller in a process can legitimately come away with nothing, and a Lazy caches that empty
    /// answer for the life of the process: every role then paints the fallback grey forever, and
    /// which test ran first decides it. An empty load is not cached, so the next caller retries.
    /// </remarks>
    private static IReadOnlyDictionary<string, Brush> Tokens
    {
        get
        {
            if (_tokens is { Count: > 0 } cached)
            {
                return cached;
            }

            lock (Gate)
            {
                if (_tokens is { Count: > 0 } inner)
                {
                    return inner;
                }

                var loaded = LoadTokens();
                if (loaded.Count > 0)
                {
                    _tokens = loaded;
                }

                return loaded;
            }
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return Fallback;
        }

        // The application first, so a theme swapped in at app level still wins, then the overlay's
        // own tokens, which are the only place these hues are actually declared today.
        if (Application.Current?.TryFindResource(key) is Brush found)
        {
            return found;
        }

        return Tokens.TryGetValue(key, out var token) ? token : Fallback;
    }

    /// <summary>
    /// The role hues, frozen. The dictionary is loaded once on whichever thread asks first, and an
    /// unfrozen brush belongs to that thread: handing it to any other one throws on the first read
    /// of its colour. Frozen clones are the only cross-thread-safe thing to cache.
    /// </summary>
    private static Dictionary<string, Brush> LoadTokens()
    {
        var frozen = new Dictionary<string, Brush>(StringComparer.Ordinal);

        try
        {
            var dictionary = new ResourceDictionary { Source = new Uri(TokensUri, UriKind.Relative) };
            foreach (var entry in dictionary.Keys)
            {
                if (entry is string key && dictionary[key] is SolidColorBrush brush)
                {
                    frozen[key] = Frozen(brush.Color.R, brush.Color.G, brush.Color.B);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Every failure, not just IOException: a pack URI resolved before WPF's resource
            // plumbing is up throws several other things, and catching one of them was the
            // difference between a retry and a process that paints grey from then on.
        }

        return frozen;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
