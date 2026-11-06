using DoraExplorer.App.Models;
using FluentFTP;
using Renci.SshNet;

namespace DoraExplorer.App.Services;

/// <summary>Item de uma pasta remota. Links simbólicos ficam de fora das cópias para não seguir ciclos.</summary>
internal sealed record RemoteEntry(string Name, bool IsDirectory, long Length, bool IsLink = false, DateTime? Modified = null);

/// <summary>
/// Uma conexão aberta com um servidor para operar em arquivos e pastas. Cada operação do usuário abre uma sessão,
/// faz o trabalho e a descarta (como a listagem); descartar a sessão também interrompe uma transferência em curso.
/// </summary>
internal interface IRemoteSession : IDisposable
{
    /// <summary>Informações do item, ou nulo se não existir.</summary>
    RemoteEntry? Stat(string remotePath);

    IReadOnlyList<RemoteEntry> List(string remoteDirectory);

    void CreateDirectory(string remoteDirectory);

    void DeleteFile(string remotePath);

    void DeleteEmptyDirectory(string remoteDirectory);

    void Rename(string fromPath, string toPath);

    /// <summary>Baixa o arquivo para <paramref name="destination"/>; <paramref name="progress"/> recebe os bytes já baixados.</summary>
    void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken);

    /// <summary>Envia o conteúdo de <paramref name="source"/>; <paramref name="progress"/> recebe os bytes já enviados.</summary>
    void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken);
}

internal static class RemoteSessionFactory
{
    /// <summary>
    /// Abre a sessão do protocolo da conexão. SFTP e SCP usam o subsistema SFTP para listar, criar, apagar e
    /// renomear (o SCP puro só copia arquivos); SMB e NFS operam em pastas compartilhadas de verdade; TFTP não
    /// tem nada disso e fica de fora.
    /// </summary>
    public static IRemoteSession Open(NetworkConnectionInfo connection) =>
        connection.Protocol switch
        {
            NetworkProtocol.Ftp => FtpRemoteSession.Open(connection),
            NetworkProtocol.Sftp or NetworkProtocol.Scp => SftpRemoteSession.Open(connection),
            NetworkProtocol.Smb => SmbRemoteSession.Open(connection),
            NetworkProtocol.Nfs => NfsRemoteSession.Open(connection),
            _ => throw new NotSupportedException($"O protocolo {connection.Protocol.ToString().ToUpperInvariant()} não permite operar em pastas remotas.")
        };
}

internal sealed class FtpRemoteSession(FtpClient client) : IRemoteSession
{
    public static FtpRemoteSession Open(NetworkConnectionInfo connection)
    {
        var client = NetworkFileService.CreateFtpClient(connection);
        try
        {
            client.Connect();
            return new FtpRemoteSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public RemoteEntry? Stat(string remotePath)
    {
        if (remotePath is "/" or "")
        {
            return new RemoteEntry("/", true, 0);
        }

        if (client.DirectoryExists(remotePath))
        {
            return new RemoteEntry(LastSegment(remotePath), true, 0);
        }

        if (client.FileExists(remotePath))
        {
            var size = client.GetFileSize(remotePath);
            return new RemoteEntry(LastSegment(remotePath), false, size < 0 ? 0 : size);
        }

        return null;
    }

    public IReadOnlyList<RemoteEntry> List(string remoteDirectory) =>
        client.GetListing(remoteDirectory, FtpListOption.Auto)
            .Select(item => new RemoteEntry(
                LastSegment(item.Name),
                item.Type == FtpObjectType.Directory,
                item.Type == FtpObjectType.File ? Math.Max(0, item.Size) : 0,
                item.Type == FtpObjectType.Link))
            .Where(entry => entry.Name is not ("." or ".." or ""))
            .ToList();

    public void CreateDirectory(string remoteDirectory)
    {
        if (!client.CreateDirectory(remoteDirectory))
        {
            throw new IOException($"Não foi possível criar a pasta '{remoteDirectory}' no servidor.");
        }
    }

    public void DeleteFile(string remotePath) => client.DeleteFile(remotePath);

    public void DeleteEmptyDirectory(string remoteDirectory) => client.DeleteDirectory(remoteDirectory);

    public void Rename(string fromPath, string toPath) => client.Rename(fromPath, toPath);

    public void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!client.DownloadStream(destination, remotePath, 0, p => progress(p.TransferredBytes)))
        {
            throw new IOException($"O servidor não enviou '{remotePath}'.");
        }
    }

    public void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = client.UploadStream(source, remotePath, FtpRemoteExists.Overwrite, false, p => progress(p.TransferredBytes));
        if (status == FtpStatus.Failed)
        {
            throw new IOException($"O servidor não recebeu '{remotePath}'.");
        }
    }

    public void Dispose() => client.Dispose();

    private static string LastSegment(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

}

internal sealed class SftpRemoteSession(SftpClient client) : IRemoteSession
{
    public static SftpRemoteSession Open(NetworkConnectionInfo connection)
    {
        var client = NetworkFileService.CreateSftpClient(connection);
        try
        {
            NetworkFileService.ConnectSsh(client, connection);
            return new SftpRemoteSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public RemoteEntry? Stat(string remotePath)
    {
        if (remotePath is "/" or "")
        {
            return new RemoteEntry("/", true, 0);
        }

        if (!client.Exists(remotePath))
        {
            return null;
        }

        var file = client.Get(remotePath);
        return new RemoteEntry(file.Name, file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.IsSymbolicLink);
    }

    public IReadOnlyList<RemoteEntry> List(string remoteDirectory) =>
        client.ListDirectory(remoteDirectory)
            .Where(item => item.Name is not ("." or ".."))
            .Select(item => new RemoteEntry(
                item.Name,
                item.IsDirectory && !item.IsSymbolicLink,
                item.IsDirectory ? 0 : item.Length,
                item.IsSymbolicLink))
            .ToList();

    public void CreateDirectory(string remoteDirectory) => client.CreateDirectory(remoteDirectory);

    public void DeleteFile(string remotePath) => client.DeleteFile(remotePath);

    public void DeleteEmptyDirectory(string remoteDirectory) => client.DeleteDirectory(remoteDirectory);

    public void Rename(string fromPath, string toPath) => client.RenameFile(fromPath, toPath);

    public void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        client.DownloadFile(remotePath, destination, done =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress((long)done);
        });
    }

    public void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        client.UploadFile(source, remotePath, true, done =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress((long)done);
        });
    }

    public void Dispose() => client.Dispose();
}
