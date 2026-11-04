using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.Views.Dialogs;
using Application = System.Windows.Application;

namespace DoraExplorer.App.ViewModels;

/// <summary>
/// Operações do painel em pastas de servidores (FTP, SFTP e SCP): criar pasta, renomear, excluir, copiar, mover,
/// compactar e abrir arquivo. O trabalho de rede fica em <see cref="IRemoteFileService"/>; aqui ficam as regras do painel.
/// </summary>
public partial class FilePaneViewModel
{
    private readonly IRemoteFileService _remoteFiles;

    private static bool IsRemotePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && NetworkFileService.TryParseRemotePath(path, out _, out _, out _);

    private static string AccountKey(NetworkConnectionInfo connection) =>
        $"{connection.Protocol}|{connection.Host.ToUpperInvariant()}|{connection.Port}|{connection.Username}";

    /// <summary>Caminho da árvore da pasta que contém o item remoto.</summary>
    private static string RemoteParentUri(string remoteItemPath)
    {
        _ = NetworkFileService.TryParseRemotePath(remoteItemPath, out var protocol, out var host, out var remotePath);
        return NetworkFileService.BuildRemotePath(protocol, host, RemoteTransferEngine.ParentOf(remotePath));
    }

    /// <summary>Conexão (já aberta pelo usuário) do servidor a que o caminho pertence.</summary>
    private bool TryGetRemoteConnection(string path, out NetworkConnectionInfo connection, out string? errorMessage)
    {
        errorMessage = null;
        if (!_networkSessions.TryGetForPath(path, out connection!))
        {
            errorMessage = "Não há conexão aberta com este servidor. Conecte-se de novo pelo cliente de rede.";
            return false;
        }

        if (connection.Protocol == NetworkProtocol.Tftp)
        {
            errorMessage = "O TFTP não permite operar em pastas: use Enviar ou Receber no cliente TFTP.";
            return false;
        }

        return true;
    }

