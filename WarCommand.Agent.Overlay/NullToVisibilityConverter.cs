using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WarCommand.Agent.Overlay;

/// <summary>Collapses a row's optional leg line when the row has no second point.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static NullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
