using System.Globalization;
using System.Windows.Data;

namespace DoraExplorer.App.Converters;

public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size)
        {
            return string.Empty;
        }

        var unitIndex = 0;
        var displaySize = (double)size;

        while (displaySize >= 1024 && unitIndex < Units.Length - 1)
        {
            displaySize /= 1024;
            unitIndex++;
        }

        var format = unitIndex == 0 ? "0" : "0.##";
        return $"{displaySize.ToString(format, culture)} {Units[unitIndex]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