    /// <summary>Espera uma operação curta de rede (criar pasta, renomear). Tem limite de tempo próprio na camada remota.</summary>
    private static T RunBlocking<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void ShowRemoteError(string? message, string title)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageDialog.Show(Application.Current?.MainWindow, message, title);
        }
    }

    // ---- Excluir ------------------------------------------------------------------------------

    /// <summary>
    /// Começa a exclusão no servidor sem travar a janela (apagar uma árvore grande leva tempo): a lista é atualizada
    /// e um eventual erro aparece quando terminar.
    /// </summary>
    private bool TryStartRemoteDelete(IReadOnlyList<string> remotePaths, out string? errorMessage)
    {
        errorMessage = null;
        var resolved = new List<(string Path, NetworkConnectionInfo Connection)>();
        foreach (var path in remotePaths)
        {
            if (!TryGetRemoteConnection(path, out var connection, out errorMessage))
            {
                return false;
            }

            resolved.Add((path, connection));
        }

        foreach (var account in resolved.GroupBy(item => AccountKey(item.Connection)))
        {
            _ = DeleteRemoteAsync(account.First().Connection, account.Select(item => item.Path).ToList());
        }

        return true;
    }

    private async Task DeleteRemoteAsync(NetworkConnectionInfo connection, List<string> paths)
    {
        try
        {
            var result = await _remoteFiles.DeleteAsync(connection, paths).ConfigureAwait(true);
            foreach (var parent in paths.Select(RemoteParentUri).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RefreshDirectory(parent);
            }

            if (!result.Success)
            {
                ShowRemoteError(result.ErrorMessage ?? "Não foi possível excluir no servidor.", "Excluir");
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao excluir no servidor", ex);
            ShowRemoteError(ex.Message, "Excluir");
        }
    }

    // ---- Criar pasta e renomear -----------------------------------------------------------------

    private bool TryCreateRemoteFolder(string parent, string folderName, out string? errorMessage)
    {
        if (!TryGetRemoteConnection(parent, out var connection, out errorMessage))
        {
            return false;
        }

        _ = NetworkFileService.TryParseRemotePath(parent, out _, out _, out var remoteParent);
        string name;
        try
        {
            name = RemoteTransferEngine.SafeName(folderName.Trim());
        }
        catch (IOException ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        var target = RemoteTransferEngine.Combine(remoteParent, name);
        var result = RunBlocking(() => _remoteFiles.CreateDirectoryAsync(connection, target));
        if (!result.Success)
        {
            errorMessage = result.ErrorMessage ?? "Não foi possível criar a pasta no servidor.";
            return false;
        }

        RefreshDirectory(parent);
        errorMessage = null;
        return true;
    }

    /// <summary>"Nova pasta" num servidor: escolhe um nome livre entre os itens já listados (sem renomear em linha).</summary>
    private bool TryCreateRemoteFolderWithFreeName(string parent, out string? errorMessage)
    {
        var existing = FindNodeByPath(parent)?.Children
            .Where(child => !child.IsPlaceholder)
            .Select(child => child.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        return TryCreateRemoteFolder(parent, RemoteTransferEngine.UniqueName(existing.Contains, "Nova pasta"), out errorMessage);
    }

    private bool TryRenameRemote(string sourcePath, string newName, out string? newPath, out string? errorMessage)
    {
        newPath = null;
        if (!TryGetRemoteConnection(sourcePath, out var connection, out errorMessage))
        {
            return false;
        }

        var result = RunBlocking(() => _remoteFiles.RenameAsync(connection, sourcePath, newName));
        if (!result.Success)
        {
            errorMessage = result.ErrorMessage ?? "Não foi possível renomear no servidor.";
            return false;
        }

        newPath = result.NewPath;
        errorMessage = null;
        RefreshDirectory(RemoteParentUri(sourcePath));
        return true;
    }

    // ---- Abrir ---------------------------------------------------------------------------------

    /// <summary>Baixa o arquivo para uma pasta temporária e o abre no programa do Windows (alterações não voltam ao servidor).</summary>
    private async Task OpenRemoteFileAsync(string path)
    {
        try
        {
            if (!TryGetRemoteConnection(path, out var connection, out var error))
            {
                ShowRemoteError(error, "Abrir");
                return;
            }

            var result = await _remoteFiles.DownloadForOpenAsync(connection, path).ConfigureAwait(true);
            if (!result.Success || result.LocalPath is null)
            {
                ShowRemoteError(result.ErrorMessage ?? "Não foi possível baixar o arquivo.", "Abrir");
                return;
            }

            if (!FileOpenService.TryOpen(result.LocalPath, out var openError))
            {
                ShowRemoteError(openError, "Abrir");
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao abrir o arquivo remoto {path}", ex);
            ShowRemoteError(ex.Message, "Abrir");
        }
    }

    // ---- Copiar e mover ---------------------------------------------------------------------------

    private async Task<(bool Success, string? ErrorMessage)> TransferRemoteAsync(
        string sourcePath,
        string destinationDirectory,
        bool move,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (ArchivePathHelper.IsVirtualArchivePath(sourcePath) || ArchivePathHelper.IsVirtualArchivePath(destinationDirectory))
        {
            return (false, "Não é possível copiar entre um arquivo compactado e um servidor. Extraia o arquivo primeiro.");
        }

        var sourceRemote = IsRemotePath(sourcePath);
        var destinationRemote = IsRemotePath(destinationDirectory);
        NetworkConnectionInfo? source = null;
        NetworkConnectionInfo? destination = null;

        if (sourceRemote)
        {
            if (!TryGetRemoteConnection(sourcePath, out var connection, out var error))
            {
                return (false, error);
            }

            source = connection;
        }

        if (destinationRemote)
        {
            if (!TryGetRemoteConnection(destinationDirectory, out var connection, out var error))
            {
                return (false, error);
            }

            destination = connection;
        }

        (bool Success, string? ErrorMessage) result;
        if (source is not null && destination is not null)
        {
            result = await _remoteFiles
                .TransferBetweenAsync(source, sourcePath, destination, destinationDirectory, move, progress, cancellationToken)
                .ConfigureAwait(true);
        }
        else if (source is not null)
        {
            var downloaded = await _remoteFiles
                .DownloadAsync(source, sourcePath, destinationDirectory, progress, cancellationToken)
                .ConfigureAwait(true);
            result = (downloaded.Success, downloaded.ErrorMessage);
            if (result.Success && move)
            {
                result = await _remoteFiles.DeleteAsync(source, [sourcePath], cancellationToken).ConfigureAwait(true);
            }
        }
        else
        {
            result = await _remoteFiles
                .UploadAsync(destination!, sourcePath, destinationDirectory, progress, cancellationToken)
                .ConfigureAwait(true);
            if (result.Success && move)
            {
                result = await Task.Run(
                        () =>
                        {
                            var deleted = _fileOperationService.TryDelete([sourcePath], FileDeleteMode.Permanent, out var deleteError);
                            return (deleted, deleteError);
                        },
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }

        // Mesmo com falha ou cancelamento, parte dos itens pode já ter sido transferida.
        RefreshDirectoryIfShown(destinationDirectory);
        if (move)
        {
            var sourceParent = sourceRemote ? RemoteParentUri(sourcePath) : Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(sourceParent))
            {
                RefreshDirectoryIfShown(sourceParent);
            }
        }

        return result;
    }

    // ---- Compactar -------------------------------------------------------------------------------

    /// <summary>
    /// Compactar com servidor no meio: itens remotos são baixados para uma pasta temporária antes de compactar e, se o
    /// destino é um servidor, o arquivo compactado é criado numa pasta temporária e enviado. O andamento mostra uma etapa por vez.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> CompressWithRemoteAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        ArchiveFormat format,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), "DoraExplorer", "compress-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(temp, "origem");
        Directory.CreateDirectory(staging);

        try
        {
            var localSources = new List<string>();
            foreach (var path in sourcePaths)
            {
                if (ArchivePathHelper.IsVirtualArchivePath(path))
                {
                    return (false, "Não é possível compactar itens de dentro de um arquivo compactado.");
                }

                if (!IsRemotePath(path))
                {
                    localSources.Add(path);
                    continue;
                }

                if (!TryGetRemoteConnection(path, out var connection, out var error))
                {
                    return (false, error);
                }

                var downloaded = await _remoteFiles.DownloadAsync(connection, path, staging, progress, cancellationToken).ConfigureAwait(true);
                if (!downloaded.Success || downloaded.LocalPath is null)
                {
                    return (false, downloaded.ErrorMessage);
                }

                localSources.Add(downloaded.LocalPath);
            }

            if (!IsRemotePath(destinationDirectory))
            {
                var local = await _fileOperationService
                    .TryCompressAsync(localSources, destinationDirectory, format, progress, cancellationToken)
                    .ConfigureAwait(true);
                RefreshDirectoryIfShown(destinationDirectory);
                return (local.Success, local.ErrorMessage);
            }

            var archiveFolder = Path.Combine(temp, "arquivo");
            Directory.CreateDirectory(archiveFolder);
            var compressed = await _fileOperationService
                .TryCompressAsync(localSources, archiveFolder, format, progress, cancellationToken)
                .ConfigureAwait(true);
            if (!compressed.Success || compressed.ArchivePath is null)
            {
                return (false, compressed.ErrorMessage);
            }

            if (!TryGetRemoteConnection(destinationDirectory, out var destination, out var destinationError))
            {
                return (false, destinationError);
            }

            var uploaded = await _remoteFiles
                .UploadAsync(destination, compressed.ArchivePath, destinationDirectory, progress, cancellationToken)
                .ConfigureAwait(true);
            RefreshDirectoryIfShown(destinationDirectory);
            return uploaded;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                // Limpeza de melhor esforço da pasta temporária.
            }
        }
    }
}
