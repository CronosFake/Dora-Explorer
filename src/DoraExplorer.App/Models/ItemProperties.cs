namespace DoraExplorer.App.Models;

/// <summary>O que o Windows e o disco sabem sobre um arquivo ou uma pasta local (a janela Propriedades monta o texto a partir daqui).</summary>
public sealed record ItemProperties(
    string Name,
    string Path,
    string? Location,
    bool IsDirectory,
    string Extension,
    string TypeDescription,
    long? SizeBytes,
    long? SizeOnDiskBytes,
    DateTime? Created,
    DateTime? Modified,
    DateTime? Accessed,
    FileAttributes Attributes,
    string? LinkTarget,
    string? Owner,
    ExecutableDetails? Executable,
    (int Width, int Height)? ImageSize);

/// <summary>Os dados de versão que um executável ou uma biblioteca (.exe, .dll) carrega.</summary>
public sealed record ExecutableDetails(
    string? Description,
    string? FileVersion,
    string? ProductName,
    string? ProductVersion,
    string? Company,
    string? Copyright,
    string? OriginalFileName);

/// <summary>Rótulo, sistema de arquivos, tipo e espaço de uma unidade.</summary>
public sealed record DriveDetails(string Root, string Label, string Format, string DriveType, long TotalBytes, long FreeBytes);

/// <summary>Quanto há dentro de uma pasta (ou de várias): arquivos, pastas e bytes, no tamanho do arquivo e no espaço que ocupa no disco.</summary>
public readonly record struct FolderTotals(long Files, long Folders, long Bytes, long BytesOnDisk, long Unreadable);

/// <summary>As somas de verificação de um arquivo, em hexadecimal minúsculo: o SHA-256 e o MD5 (este só para conferir downloads).</summary>
public sealed record FileHashes(string Sha256, string Md5);
