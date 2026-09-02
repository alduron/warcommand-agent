using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Resource key to brush. A DynamicResource key cannot itself be a binding, so the lookup happens
/// here and OverlayTokens stays the one place the role hues are written down.
/// </summary>
public sealed class RoleBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = Frozen(0xB4, 0xB6, 0xB8);

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

        if (Application.Current?.TryFindResource(key) is Brush found)
        {
            return found;
        }

        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
