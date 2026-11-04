namespace DoraExplorer.App.Models;

/// <summary>Como decidir que dois arquivos são o mesmo: <see cref="Standard"/> só olha nome e tamanho (rápido, pode errar); <see cref="Deep"/> compara o conteúdo.</summary>
public enum DuplicateMode
{
    /// <summary>Mesmo nome (sem diferenciar maiúsculas) e mesmo tamanho. Não lê o conteúdo dos arquivos.</summary>
    Standard,

    /// <summary>Mesmo tamanho e mesmo SHA-256 do conteúdo, qualquer que seja o nome. Lê os arquivos que têm o tamanho de outro.</summary>
    Deep
}

/// <param name="RootPath">Pasta onde procurar (local).</param>
/// <param name="Mode">Padrão (nome e tamanho) ou profundo (tamanho e hash).</param>
/// <param name="IncludeSubfolders">Procurar também nas subpastas.</param>
/// <param name="IncludeSystemFolders">
/// Só vale quando a pasta é a raiz de uma unidade (C:\, D:\): por padrão <c>Windows</c>, <c>Program Files</c>, <c>ProgramData</c> e as
/// pastas ocultas do sistema não entram, porque lá os "duplicados" são quase todos links físicos (o mesmo arquivo em dois lugares, que
/// não libera espaço) e sozinhos respondem por mais da metade dos arquivos de um C:.
/// </param>
public sealed record DuplicateScanRequest(
    string RootPath,
    DuplicateMode Mode,
    bool IncludeSubfolders = true,
    bool IncludeSystemFolders = false);

/// <param name="Attributes">Atributos vistos na listagem (já vêm com ela, sem outra consulta ao disco): o profundo não lê arquivo só da nuvem.</param>
public sealed record DuplicateFile(
    string FullPath,
    string Name,
    string Folder,
    long SizeInBytes,
    DateTime? LastModified,
    FileAttributes Attributes = 0);

/// <summary>
/// Um conjunto de arquivos iguais (dois ou mais). <paramref name="Key"/> é o que os une: nome e tamanho no modo padrão, o SHA-256
/// (em hexadecimal) no profundo.
/// </summary>
public sealed record DuplicateGroup(string Key, long SizeInBytes, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Espaço que se recupera guardando só uma das cópias.</summary>
    public long WastedBytes => SizeInBytes * (Files.Count - 1);
}

/// <param name="Groups">Grupos de duplicados, do que mais desperdiça ao que menos.</param>
/// <param name="FilesScanned">Arquivos vistos (vazios não contam).</param>
/// <param name="FilesRead">Arquivos cujo conteúdo foi lido para o hash (modo profundo).</param>
/// <param name="Unreadable">Arquivos que o Windows não deixou ler no modo profundo (em uso, sem permissão) ou que são só da nuvem.</param>
public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    int FilesScanned,
    int FilesRead,
    int Unreadable)
{
    /// <summary>Pastas do sistema que ficaram de fora por a procura ter começado na raiz de uma unidade (vazio se nenhuma, ou se foram incluídas).</summary>
    public IReadOnlyList<string> SkippedSystemFolders { get; init; } = [];

    public long TotalWastedBytes => Groups.Sum(group => group.WastedBytes);

    public int TotalDuplicateFiles => Groups.Sum(group => group.Files.Count);

    public static DuplicateScanResult Empty { get; } = new([], 0, 0, 0);
}

/// <summary>Andamento da procura: a fase e quantos arquivos foram vistos ou lidos até agora.</summary>
public sealed record DuplicateScanProgress(DuplicateScanPhase Phase, int FilesScanned, int FilesToRead, int FilesRead, string CurrentPath);

public enum DuplicateScanPhase
{
    /// <summary>Listando as pastas.</summary>
    Listing,

    /// <summary>Comparando conteúdo (só no modo profundo).</summary>
    Reading
}
