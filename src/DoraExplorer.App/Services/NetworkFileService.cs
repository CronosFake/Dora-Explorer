using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using FluentFTP;
using Renci.SshNet;

namespace DoraExplorer.App.Services;

public sealed class NetworkFileService : INetworkFileService
{
    private static readonly TimeSpan SshConnectTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SshOperationTimeout = TimeSpan.FromSeconds(30);

    public bool TryConnectAndList(NetworkConnectionInfo connection, out IReadOnlyList<FileSystemEntry> entries, out string? errorMessage)
    {
        entries = [];
        errorMessage = null;

        try
        {
            entries = connection.Protocol switch
            {
                NetworkProtocol.Ftp => ListFtp(connection),
                NetworkProtocol.Sftp => ListSftp(connection),
                NetworkProtocol.Scp => ListScpDirectory(connection),
                NetworkProtocol.Tftp => [],
                _ => []
            };

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            AppLogService.Error($"Falha na ligação {connection.Protocol} a {connection.Host}", ex);
            return false;
        }
    }

    public bool TryExecuteTransfer(NetworkConnectionInfo connection, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            switch (connection.Protocol)
            {
                case NetworkProtocol.Scp:
                    ExecuteScpTransfer(connection);
                    break;
                case NetworkProtocol.Tftp:
                    ExecuteTftpTransfer(connection);
                    break;
                default:
                    errorMessage = "Use a navegação na árvore para transferências FTP/SFTP.";
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            AppLogService.Error($"Falha na transferência {connection.Protocol}", ex);
            return false;
        }
    }

    public async Task<(bool Success, IReadOnlyList<FileSystemEntry> Entries, string? ErrorMessage)> ConnectAndListAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await OperationTimeouts.RunAsync<IReadOnlyList<FileSystemEntry>>(
                _ => connection.Protocol switch
                {
                    NetworkProtocol.Ftp => ListFtp(connection),
                    NetworkProtocol.Sftp => ListSftp(connection),
                    NetworkProtocol.Scp => ListScpDirectory(connection),
                    NetworkProtocol.Tftp => Array.Empty<FileSystemEntry>(),
                    _ => Array.Empty<FileSystemEntry>()
                },
                OperationTimeouts.Network,
                cancellationToken).ConfigureAwait(false);

            return (true, entries, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, [], "Tempo limite da ligação de rede excedido.");
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha na ligação {connection.Protocol} a {connection.Host}", ex);
            return (false, [], ex.Message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> ExecuteTransferAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await OperationTimeouts.RunAsync(
                token =>
                {
                    switch (connection.Protocol)
                    {
                        case NetworkProtocol.Scp:
                            ExecuteScpTransfer(connection, token);
                            break;
                        case NetworkProtocol.Tftp:
                            ExecuteTftpTransfer(connection, null, token);
                            break;
                        default:
                            throw new InvalidOperationException("Use a navegação na árvore para transferências FTP/SFTP.");
                    }

                    return true;
                },
                OperationTimeouts.FileTransfer,
                cancellationToken).ConfigureAwait(false);

            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Tempo limite da transferência excedido.");
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha na transferência {connection.Protocol}", ex);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> TestConnectionAsync(
        NetworkConnectionInfo connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await OperationTimeouts.RunAsync(
                _ =>
                {
                    TestConnection(connection);
                    return true;
                },
                OperationTimeouts.Network,
                cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Tempo limite do teste de ligação excedido.");
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha no teste {connection.Protocol} a {connection.Host}", ex);
            return (false, ex.Message);
        }
    }

