using System.IO;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Hosts local FTP/SFTP/SCP/TFTP listeners for the center-toolbar REDE SERVER.
/// </summary>
public sealed class NetworkServerService : INetworkServerService
{
    private readonly object _sync = new();
    private readonly Dictionary<NetworkProtocol, IProtocolServer> _servers = new();
    private readonly NetworkServerLogStore _logs;

    private readonly string? _certificatePath;
    private readonly string? _hostKeyPath;

    public NetworkServerService(NetworkServerLogStore logs)
    {
        _logs = logs;
    }

    // Usado nos testes, para não criar o certificado FTPS nem a chave SSH na pasta real do usuário.
    internal NetworkServerService(NetworkServerLogStore logs, string certificatePath, string? hostKeyPath = null)
        : this(logs)
    {
        _certificatePath = certificatePath;
        _hostKeyPath = hostKeyPath;
    }

    public event EventHandler? StateChanged;

    public bool IsRunning(NetworkProtocol protocol)
    {
        lock (_sync)
        {
            return _servers.TryGetValue(protocol, out var server) && server.IsRunning;
        }
    }

    public NetworkConnectionInfo? GetRunningConfig(NetworkProtocol protocol)
    {
        lock (_sync)
        {
            return _servers.TryGetValue(protocol, out var server) ? server.Config : null;
        }
    }

    public bool TryStart(NetworkConnectionInfo config, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(config.Host))
        {
            errorMessage = "Selecione o endereço local do servidor.";
            return false;
        }

        if (config.Port is < 1 or > 65535)
        {
            errorMessage = "Porta inválida.";
            return false;
        }

        // Sem login, qualquer máquina da rede leria e gravaria na pasta compartilhada.
        if (config.Protocol is NetworkProtocol.Ftp or NetworkProtocol.Sftp or NetworkProtocol.Scp &&
            (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrEmpty(config.Password)))
        {
            errorMessage = $"O servidor {config.Protocol.ToString().ToUpperInvariant()} exige usuário e senha.";
            return false;
        }

        if (!TryResolveRootPath(config, out var rootPath, out errorMessage))
        {
            return false;
        }

        if (config.Protocol == NetworkProtocol.Ftp &&
            !PassivePortRange.TryParse(config.PassivePorts, out _, out errorMessage))
        {
            return false;
        }

        if (NetworkPortGuard.IsPortUsedByOtherProcess(config.Port, config.Protocol))
        {
            errorMessage = $"A porta {config.Port} já está em uso por outro processo.";
            return false;
        }

        lock (_sync)
        {
            if (_servers.TryGetValue(config.Protocol, out var existing) && existing.IsRunning)
            {
                errorMessage = $"O servidor {config.Protocol} já está em execução.";
                return false;
            }

            existing?.Dispose();
            _servers.Remove(config.Protocol);

            // Novo arranque: limpa o log deste servidor.
            _logs.Clear(config.Protocol);
            _logs.Append(config.Protocol, $"A iniciar em {config.Host}:{config.Port}");
            _logs.Append(config.Protocol, $"Pasta raiz: {rootPath}");

            IProtocolServer server = config.Protocol switch
            {
                NetworkProtocol.Ftp => new FtpServer(config, rootPath, _logs, _certificatePath),
                NetworkProtocol.Sftp or NetworkProtocol.Scp => new SshFileServer(config, rootPath, _logs, _hostKeyPath),
                NetworkProtocol.Tftp => new TftpServer(config, rootPath, _logs),
                _ => throw new ArgumentOutOfRangeException(nameof(config))
            };

            try
            {
                server.Start();
            }
            catch (Exception ex)
            {
                server.Dispose();
                errorMessage = ex.Message;
                _logs.Append(config.Protocol, $"ERRO ao iniciar: {ex.Message}");
                AppLogService.Error($"Falha ao iniciar servidor {config.Protocol}", ex);
                return false;
            }

            _servers[config.Protocol] = server;
        }

        _logs.Append(config.Protocol, "Servidor iniciado.");
        AppLogService.Info($"Servidor {config.Protocol} iniciado em {config.Host}:{config.Port} (raiz: {rootPath})");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void StopServer(NetworkProtocol protocol)
    {
        IProtocolServer? server;
        lock (_sync)
        {
            if (!_servers.Remove(protocol, out server))
            {
                _logs.Append(protocol, "Parar pedido, mas o servidor já não estava em execução.");
                return;
            }
        }

        var port = server.Config.Port;
        if (port > 0 && NetworkPortGuard.IsPortUsedByOtherProcess(port, protocol))
        {
            _logs.Append(protocol, $"A parar: porta {port} em uso por outro processo — apenas estado Dora limpo.");
            AppLogService.Info(
                $"Parar {protocol}: porta {port} em uso por outro processo — apenas o estado Dora será limpo.");
            server.DisposeWithoutClosingExternalPort();
        }
        else
        {
            server.Dispose();
            _logs.Append(protocol, "Servidor parado.");
            AppLogService.Info($"Servidor {protocol} parado.");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopAll()
    {
        List<NetworkProtocol> protocols;
        lock (_sync)
        {
            protocols = _servers.Keys.ToList();
        }

        foreach (var protocol in protocols)
        {
            StopServer(protocol);
        }
    }

    public void Dispose() => StopAll();

    private static bool TryResolveRootPath(NetworkConnectionInfo config, out string rootPath, out string? errorMessage)
    {
        rootPath = string.Empty;
        errorMessage = null;

        var path = config.RemotePath?.Trim();
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            errorMessage = "Indique a pasta raiz do servidor.";
            return false;
        }

        try
        {
            rootPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorMessage = $"Pasta raiz inválida: {path}";
            return false;
        }

        if (!Directory.Exists(rootPath))
        {
            errorMessage = $"A pasta raiz não existe: {rootPath}";
            return false;
        }

        if (IsOverlyBroadRoot(rootPath))
        {
            errorMessage = "Escolha uma pasta dedicada para compartilhar: não é permitido expor a raiz de uma unidade nem toda a pasta do usuário.";
            return false;
        }

        return true;
    }

    /// <summary>Raiz de unidade, pasta do usuário ou um ancestral dela (ex.: C:\Users).</summary>
    private static bool IsOverlyBroadRoot(string fullPath)
    {
        var normalized = fullPath.TrimEnd('\\', '/');
        var driveRoot = (Path.GetPathRoot(fullPath) ?? string.Empty).TrimEnd('\\', '/');
        if (string.Equals(normalized, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        return string.Equals(normalized, profile, StringComparison.OrdinalIgnoreCase) ||
               PathSafetyService.IsSubPath(profile, normalized);
    }
}
