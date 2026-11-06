namespace DoraExplorer.App.Models;

public enum NavigationNodeKind
{
    Favorites,
    Drive,
    RecycleBin,
    Ftp,
    Sftp,
    Scp,
    Tftp,
    Smb,
    Nfs,
    Directory,
    File,
    Archive,

    /// <summary>Pasta "Rede": agrupa os clientes FTP, SFTP, SCP, TFTP, SMB e NFS na árvore.</summary>
    NetworkGroup
}
