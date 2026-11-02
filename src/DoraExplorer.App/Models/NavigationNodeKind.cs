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
    Directory,
    File,
    Archive,

    /// <summary>Pasta "Rede": agrupa os clientes FTP, SFTP, SCP e TFTP na árvore.</summary>
    NetworkGroup
}
