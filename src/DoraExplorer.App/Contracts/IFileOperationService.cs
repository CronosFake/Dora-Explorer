using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

public interface IFileOperationService
{
    bool TryCreateDirectory(string parentPath, string folderName, out string? errorMessage);

    bool TryRename(string sourcePath, string newName, out string? newFullPath, out string? errorMessage);

    bool TryCopy(string sourcePath, string destinationDirectory, out string? errorMessage);

    Task<(bool Success, string? ErrorMessage)> TryCopyAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Copia informando o andamento por bytes; cancela no meio de um arquivo grande.</summary>
    Task<(bool Success, string? ErrorMessage)> TryCopyAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    bool TryMove(string sourcePath, string destinationDirectory, out string? errorMessage);

    Task<(bool Success, string? ErrorMessage)> TryMoveAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> TryMoveAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    bool TryDelete(IReadOnlyList<string> paths, FileDeleteMode mode, out string? errorMessage);

    bool TrySendToRecycleBin(IReadOnlyList<string> paths, out string? errorMessage);

    bool TryDeletePermanently(IReadOnlyList<string> paths, out string? errorMessage);

    bool TryCompress(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        out string? archivePath,
        out string? errorMessage);

    Task<(bool Success, string? ArchivePath, string? ErrorMessage)> TryCompressAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>Compacta informando o andamento por bytes lidos; cancela no meio de um arquivo grande.</summary>
    Task<(bool Success, string? ArchivePath, string? ErrorMessage)> TryCompressAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);
}
