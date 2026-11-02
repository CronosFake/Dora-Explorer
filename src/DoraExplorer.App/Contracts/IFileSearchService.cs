using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface IFileSearchService
{
    FileSearchResults Search(
        string rootPath,
        string pattern,
        bool recursive,
        CancellationToken cancellationToken = default);
}
