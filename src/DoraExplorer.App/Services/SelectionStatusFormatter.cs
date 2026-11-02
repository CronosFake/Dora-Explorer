using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Services;

/// <summary>Texto da barra de status do painel: o que está selecionado e quanto ocupa.</summary>
public static class SelectionStatusFormatter
{
    private const string Separator = " · ";

    public static string Format(IReadOnlyList<NavigationTreeNodeViewModel> selectedItems, NavigationTreeNodeViewModel? node)
    {
        if (selectedItems.Count > 1)
        {
            var totalBytes = selectedItems.Where(item => item.SizeInBytes.HasValue).Sum(item => item.SizeInBytes!.Value);
            var text = $"{selectedItems.Count} itens selecionados";
            return totalBytes > 0
                ? text + Separator + NavigationTreeNodeViewModel.FormatSize(totalBytes)
                : text;
        }

        var current = selectedItems.Count == 1 ? selectedItems[0] : node;
        if (current is null || current.IsPlaceholder)
        {
            return string.Empty;
        }

        return current.Kind switch
        {
            NavigationNodeKind.Drive => Join(current.Name, current.StorageSummary),
            NavigationNodeKind.Directory => Join(current.Name, DirectorySummary(current)),
            NavigationNodeKind.File or NavigationNodeKind.Archive =>
                Join(current.Name, current.FileSizeText, $"{current.ModifiedDateText} {current.ModifiedTimeText}".Trim()),
            _ => string.Empty
        };
    }

    // Numa pasta que o Windows não deixou listar, "0 pastas · 0 arquivos" seria mentira: o texto diz o que houve (o cadeado da linha diz o mesmo).
    private static string? DirectorySummary(NavigationTreeNodeViewModel node) =>
        node.ListingDenied ? "sem permissão para abrir"
        : node.ShowDirectoryCountSummary ? Counts(node)
        : null;

    private static string Counts(NavigationTreeNodeViewModel node) =>
        $"{Plural(node.FolderCount, "pasta", "pastas")}{Separator}{Plural(node.FileCount, "arquivo", "arquivos")}";

    private static string Plural(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    private static string Join(params string?[] parts) =>
        string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
