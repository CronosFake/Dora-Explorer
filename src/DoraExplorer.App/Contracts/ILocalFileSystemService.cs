using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface ILocalFileSystemService
{
    IReadOnlyList<FileSystemEntry> GetEntries(string path);

    IReadOnlyList<FileSystemEntry> GetDirectoryChildren(string path);
}
