using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DoraExplorer.App.Converters;

public sealed class BoolAndToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (var value in values)
        {
            if (value is not true)
            {
                return Visibility.Collapsed;
            }
        }

        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
