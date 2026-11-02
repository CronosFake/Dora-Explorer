using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>Regras do clique no cabeçalho de uma coluna (como no Explorador do Windows).</summary>
public static class SortHeaderLogic
{
    /// <summary>
    /// Clicar na coluna já ordenada inverte a direção; clicar noutra ordena por ela. Nome e tipo
    /// começam de A a Z; tamanho e data começam pelo maior e pelo mais recente.
    /// </summary>
    public static TreeSortOptions Toggle(TreeSortOptions current, TreeSortField field)
    {
        if (current.Field == field)
        {
            return current with { Ascending = !current.Ascending };
        }

        return current with
        {
            Field = field,
            Ascending = field is TreeSortField.Name or TreeSortField.Extension
        };
    }

    /// <summary>Seta de direção ao lado do título da coluna ordenada ("" nas outras).</summary>
    public static string Indicator(TreeSortOptions options, TreeSortField field) =>
        options.Field != field ? string.Empty : options.Ascending ? " ▲" : " ▼";
}
