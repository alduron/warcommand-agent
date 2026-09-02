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

    private static readonly Lazy<IReadOnlyDictionary<string, Brush>> Tokens =
        new(LoadTokens, isThreadSafe: true);

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

        return Tokens.Value.TryGetValue(key, out var token) ? token : Fallback;
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
        catch (IOException)
        {
            // No dictionary means the fallback hue, which is what an unknown key gets anyway.
        }

        return frozen;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
