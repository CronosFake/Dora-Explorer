using System.Text.Json.Serialization;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Models;

public sealed class NetworkProtocolSettings
{
    public string DisplayName { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Username { get; set; } = string.Empty;

    [JsonConverter(typeof(ProtectedStringJsonConverter))]
    public string Password { get; set; } = string.Empty;

    public string RemotePath { get; set; } = "/";

    public string LocalFilePath { get; set; } = string.Empty;

    public string RemoteFilePath { get; set; } = string.Empty;

    public bool IsUpload { get; set; } = true;

    // Servidor (REDE SERVER)
    public bool ReadOnly { get; set; }

    public FtpServerSecurity ServerSecurity { get; set; } = FtpServerSecurity.Optional;

    public string PassivePorts { get; set; } = string.Empty;

    // Cliente
    public FtpClientSecurity ClientSecurity { get; set; } = FtpClientSecurity.Auto;

    public string PrivateKeyPath { get; set; } = string.Empty;

    [JsonConverter(typeof(ProtectedStringJsonConverter))]
    public string PrivateKeyPassphrase { get; set; } = string.Empty;
}

public sealed class NetworkSettingsStore
{
    /// <summary>Configuração do servidor local (menu central REDE SERVER).</summary>
    public NetworkProtocolSettings Ftp { get; set; } = new() { Port = 21 };

    public NetworkProtocolSettings Sftp { get; set; } = new() { Port = 22 };

    public NetworkProtocolSettings Scp { get; set; } = new() { Port = 22 };

    public NetworkProtocolSettings Tftp { get; set; } = new() { Port = 69 };

    /// <summary>Configuração do cliente (nós FTP/SFTP/SCP/TFTP nos painéis).</summary>
    public NetworkProtocolSettings FtpClient { get; set; } = new() { Port = 21 };

    public NetworkProtocolSettings SftpClient { get; set; } = new() { Port = 22 };

    public NetworkProtocolSettings ScpClient { get; set; } = new() { Port = 22 };

    public NetworkProtocolSettings TftpClient { get; set; } = new() { Port = 69 };

    public List<NetworkClientProfile> ClientProfiles { get; set; } = [];

    public NetworkProtocolSettings Get(NetworkProtocol protocol) => GetServer(protocol);

    public NetworkProtocolSettings GetServer(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => Ftp,
            NetworkProtocol.Sftp => Sftp,
            NetworkProtocol.Scp => Scp,
            NetworkProtocol.Tftp => Tftp,
            _ => Ftp
        };

    public NetworkProtocolSettings GetClient(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => FtpClient,
            NetworkProtocol.Sftp => SftpClient,
            NetworkProtocol.Scp => ScpClient,
            NetworkProtocol.Tftp => TftpClient,
            _ => FtpClient
        };

    public void Set(NetworkProtocol protocol, NetworkProtocolSettings settings) =>
        SetServer(protocol, settings);

    public void SetServer(NetworkProtocol protocol, NetworkProtocolSettings settings)
    {
        switch (protocol)
        {
            case NetworkProtocol.Ftp:
                Ftp = settings;
                break;
            case NetworkProtocol.Sftp:
                Sftp = settings;
                break;
            case NetworkProtocol.Scp:
                Scp = settings;
                break;
            case NetworkProtocol.Tftp:
                Tftp = settings;
                break;
        }
    }

    public void SetClient(NetworkProtocol protocol, NetworkProtocolSettings settings)
    {
        switch (protocol)
        {
            case NetworkProtocol.Ftp:
                FtpClient = settings;
                break;
            case NetworkProtocol.Sftp:
                SftpClient = settings;
                break;
            case NetworkProtocol.Scp:
                ScpClient = settings;
                break;
            case NetworkProtocol.Tftp:
                TftpClient = settings;
                break;
        }
    }
}

public enum NetworkDialogAction
{
    Cancelled,
    Start,
    Stop,
    Save
}
