using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DoraExplorer.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isVisible = value is true;
        if (parameter is string invert && invert.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
