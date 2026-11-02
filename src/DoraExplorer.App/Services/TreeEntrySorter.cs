using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public static class TreeEntrySorter
{
    public static IReadOnlyList<FileSystemEntry> Sort(IEnumerable<FileSystemEntry> entries, TreeSortOptions options)
    {
        var list = entries.Where(e => !e.IsParentLink).ToList();

        list.Sort((a, b) =>
        {
            if (options.FoldersFirst)
            {
                var folderComparison = b.IsDirectory.CompareTo(a.IsDirectory);
                if (folderComparison != 0)
                {
                    return folderComparison;
                }
            }

            var fieldComparison = CompareByField(a, b, options.Field, options.Ascending);
            if (fieldComparison != 0)
            {
                return fieldComparison;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return list;
    }

    private static int CompareByField(FileSystemEntry a, FileSystemEntry b, TreeSortField field, bool ascending)
    {
        var comparison = field switch
        {
            TreeSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            TreeSortField.Size => (a.SizeInBytes ?? -1).CompareTo(b.SizeInBytes ?? -1),
            TreeSortField.Date => (a.LastModified ?? DateTime.MinValue).CompareTo(b.LastModified ?? DateTime.MinValue),
            TreeSortField.Extension => StringComparer.OrdinalIgnoreCase.Compare(
                GetExtensionSortKey(a),
                GetExtensionSortKey(b)),
            _ => 0
        };

        return ascending ? comparison : -comparison;
    }

    private static string GetExtensionSortKey(FileSystemEntry entry) =>
        entry.IsDirectory ? "\0" : Path.GetExtension(entry.Name);
}
