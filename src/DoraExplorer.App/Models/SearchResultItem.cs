namespace DoraExplorer.App.Models;

public sealed class SearchResultItem
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    public long? SizeInBytes { get; init; }
}
