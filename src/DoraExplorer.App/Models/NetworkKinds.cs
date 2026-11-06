namespace DoraExplorer.App.Models;

/// <summary>
/// Uma só definição de quais nós da árvore são clientes de rede e quais deles permitem navegar por pastas. Antes a
/// lista "Ftp ou Sftp ou Scp ou Tftp" estava copiada em mais de dez pontos, e esquecer um deles deixava o protocolo
/// novo sem seta de expandir, sem ligar ou sem recarregar.
/// </summary>
internal static class NetworkKinds
{
    /// <summary>Nó de um protocolo de rede dentro da pasta "Rede" (FTP, SFTP, SCP, TFTP, SMB, NFS).</summary>
    public static bool IsClientRoot(NavigationNodeKind kind) =>
        kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp
            or NavigationNodeKind.Tftp or NavigationNodeKind.Smb or NavigationNodeKind.Nfs;

    /// <summary>Protocolos cujos nós mostram pastas e arquivos do servidor (o TFTP só transfere um arquivo).</summary>
    public static bool IsBrowsable(NavigationNodeKind kind) =>
        kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp or NavigationNodeKind.Scp
            or NavigationNodeKind.Smb or NavigationNodeKind.Nfs;

    public static bool IsBrowsable(NetworkProtocol protocol) =>
        protocol is NetworkProtocol.Ftp or NetworkProtocol.Sftp or NetworkProtocol.Scp
            or NetworkProtocol.Smb or NetworkProtocol.Nfs;

    /// <summary>SCP e TFTP não têm sessão: o cliente envia ou recebe um arquivo e termina.</summary>
    public static bool IsTransferOnly(NetworkProtocol protocol) =>
        protocol is NetworkProtocol.Scp or NetworkProtocol.Tftp;

    /// <summary>Protocolos que funcionam sobre arquivos compartilhados (pastas de verdade no servidor): SMB e NFS.</summary>
    public static bool IsFileShare(NetworkProtocol protocol) =>
        protocol is NetworkProtocol.Smb or NetworkProtocol.Nfs;

    public static NavigationNodeKind ToKind(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => NavigationNodeKind.Ftp,
            NetworkProtocol.Sftp => NavigationNodeKind.Sftp,
            NetworkProtocol.Scp => NavigationNodeKind.Scp,
            NetworkProtocol.Tftp => NavigationNodeKind.Tftp,
            NetworkProtocol.Smb => NavigationNodeKind.Smb,
            NetworkProtocol.Nfs => NavigationNodeKind.Nfs,
            _ => NavigationNodeKind.Ftp
        };

    public static NetworkProtocol ToProtocol(NavigationNodeKind kind) =>
        kind switch
        {
            NavigationNodeKind.Ftp => NetworkProtocol.Ftp,
            NavigationNodeKind.Sftp => NetworkProtocol.Sftp,
            NavigationNodeKind.Scp => NetworkProtocol.Scp,
            NavigationNodeKind.Tftp => NetworkProtocol.Tftp,
            NavigationNodeKind.Smb => NetworkProtocol.Smb,
            NavigationNodeKind.Nfs => NetworkProtocol.Nfs,
            _ => NetworkProtocol.Ftp
        };

    /// <summary>Porta padrão do protocolo (a do servidor e a do cliente são a mesma).</summary>
    public static int DefaultPort(NetworkProtocol protocol) =>
        protocol switch
        {
            NetworkProtocol.Ftp => 21,
            NetworkProtocol.Sftp or NetworkProtocol.Scp => 22,
            NetworkProtocol.Tftp => 69,
            NetworkProtocol.Smb => 445,
            NetworkProtocol.Nfs => 2049,
            _ => 22
        };

    /// <summary>
    /// Porta que o servidor local propõe. O SMB padrão (445) quase sempre já está com o próprio Windows (serviço Servidor),
    /// então o servidor SMB do Dora começa em 4445 para subir sem mexer no sistema.
    /// </summary>
    public static int DefaultServerPort(NetworkProtocol protocol) =>
        protocol == NetworkProtocol.Smb ? 4445 : DefaultPort(protocol);

    /// <summary>Nome do protocolo como aparece na interface ("SMB", "NFS"...).</summary>
    public static string DisplayName(NetworkProtocol protocol) => protocol.ToString().ToUpperInvariant();
}
