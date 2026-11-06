using System.IO;
using System.Net.Sockets;
using DoraExplorer.App.Models;
using SMBLibrary;
using SMBLibrary.Client;

namespace DoraExplorer.App.Services;

/// <summary>
/// Sessão SMB 2/3 (SMBLibrary) para o gerenciador de arquivos. O caminho do programa tem o compartilhamento como
/// primeira parte ("/Fotos/2024/a.jpg"): "/" lista os compartilhamentos do servidor e cada um é aberto ("tree connect")
/// só quando preciso. Falhas voltam como <see cref="IOException"/> com texto em português.
/// </summary>
internal sealed class SmbRemoteSession : IRemoteSession
{
    private const int ResponseTimeoutMilliseconds = 30_000;

    private readonly SMB2Client _client;
    private readonly Dictionary<string, ISMBFileStore?> _stores = new(StringComparer.OrdinalIgnoreCase);

    private SmbRemoteSession(SMB2Client client) => _client = client;

    public static SmbRemoteSession Open(NetworkConnectionInfo connection)
    {
        var port = connection.Port > 0 ? connection.Port : SmbLibraryAccess.StandardPort;
        var client = new SMB2Client(ResponseTimeoutMilliseconds);
        try
        {
            bool connected;
            try
            {
                connected = SmbLibraryAccess.Connect(client, SmbLibraryAccess.Resolve(connection.Host), port);
            }
            catch (SocketException ex)
            {
                throw new IOException($"Não foi possível ligar a {connection.Host}:{port}: {ex.Message}", ex);
            }

            if (!connected)
            {
                throw new IOException($"Não foi possível ligar a {connection.Host}:{port}. Confira o endereço, a porta e se o servidor aceita SMB 2 ou 3.");
            }

            var (domain, user) = SplitAccount(connection.Username);
            var status = client.Login(domain, user, connection.Password ?? string.Empty);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"Não foi possível entrar em {connection.Host}: {SmbLibraryAccess.Describe(status)}.");
            }

