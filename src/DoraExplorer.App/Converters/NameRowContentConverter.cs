using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Converters;

/// <summary>
/// A grade de conteúdo de uma linha quando o usuário ajustou a largura do Nome: <c>ConverterParameter="Width"</c> dá a largura
/// fixa e <c>"Alignment"</c> o alinhamento à esquerda (as colunas à direita acompanham a borda do Nome e sobra espaço vazio no
/// fim da linha). Sem ajuste, e nas linhas que não entram nas colunas (as raízes e os protocolos da Rede: a barra de espaço do
/// disco ocupa a área visível), a grade fica como sempre: largura automática e esticada. values: largura manual do Nome (0 =
/// automática), colunas fixas, nível da linha, a linha entra nas colunas?
/// </summary>
public sealed class NameRowContentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var applies = values.Length >= 4 &&
                      values[0] is double manual && manual > 0 &&
                      values[1] is double fixedColumns &&
                      values[2] is int depth &&
                      values[3] is true;

        if (parameter is "Alignment")
        {
            return applies ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }

        return applies
            ? PaneColumnWidths.NameRowContentWidth((double)values[0], (double)values[1], (int)values[2])
            : double.NaN;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
