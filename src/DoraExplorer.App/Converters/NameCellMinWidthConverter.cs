using System.Globalization;
using System.Windows.Data;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Converters;

/// <summary>
/// Largura mínima da célula do nome de uma linha: a largura da linha mais larga da lista (values[0]) menos o que fica à esquerda
/// do nome naquela linha (values[1] é o nível dela na árvore). Todas as linhas ficam com a mesma largura natural, então a
/// extensão rolável não muda conforme a lista vai sendo rolada.
/// </summary>
public sealed class NameCellMinWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double rowWidth || values[1] is not int depth)
        {
            return 0.0;
        }

        return PaneColumnWidths.NameCellMinWidth(rowWidth, depth);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
