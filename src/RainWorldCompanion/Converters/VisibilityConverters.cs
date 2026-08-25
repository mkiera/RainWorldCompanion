using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RainWorldCompanion.Converters;

/// <summary>
/// Collapses an element when the bound string is null, empty or whitespace. Saves a matching
/// "HasX" property on every view model that carries an optional line of text.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("StringToVisibilityConverter is one way.");
}

/// <summary>
/// Visible when the bound boolean is false. The framework converter only handles the true case.
/// </summary>
public sealed class NotBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("NotBooleanToVisibilityConverter is one way.");
}
