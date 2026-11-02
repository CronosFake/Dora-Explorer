namespace DoraExplorer.App.Models;

public sealed record TreeSortOptions(
    bool FoldersFirst = true,
    TreeSortField Field = TreeSortField.Name,
    bool Ascending = true)
{
    public static TreeSortOptions Default { get; } = new();
}
