using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// A <see cref="StatusSeverity"/> as the token it draws in.
/// </summary>
/// <remarks>
/// The mapping lives here rather than on <see cref="StatusItem"/> so the item carries no colour at
/// all, and the lookup is shared with the role hues so there is one loader and one fallback.
/// </remarks>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        RoleBrushConverter.Token(value switch
        {
            StatusSeverity.Fault => "Urgent",
            StatusSeverity.Warn => "Warn",
            _ => "Dim",
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True collapses, false shows. For the item-owned divider, which the first item must not draw.
/// </summary>
public sealed class NotBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
