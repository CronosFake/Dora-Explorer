using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface IDirectoryIndexService : IDisposable
{
    IReadOnlyList<FileSystemEntry>? TryGetEntries(string path, ExplorerViewOptions view);

    IReadOnlyList<FileSystemEntry> ReadFromDiskAndQueueStore(string path, ExplorerViewOptions view);

    bool TryGetCounts(string path, ExplorerViewOptions view, out int folders, out int files);

    (int Folders, int Files) ComputeCountsFromDisk(string path, ExplorerViewOptions view);

    bool IsIndexedAndCurrent(string path);

    /// <summary>A última leitura do disco desta pasta falhou por falta de permissão (ela não é gravada no índice).</summary>
    bool WasAccessDenied(string path);

    bool TryGetSearchListing(string path, out IReadOnlyList<SearchListingEntry> listing);

    void Invalidate(string path);

    void ScheduleMaintenance();
}
