namespace DoraExplorer.App.Models;

public sealed class FileSystemEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    public long? SizeInBytes { get; init; }

    public DateTime? LastModified { get; init; }

    public bool IsParentLink { get; init; }

    public bool IsRecycleBinItem { get; init; }

    public string? OriginalPath { get; init; }

    public bool IsHidden { get; init; }

    public bool IsSystem { get; init; }

    /// <summary>
    /// Só em listagens de arquivos compactados: quantas subpastas esta pasta tem diretamente dentro. Pastas de um arquivo
    /// compactado não estão no índice do disco, então a própria listagem informa (nulo = não se sabe).
    /// </summary>
    public int? ChildFolderCount { get; init; }

    /// <summary>Só em listagens de arquivos compactados: quantos arquivos esta pasta tem diretamente dentro.</summary>
    public int? ChildFileCount { get; init; }
}
