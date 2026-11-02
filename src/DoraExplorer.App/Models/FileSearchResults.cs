namespace DoraExplorer.App.Models;

public sealed class FileSearchResults
{
    public required IReadOnlyList<SearchResultItem> Items { get; init; }

    public bool LimitReached { get; init; }
}
