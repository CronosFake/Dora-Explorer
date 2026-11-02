namespace DoraExplorer.App.Models;

public sealed class NetworkConnectionInfo
{
    public required NetworkProtocol Protocol { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public required string Host { get; init; }

    public int Port { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string RemotePath { get; set; } = "/";

    public string? LocalFilePath { get; init; }

    public string? RemoteFilePath { get; init; }

    public bool IsUpload { get; init; }

    // ---- Servidor ----------------------------------------------------------------------------

    /// <summary>Servidor FTP/TFTP: recusa gravar, apagar e renomear.</summary>
    public bool ReadOnly { get; init; }

    public FtpServerSecurity ServerSecurity { get; init; } = FtpServerSecurity.Optional;

    /// <summary>Faixa de portas das conexões de dados passivas ("50000-50100"); vazio = automática.</summary>
    public string? PassivePorts { get; init; }

    // ---- Cliente -----------------------------------------------------------------------------

    public FtpClientSecurity ClientSecurity { get; init; } = FtpClientSecurity.Auto;

    /// <summary>Chave privada SSH (OpenSSH/PEM/PuTTY) para SFTP e SCP, em vez de senha.</summary>
    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }
}