    public static string BuildRemotePath(NetworkProtocol protocol, string host, string remotePath)
    {
        var normalized = NormalizeRemotePath(remotePath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encodedPath = segments.Length == 0
            ? "/"
            : "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
        return $"dora-{protocol.ToString().ToLowerInvariant()}://{host}{encodedPath}";
    }

    public static bool TryParseRemotePath(string path, out NetworkProtocol protocol, out string host, out string remotePath)
    {
        protocol = default;
        host = string.Empty;
        remotePath = "/";

        if (!path.StartsWith("dora-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // FTP é o valor 0 do enum (o "default"): não dá para usar default como sinal de "esquema desconhecido".
        NetworkProtocol? parsed = uri.Scheme switch
        {
            "dora-ftp" => NetworkProtocol.Ftp,
            "dora-sftp" => NetworkProtocol.Sftp,
            "dora-scp" => NetworkProtocol.Scp,
            "dora-tftp" => NetworkProtocol.Tftp,
            _ => null
        };

        if (parsed is null)
        {
            return false;
        }

        protocol = parsed.Value;

        host = uri.Host;
        // AbsolutePath perde o caminho se o URI foi mal formado; UnescapeDataString mantém espaços.
        var absolute = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : Uri.UnescapeDataString(uri.AbsolutePath);
        remotePath = NormalizeRemotePath(absolute);
        return true;
    }

    private static IReadOnlyList<FileSystemEntry> ListFtp(NetworkConnectionInfo connection)
    {
        using var client = CreateFtpClient(connection);
        client.Connect();
        try
        {
            var remotePath = NormalizeRemotePath(connection.RemotePath);
            var listing = GetFtpListing(client, remotePath);

            return listing
                .Where(item => item.Name is not "." and not "..")
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    var name = GetFtpEntryName(item.Name);
                    return new FileSystemEntry
                    {
                        Name = name,
                        FullPath = BuildRemotePath(NetworkProtocol.Ftp, connection.Host, CombineRemote(remotePath, name)),
                        IsDirectory = item.Type == FtpObjectType.Directory,
                        SizeInBytes = item.Type == FtpObjectType.File ? item.Size : null,
                        LastModified = item.Modified
                    };
                })
                .ToList();
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private static FtpListItem[] GetFtpListing(FtpClient client, string remotePath)
    {
        // Auto usa MLSD quando o servidor oferece (datas e tipos exatos) e, senão, LIST reconhecendo sozinho
        // o formato (Unix, Windows/IIS...). CWD + listar a pasta atual é o caminho mais compatível.
        const FtpListOption listOptions = FtpListOption.Auto;

        try
        {
            var target = remotePath is "/" or "." or "" ? "/" : remotePath;
            client.SetWorkingDirectory(target);
            var listing = client.GetListing(null, listOptions);
            if (listing.Length > 0)
            {
                return listing;
            }

            // Pasta vazia é válida — não tentar fallback que possa listar outra pasta.
            return listing;
        }
        catch (Exception first)
        {
            AppLogService.Warning($"FTP CWD/LIST('{remotePath}') falhou, a tentar GetListing direto: {first.Message}");
        }

        try
        {
            return client.GetListing(remotePath, listOptions);
        }
        catch (Exception second)
        {
            throw new IOException(
                $"Não foi possível listar '{remotePath}': {second.Message}",
                second);
        }
    }

    private static IReadOnlyList<FileSystemEntry> ListSftp(NetworkConnectionInfo connection)
    {
        using var client = CreateSftpClient(connection);
        ConnectSsh(client, connection);
        try
        {
            var remotePath = NormalizeRemotePath(connection.RemotePath);
            return client.ListDirectory(remotePath)
                .Where(item => item.Name is not "." and not "..")
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FileSystemEntry
                {
                    Name = item.Name,
                    FullPath = BuildRemotePath(NetworkProtocol.Sftp, connection.Host, CombineRemote(remotePath, item.Name)),
                    IsDirectory = item.IsDirectory,
                    SizeInBytes = item.IsDirectory ? null : item.Length,
                    LastModified = item.LastWriteTime
                })
                .ToList();
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private static IReadOnlyList<FileSystemEntry> ListScpDirectory(NetworkConnectionInfo connection)
    {
        using var client = CreateSftpClient(connection);
        ConnectSsh(client, connection);
        try
        {
            var remotePath = NormalizeRemotePath(connection.RemotePath);
            return client.ListDirectory(remotePath)
                .Where(item => item.Name is not "." and not "..")
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FileSystemEntry
                {
                    Name = item.Name,
                    FullPath = BuildRemotePath(NetworkProtocol.Scp, connection.Host, CombineRemote(remotePath, item.Name)),
                    IsDirectory = item.IsDirectory,
                    SizeInBytes = item.IsDirectory ? null : item.Length,
                    LastModified = item.LastWriteTime
                })
                .ToList();
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private static void TestConnection(NetworkConnectionInfo connection)
    {
        switch (connection.Protocol)
        {
            case NetworkProtocol.Ftp:
                using (var client = CreateFtpClient(connection))
                {
                    client.Connect();
                    try
                    {
                        _ = client.GetWorkingDirectory();
                    }
                    finally
                    {
                        if (client.IsConnected)
                        {
                            client.Disconnect();
                        }
                    }
                }

                break;

            case NetworkProtocol.Sftp:
            case NetworkProtocol.Scp:
                using (var client = CreateSftpClient(connection))
                {
                    ConnectSsh(client, connection);
                    try
                    {
                        _ = client.WorkingDirectory;
                    }
                    finally
                    {
                        if (client.IsConnected)
                        {
                            client.Disconnect();
                        }
                    }
                }

                break;

            case NetworkProtocol.Tftp:
                // TFTP não tem sessão autenticada: pede um ficheiro inexistente e espera qualquer resposta do servidor.
                TftpClient.Probe(connection.Host, connection.Port > 0 ? connection.Port : 69, TftpClientOptions.Probe);
                break;

            default:
                throw new InvalidOperationException("Protocolo não suportado.");
        }
    }

    private static void ExecuteScpTransfer(NetworkConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connection.LocalFilePath) || string.IsNullOrWhiteSpace(connection.RemoteFilePath))
        {
            throw new InvalidOperationException("Indique o ficheiro local e remoto para SCP.");
        }

        using var client = CreateScpClient(connection);
        ConnectSsh(client, connection);

        // Cancelar (ou estourar o tempo) fecha a sessão, o que interrompe a transferência em curso.
        using var cancellation = cancellationToken.Register(client.Dispose);

        if (connection.IsUpload)
        {
            using var stream = File.OpenRead(connection.LocalFilePath);
            client.Upload(stream, connection.RemoteFilePath);
            return;
        }

        // Baixa para um temporário: uma falha a meio não destrói o ficheiro local que já existia.
        var tempPath = connection.LocalFilePath + ".dora-part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = File.Create(tempPath))
            {
                client.Download(connection.RemoteFilePath, stream);
            }

            File.Move(tempPath, connection.LocalFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static void ExecuteTftpTransfer(
        NetworkConnectionInfo connection,
        TftpClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connection.LocalFilePath) || string.IsNullOrWhiteSpace(connection.RemoteFilePath))
        {
            throw new InvalidOperationException("Indique o ficheiro local e remoto para TFTP.");
        }

        var port = connection.Port > 0 ? connection.Port : 69;
        options ??= TftpClientOptions.Default;

        if (connection.IsUpload)
        {
            TftpClient.Upload(connection.Host, port, connection.RemoteFilePath, connection.LocalFilePath, options, cancellationToken);
        }
        else
        {
            TftpClient.Download(connection.Host, port, connection.RemoteFilePath, connection.LocalFilePath, options, cancellationToken);
        }
    }
    internal static FtpClient CreateFtpClient(NetworkConnectionInfo connection)
    {
        var client = new FtpClient(
            connection.Host,
            connection.Username ?? "anonymous",
            connection.Password ?? string.Empty,
            connection.Port > 0 ? connection.Port : 21);
        client.Config.EncryptionMode = connection.ClientSecurity switch
        {
            FtpClientSecurity.None => FtpEncryptionMode.None,
            FtpClientSecurity.Explicit => FtpEncryptionMode.Explicit,
            FtpClientSecurity.Implicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.Auto // como os clientes de mercado: FTPS explícito se o servidor oferecer
        };
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 15000;

        // MLSD e MDTM informam a hora em UTC (RFC 3659): converte para a hora local, como no resto do programa.
        client.Config.TimeZone = 0;
        client.Config.LocalTimeZone = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;
        client.Config.TimeConversion = FtpDate.LocalTime;

        if (client.Config.EncryptionMode != FtpEncryptionMode.None)
        {
            FtpCertificateVerifier.Attach(client, connection.Host, connection.Port > 0 ? connection.Port : 21);
        }

        return client;
    }

    // Verifica a chave do servidor antes de enviar credenciais (ver HostKeyVerifier).
    internal static void ConnectSsh(BaseClient client, NetworkConnectionInfo connection) =>
        HostKeyVerifier.Connect(client, connection.Host, connection.Port > 0 ? connection.Port : 22);

    internal static SftpClient CreateSftpClient(NetworkConnectionInfo connection)
    {
        var client = new SftpClient(CreateSshConnectionInfo(connection));

        // Os padrões do SSH.NET são 30 s para ligar e tempo INFINITO por operação: um servidor parado travava a listagem.
        client.OperationTimeout = SshOperationTimeout;
        return client;
    }

    private static ConnectionInfo CreateSshConnectionInfo(NetworkConnectionInfo connection) =>
        new(
            connection.Host,
            connection.Port > 0 ? connection.Port : 22,
            connection.Username ?? string.Empty,
            BuildSshAuthentication(connection))
        {
            Timeout = SshConnectTimeout
        };

    /// <summary>
    /// Métodos de autenticação na ordem dos clientes de mercado: chave privada (se informada), depois senha e
    /// "keyboard-interactive" (o que muitos servidores Linux usam para senha). Sem chave nem senha, tenta senha vazia.
    /// </summary>
    internal static AuthenticationMethod[] BuildSshAuthentication(NetworkConnectionInfo connection)
    {
        var user = connection.Username ?? string.Empty;
        var password = connection.Password ?? string.Empty;
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(connection.PrivateKeyPath))
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, LoadPrivateKey(connection)));
        }

        if (password.Length > 0 || methods.Count == 0)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));

            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(interactive);
        }

        return [.. methods];
    }

    private static PrivateKeyFile LoadPrivateKey(NetworkConnectionInfo connection)
    {
        var path = connection.PrivateKeyPath!.Trim();
        try
        {
            return string.IsNullOrEmpty(connection.PrivateKeyPassphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, connection.PrivateKeyPassphrase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or Renci.SshNet.Common.SshException or Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            throw new IOException($"Não foi possível usar a chave privada \"{path}\": {ex.Message}", ex);
        }
    }

    // ShellQuote escapa metacaracteres do caminho remoto (SSH.NET 2026: evita injeção de comando no SCP).
    private static ScpClient CreateScpClient(NetworkConnectionInfo connection)
    {
        // A duração da transferência é limitada por OperationTimeouts.FileTransfer, que fecha a sessão ao expirar.
        return new ScpClient(CreateSshConnectionInfo(connection), RemotePathTransformation.ShellQuote);
    }
    private static string GetFtpEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var normalized = name.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized == ".")
        {
            return "/";
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static string CombineRemote(string parent, string child)
    {
        if (parent.EndsWith('/'))
        {
            return parent + child;
        }

        return parent + "/" + child;
    }
}
