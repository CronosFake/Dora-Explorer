using System.Text.Json.Serialization;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Models;

/// <summary>Perfil de cliente de rede persistido (pasta sob FTP/SFTP/SCP/TFTP).</summary>
public sealed class NetworkClientProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public NetworkProtocol Protocol { get; set; }

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

    public FtpClientSecurity ClientSecurity { get; set; } = FtpClientSecurity.Auto;

    public string PrivateKeyPath { get; set; } = string.Empty;

    [JsonConverter(typeof(ProtectedStringJsonConverter))]
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    public NetworkConnectionInfo ToConnection() =>
        new()
        {
            Protocol = Protocol,
            DisplayName = DisplayName,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            RemotePath = string.IsNullOrWhiteSpace(RemotePath) ? "/" : RemotePath,
            LocalFilePath = LocalFilePath,
            RemoteFilePath = RemoteFilePath,
            IsUpload = IsUpload,
            ClientSecurity = ClientSecurity,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase
        };

    public static NetworkClientProfile FromConnection(NetworkConnectionInfo connection) =>
        new()
        {
            Protocol = connection.Protocol,
            DisplayName = string.IsNullOrWhiteSpace(connection.DisplayName)
                ? connection.Host
                : connection.DisplayName.Trim(),
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username ?? string.Empty,
            Password = connection.Password ?? string.Empty,
            RemotePath = string.IsNullOrWhiteSpace(connection.RemotePath) ? "/" : connection.RemotePath,
            LocalFilePath = connection.LocalFilePath ?? string.Empty,
            RemoteFilePath = connection.RemoteFilePath ?? string.Empty,
            IsUpload = connection.IsUpload,
            ClientSecurity = connection.ClientSecurity,
            PrivateKeyPath = connection.PrivateKeyPath ?? string.Empty,
            PrivateKeyPassphrase = connection.PrivateKeyPassphrase ?? string.Empty
        };
}
