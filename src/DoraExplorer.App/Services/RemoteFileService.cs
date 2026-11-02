using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Operações em pastas de servidores FTP/SFTP/SCP: cada chamada abre uma sessão, trabalha e a descarta. Transferências
/// mostram andamento por bytes e só expiram se nada andar por 2 minutos; cancelar derruba a sessão (o que interrompe a
/// transferência na hora) e o que ficou pela metade é apagado.
/// </summary>
public sealed class RemoteFileService : IRemoteFileService
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);

    private readonly Func<NetworkConnectionInfo, IRemoteSession> _open;

    public RemoteFileService()
        : this(RemoteSessionFactory.Open)
    {
    }

    internal RemoteFileService(Func<NetworkConnectionInfo, IRemoteSession> open) => _open = open;

    public async Task<(bool Success, string? ErrorMessage)> CreateDirectoryAsync(
        NetworkConnectionInfo connection,
        string remoteDirectory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            connection,
            (session, _, _) =>
            {
                var path = ToRemotePath(remoteDirectory);
                if (session.Stat(path) is not null)
                {
                    throw new IOException($"Já existe um item chamado '{RemoteTransferEngine.LastSegment(path)}' nesta pasta.");
                }

                session.CreateDirectory(path);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.ErrorMessage);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteAsync(
        NetworkConnectionInfo connection,
        IReadOnlyList<string> remotePaths,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            connection,
            (session, token, keepAlive) =>
            {
                foreach (var path in remotePaths)
                {
                    RemoteTransferEngine.Delete(session, ToRemotePath(path), keepAlive, token);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.ErrorMessage);
    }

    public async Task<(bool Success, string? NewPath, string? ErrorMessage)> RenameAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            connection,
            (session, _, _) =>
            {
                var target = RemoteTransferEngine.Rename(session, ToRemotePath(remotePath), newName);
                return NetworkFileService.BuildRemotePath(connection.Protocol, connection.Host, target);
            },
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.Value, result.ErrorMessage);
    }

    public async Task<(bool Success, string? LocalPath, string? ErrorMessage)> DownloadAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        string localDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            connection,
            (session, token, keepAlive) =>
            {
                var tracker = new TransferProgressTracker(progress, keepAlive);
                var path = ToRemotePath(remotePath);
                if (tracker.WantsTotals)
                {
                    var (bytes, files) = RemoteTransferEngine.Scan(session, path, keepAlive, token);
                    tracker.SetTotals(bytes, files);
                }

                var local = RemoteTransferEngine.Download(session, path, localDirectory, tracker, token);
                tracker.Complete();
                AppLogService.Info($"Baixado: {remotePath} -> {local}");
                return local;
            },
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.Value, result.ErrorMessage);
    }

    public async Task<(bool Success, string? ErrorMessage)> UploadAsync(
        NetworkConnectionInfo connection,
        string localPath,
        string remoteDirectory,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        string? target = null;
        var result = await RunAsync(
            connection,
            (session, token, keepAlive) =>
            {
                try
                {
                    var tracker = new TransferProgressTracker(progress, keepAlive);
                    if (tracker.WantsTotals)
                    {
                        var (bytes, files) = TransferScanner.ScanItem(localPath, keepAlive, token);
                        tracker.SetTotals(bytes, files);
                    }

                    var remote = RemoteTransferEngine.Upload(
                        session, localPath, ToRemotePath(remoteDirectory), tracker, chosen => target = chosen, token);
                    tracker.Complete();
                    AppLogService.Info($"Enviado: {localPath} -> {remote}");
                    return true;
                }
                catch (Exception)
                {
                    CleanUpRemote(connection, target);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.ErrorMessage);
    }

    public async Task<(bool Success, string? ErrorMessage)> TransferBetweenAsync(
        NetworkConnectionInfo source,
        string sourcePath,
        NetworkConnectionInfo destination,
        string destinationDirectory,
        bool move,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (move && SameAccount(source, destination))
        {
            var renamed = await RunAsync(
                source,
                (session, _, _) =>
                {
                    var from = ToRemotePath(sourcePath);
                    var directory = ToRemotePath(destinationDirectory);
                    var name = RemoteTransferEngine.UniqueName(
                        candidate => session.Stat(RemoteTransferEngine.Combine(directory, candidate)) is not null,
                        RemoteTransferEngine.SafeName(RemoteTransferEngine.LastSegment(from)));
                    session.Rename(from, RemoteTransferEngine.Combine(directory, name));
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (renamed.Success || cancellationToken.IsCancellationRequested)
            {
                return (renamed.Success, renamed.ErrorMessage);
            }

            AppLogService.Warning($"Mover no próprio servidor falhou ({renamed.ErrorMessage}); copiando e apagando.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "DoraExplorer", "transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var downloaded = await DownloadAsync(source, sourcePath, temp, progress, cancellationToken).ConfigureAwait(false);
            if (!downloaded.Success || downloaded.LocalPath is null)
            {
                return (false, downloaded.ErrorMessage);
            }

            var uploaded = await UploadAsync(destination, downloaded.LocalPath, destinationDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!uploaded.Success || !move)
            {
                return uploaded;
            }

            return await DeleteAsync(source, [sourcePath], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteLocalQuietly(temp);
        }
    }

    public async Task<(bool Success, string? LocalPath, string? ErrorMessage)> DownloadForOpenAsync(
        NetworkConnectionInfo connection,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "DoraExplorer", "remote");
        CleanStaleTempFolders(root);

        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var downloaded = await DownloadAsync(connection, remotePath, folder, progress: null, cancellationToken).ConfigureAwait(false);
        if (!downloaded.Success)
        {
            DeleteLocalQuietly(folder);
        }

        return downloaded;
    }

    /// <summary>Caminho no servidor: o de um item da árvore (<c>dora-ftp://host/a/b</c>) ou um caminho simples.</summary>
    internal static string ToRemotePath(string path) =>
        NetworkFileService.TryParseRemotePath(path, out _, out _, out var remote) ? remote : path.Replace('\\', '/');

    internal static bool SameAccount(NetworkConnectionInfo first, NetworkConnectionInfo second) =>
        first.Protocol == second.Protocol &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port &&
        string.Equals(first.Username, second.Username, StringComparison.Ordinal);

    private async Task<(bool Success, T? Value, string? ErrorMessage)> RunAsync<T>(
        NetworkConnectionInfo connection,
        Func<IRemoteSession, CancellationToken, Action, T> work,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await OperationTimeouts.RunWithInactivityAsync(
                (token, keepAlive) =>
                {
                    using var session = _open(connection);

                    // Derrubar a sessão é o jeito de interromper uma chamada de rede que está esperando o servidor.
                    using var interrupt = token.Register(session.Dispose);
                    keepAlive();
                    try
                    {
                        return work(session, token, keepAlive);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }
                },
                InactivityTimeout,
                cancellationToken).ConfigureAwait(false);
            return (true, value, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, default, $"A operação parou de responder: nenhum dado foi processado por {InactivityTimeout.TotalMinutes:0} minutos.");
        }
        catch (OperationCanceledException)
        {
            return (false, default, FileOperationService.CancelledMessage);
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha na operação remota {connection.Protocol} em {connection.Host}", ex);
            return (false, default, ex.Message);
        }
    }

    /// <summary>Apaga um envio que ficou pela metade, numa sessão nova (a anterior pode ter sido derrubada).</summary>
    private void CleanUpRemote(NetworkConnectionInfo connection, string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return;
        }

        try
        {
            using var session = _open(connection);
            RemoteTransferEngine.Delete(session, remotePath, heartbeat: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Não foi possível apagar o envio incompleto '{remotePath}': {ex.Message}");
        }
    }

    private static void CleanStaleTempFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleTempAge)
                {
                    DeleteLocalQuietly(directory);
                }
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            // Limpeza de melhor esforço.
        }
    }

    private static void DeleteLocalQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            // Limpeza de melhor esforço.
        }
    }
}
