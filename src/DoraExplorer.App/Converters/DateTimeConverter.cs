using System.Globalization;
using System.Windows.Data;

namespace DoraExplorer.App.Converters;

public sealed class DateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DateTime dateTime
            ? dateTime.ToString("dd/MM/yyyy HH:mm", culture)
            : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