            return new SmbRemoteSession(client);
        }
        catch
        {
            TryDisconnect(client);
            throw;
        }
    }

    /// <summary>"DOMINIO\usuario" separa o domínio; "usuario" e "usuario@dominio.com" ficam como estão, sem domínio.</summary>
    internal static (string Domain, string User) SplitAccount(string? account)
    {
        var text = account?.Trim() ?? string.Empty;
        var slash = text.IndexOf('\\');
        return slash > 0 ? (text[..slash], text[(slash + 1)..]) : (string.Empty, text);
    }

    public RemoteEntry? Stat(string remotePath)
    {
        var (share, path) = SplitShare(remotePath);
        if (share is null)
        {
            return new RemoteEntry("/", true, 0);
        }

        var store = Store(share, throwIfMissing: false);
        if (store is null)
        {
            return null;
        }

        if (path.Length == 0)
        {
            return new RemoteEntry(share, true, 0);
        }

        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, 0, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (IsNotFound(status))
            {
                return null;
            }

            throw Failure("Consultar o item", status);
        }

        try
        {
            status = store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation);
            if (status != NTStatus.STATUS_SUCCESS || information is not FileNetworkOpenInformation open)
            {
                throw Failure("Consultar o item", status);
            }

            var isDirectory = (open.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
            return new RemoteEntry(
                LastSegment(path),
                isDirectory,
                isDirectory ? 0 : open.EndOfFile,
                (open.FileAttributes & SMBLibrary.FileAttributes.ReparsePoint) != 0,
                ToLocal(open.LastWriteTime));
        }
        finally
        {
            _ = store.CloseFile(handle);
        }
    }

    public IReadOnlyList<RemoteEntry> List(string remoteDirectory)
    {
        var (share, path) = SplitShare(remoteDirectory);
        if (share is null)
        {
            var shares = _client.ListShares(out var listStatus);
            if (listStatus != NTStatus.STATUS_SUCCESS)
            {
                throw Failure("Listar os compartilhamentos", listStatus);
            }

            // Os compartilhamentos administrativos (C$, IPC$, ADMIN$) terminam em "$" e não são para navegar.
            return shares
                .Where(name => !name.EndsWith('$'))
                .Select(name => new RemoteEntry(name, true, 0))
                .ToList();
        }

        var store = Store(share, throwIfMissing: true)!;
        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure("Abrir a pasta", status);
        }

        try
        {
            status = store.QueryDirectory(out var listing, handle, "*", FileInformationClass.FileDirectoryInformation);
            if (status is NTStatus.STATUS_NO_SUCH_FILE)
            {
                return []; // pasta sem nada dentro, nem "." e ".."
            }

            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
            {
                throw Failure("Listar a pasta", status);
            }

            return listing
                .OfType<FileDirectoryInformation>()
                .Where(item => item.FileName is not ("." or ".."))
                .Select(item =>
                {
                    var isDirectory = (item.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
                    return new RemoteEntry(
                        item.FileName,
                        isDirectory,
                        isDirectory ? 0 : item.EndOfFile,
                        (item.FileAttributes & SMBLibrary.FileAttributes.ReparsePoint) != 0,
                        ToLocal(item.LastWriteTime));
                })
                .ToList();
        }
        finally
        {
            _ = store.CloseFile(handle);
        }
    }

    public void CreateDirectory(string remoteDirectory)
    {
        var (store, path) = ItemStore(remoteDirectory);
        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.GENERIC_WRITE, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure($"Criar a pasta \"{LastSegment(path)}\"", status);
        }

        _ = store.CloseFile(handle);
    }

    public void DeleteFile(string remotePath) => Delete(remotePath, directory: false);

    public void DeleteEmptyDirectory(string remoteDirectory) => Delete(remoteDirectory, directory: true);

    private void Delete(string remotePath, bool directory)
    {
        var (store, path) = ItemStore(remotePath);
        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.DELETE | AccessMask.SYNCHRONIZE,
            directory ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
            (directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE)
            | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure($"Apagar \"{LastSegment(path)}\"", status);
        }

        try
        {
            status = store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw Failure($"Apagar \"{LastSegment(path)}\"", status);
            }
        }
        finally
        {
            _ = store.CloseFile(handle); // o servidor apaga ao fechar
        }
    }

    public void Rename(string fromPath, string toPath)
    {
        var (store, from) = ItemStore(fromPath);
        var (toShare, to) = SplitShare(toPath);
        if (!string.Equals(toShare, SplitShare(fromPath).Share, StringComparison.OrdinalIgnoreCase) || to.Length == 0)
        {
            throw new IOException("Só dá para renomear dentro do mesmo compartilhamento.");
        }

        var status = store.CreateFile(
            out var handle, out _, from, AccessMask.DELETE | AccessMask.SYNCHRONIZE,
            SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure($"Renomear \"{LastSegment(from)}\"", status);
        }

        try
        {
            status = store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = to, ReplaceIfExists = false });
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw Failure($"Renomear \"{LastSegment(from)}\"", status);
            }
        }
        finally
        {
            _ = store.CloseFile(handle);
        }
    }

    public void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken)
    {
        var (store, path) = ItemStore(remotePath);
        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure($"Abrir \"{LastSegment(path)}\"", status);
        }

        try
        {
            var block = (int)Math.Min(store.MaxReadSize, 1u << 20);
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status = store.ReadFile(out var data, handle, offset, block);
                if (status == NTStatus.STATUS_END_OF_FILE)
                {
                    break;
                }

                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw Failure($"Ler \"{LastSegment(path)}\"", status);
                }

                if (data is null || data.Length == 0)
                {
                    break;
                }

                destination.Write(data, 0, data.Length);
                offset += data.Length;
                progress(offset);
            }
        }
        finally
        {
            _ = store.CloseFile(handle);
        }
    }

    public void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, path) = ItemStore(remotePath);
        var status = store.CreateFile(
            out var handle, out _, path, AccessMask.GENERIC_WRITE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw Failure($"Criar \"{LastSegment(path)}\"", status);
        }

        try
        {
            var buffer = new byte[(int)Math.Min(store.MaxWriteSize, 1u << 20)];
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                var chunk = read == buffer.Length ? buffer : buffer[..read];
                status = store.WriteFile(out var written, handle, offset, chunk);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw Failure($"Gravar \"{LastSegment(path)}\"", status);
                }

                if (written <= 0)
                {
                    throw new IOException("O servidor não aceitou os dados enviados.");
                }

                // O servidor pode aceitar menos do que foi enviado: volta a fonte para o que faltou.
                if (written < read)
                {
                    source.Seek(written - read, SeekOrigin.Current);
                }

                offset += written;
                progress(offset);
            }
        }
        finally
        {
            _ = store.CloseFile(handle);
        }
    }

    public void Dispose()
    {
        foreach (var store in _stores.Values)
        {
            if (store is not null)
            {
                try
                {
                    _ = store.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException)
                {
                    // a conexão já caiu
                }
            }
        }

        TryDisconnect(_client);
    }

    private static void TryDisconnect(SMB2Client client)
    {
        try
        {
            if (client.IsConnected)
            {
                _ = client.Logoff();
                client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            // já desligado
        }
    }

    // ---- Caminhos ----------------------------------------------------------------------------

    private (ISMBFileStore Store, string Path) ItemStore(string remotePath)
    {
        var (share, path) = SplitShare(remotePath);
        if (share is null || path.Length == 0)
        {
            throw new IOException("Escolha uma pasta dentro de um compartilhamento do servidor.");
        }

        return (Store(share, throwIfMissing: true)!, path);
    }

    private ISMBFileStore? Store(string share, bool throwIfMissing)
    {
        if (_stores.TryGetValue(share, out var known))
        {
            return known ?? (throwIfMissing ? throw MissingShare(share) : null);
        }

        var store = _client.TreeConnect(share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            // Windows e Samba respondem "nome de rede inválido"; a SMBLibrary responde "não encontrado".
            if (IsNotFound(status))
            {
                _stores[share] = null;
                return throwIfMissing ? throw MissingShare(share) : null;
            }

            throw Failure($"Abrir o compartilhamento \"{share}\"", status);
        }

        _stores[share] = store;
        return store;
    }

    private static IOException MissingShare(string share) =>
        new($"O compartilhamento \"{share}\" não existe neste servidor.");

    /// <summary>Divide "/Fotos/2024/a.jpg" em ("Fotos", "2024\a.jpg"); a raiz devolve compartilhamento nulo.</summary>
    internal static (string? Share, string Path) SplitShare(string remotePath)
    {
        var segments = (remotePath ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? (null, string.Empty) : (segments[0], string.Join('\\', segments.Skip(1)));
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static bool IsNotFound(NTStatus status) =>
        status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            or NTStatus.STATUS_NO_SUCH_FILE or NTStatus.STATUS_BAD_NETWORK_NAME;

    private static DateTime? ToLocal(DateTime? time) =>
        time is { } value ? (value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value) : null;

    private static DateTime ToLocal(DateTime time) => time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;

    private static IOException Failure(string operation, NTStatus status) =>
        new($"{operation}: {SmbLibraryAccess.Describe(status)}.");
}
