using System.Globalization;
using System.Windows.Data;

namespace DoraExplorer.App.Converters;

public sealed class AdaptiveModifiedDateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not DateTime date)
        {
            return string.Empty;
        }

        var useShortFormat = values[1] is true;
        var format = useShortFormat ? "dd/MM" : "dd/MM/yyyy";
        return date.ToString(format, CultureInfo.CurrentCulture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
