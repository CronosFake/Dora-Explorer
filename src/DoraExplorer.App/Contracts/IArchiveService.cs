using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface IArchiveService
{
    bool TryListEntries(
        string archivePath,
        string entryDirectory,
        out IReadOnlyList<FileSystemEntry> entries,
        out string? errorMessage);

    bool TryExtractEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        out string? errorMessage);

    bool TryRemoveEntries(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        out string? errorMessage);

    bool TryExtractEntryToTempAndOpen(
        string archivePath,
        string entryPath,
        out string? errorMessage);
}
