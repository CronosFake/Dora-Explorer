using DoraExplorer.App.Models;

namespace DoraExplorer.App.Contracts;

/// <summary>
/// Operações em pastas de servidores FTP, SFTP e SCP: criar pasta, renomear, excluir, baixar e enviar.
/// Os caminhos dos itens são os da árvore (<c>dora-ftp://host/pasta/arquivo</c>) ou caminhos remotos simples.
/// </summary>
public interface IRemoteFileService
{
    Task<(bool Success, string? ErrorMessage)> CreateDirectoryAsync(
        NetworkConnectionInfo connection,
        string remoteDirectory,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> DeleteAsync(
        NetworkConnectionInfo connection,
        IReadOnlyList<string> remotePaths,
        CancellationToken cancellationToken = default);

    /// <summary>Renomeia na mesma pasta; devolve o novo caminho (no formato da árvore).</summary>
    Task<(bool Success, string? NewPath, string? ErrorMessage)> RenameAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        string newName,
        CancellationToken cancellationToken = default);

    /// <summary>Baixa um arquivo ou pasta para uma pasta local (com nome livre se já existir).</summary>
    Task<(bool Success, string? LocalPath, string? ErrorMessage)> DownloadAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        string localDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Envia um arquivo ou pasta local para uma pasta do servidor (com nome livre se já existir).</summary>
    Task<(bool Success, string? ErrorMessage)> UploadAsync(
        NetworkConnectionInfo connection,
        string localPath,
        string remoteDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copia (ou move) de um servidor para outro. No mesmo servidor e conta, mover só renomeia no próprio servidor;
    /// nos demais casos baixa para uma pasta temporária e envia.
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> TransferBetweenAsync(
        NetworkConnectionInfo source,
        string sourcePath,
        NetworkConnectionInfo destination,
        string destinationDirectory,
        bool move,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Baixa um arquivo para uma pasta temporária para abri-lo no programa do Windows.</summary>
    Task<(bool Success, string? LocalPath, string? ErrorMessage)> DownloadForOpenAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        CancellationToken cancellationToken = default);
}
