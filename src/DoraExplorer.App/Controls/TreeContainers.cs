using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DoraExplorer.App.Controls;

/// <summary>
/// Acesso aos containers que o WPF realmente criou. Com a virtualização só existem os visíveis (algumas dezenas),
/// mesmo numa pasta de 100 mil itens. Percorrer <c>parent.Items</c> chamando <c>ContainerFromItem(item)</c> para cada
/// item é a forma errada: a chamada procura o item na lista inteira, então o custo é quadrático (numa pasta de
/// 100 mil itens são bilhões de comparações por passada, e várias passadas rodam a cada mudança).
/// </summary>
internal static class TreeContainers
{
    /// <summary>Containers criados de <paramref name="parent"/>, na ordem da lista (cópia: pode-se alterar a árvore ao percorrer).</summary>
    public static List<TreeViewItem> Realized(ItemsControl parent)
    {
        var result = new List<TreeViewItem>();
        if (FindItemsHost(parent) is { } host)
        {
            foreach (UIElement child in host.Children)
            {
                if (child is TreeViewItem item)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    private static Panel? FindItemsHost(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Panel { IsItemsHost: true } panel)
            {
                return panel;
            }

            if (child is TreeViewItem)
            {
                continue; // o painel de um item filho é de outro nível
            }

            if (FindItemsHost(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
