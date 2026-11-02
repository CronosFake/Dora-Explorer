using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DoraExplorer.App.Services;

public sealed class ArchiveService : IArchiveService
{
    public bool TryListEntries(
        string archivePath,
        string entryDirectory,
        out IReadOnlyList<FileSystemEntry> entries,
        out string? errorMessage)
    {
        entries = [];
        errorMessage = null;

        if (!TryValidateArchive(archivePath, out var format, out errorMessage))
        {
            return false;
        }

        try
        {
            entries = format switch
            {
                ArchiveFormat.Zip => ListZipEntries(archivePath, entryDirectory),
                ArchiveFormat.Tar or ArchiveFormat.TarGz => ListTarEntries(archivePath, entryDirectory, format),
                ArchiveFormat.Rar or ArchiveFormat.SevenZip => ListSharpCompressEntries(archivePath, entryDirectory),
                ArchiveFormat.Gzip => ListGzipEntries(archivePath, entryDirectory),
                _ => []
            };
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao listar arquivo: {archivePath}", ex);
            errorMessage = FileErrorText.Describe(ex, archivePath);
            entries = [];
            return false;
        }
    }

    public bool TryExtractEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!TryValidateArchive(archivePath, out var format, out errorMessage))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            errorMessage = "A pasta de destino não existe.";
            return false;
        }

        if (ArchivePathHelper.IsInsideOrIsArchive(destinationDirectory) &&
            ArchivePathHelper.IsVirtualArchivePath(destinationDirectory))
        {
            errorMessage = "Não é possível extrair para dentro de um arquivo compactado.";
            return false;
        }

        var normalizedEntry = ArchivePathHelper.NormalizeEntryPath(entryPath);
        if (string.IsNullOrEmpty(normalizedEntry))
        {
            errorMessage = "Entrada inválida no arquivo.";
            return false;
        }

        try
        {
            return format switch
            {
                ArchiveFormat.Zip => ExtractZipEntry(archivePath, normalizedEntry, destinationDirectory, out errorMessage),
                ArchiveFormat.Tar or ArchiveFormat.TarGz =>
                    ExtractTarEntry(archivePath, normalizedEntry, destinationDirectory, format, out errorMessage),
                ArchiveFormat.Rar or ArchiveFormat.SevenZip =>
                    ExtractSharpCompressEntry(archivePath, normalizedEntry, destinationDirectory, out errorMessage),
                ArchiveFormat.Gzip => ExtractGzipEntry(archivePath, normalizedEntry, destinationDirectory, out errorMessage),
                _ => FailUnsupported(out errorMessage)
            };
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao extrair '{normalizedEntry}' de '{archivePath}'.", ex);
            errorMessage = FileErrorText.Describe(ex, archivePath);
            return false;
        }
    }

    public bool TryRemoveEntries(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!TryValidateArchive(archivePath, out var format, out errorMessage))
        {
            return false;
        }

        if (!ArchivePathHelper.SupportsRewrite(format))
        {
            errorMessage = "Não é possível alterar arquivos .rar, .7z ou .gz. Use Copiar para extrair o conteúdo.";
            return false;
        }

        if (entryPaths.Count == 0)
        {
            errorMessage = "Nenhuma entrada selecionada.";
            return false;
        }

        var normalized = entryPaths
            .Select(ArchivePathHelper.NormalizeEntryPath)
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            errorMessage = "Nenhuma entrada válida selecionada.";
            return false;
        }

        try
        {
            return format switch
            {
                ArchiveFormat.Zip => RemoveZipEntries(archivePath, normalized, out errorMessage),
                ArchiveFormat.Tar or ArchiveFormat.TarGz =>
                    RemoveTarEntries(archivePath, normalized, format, out errorMessage),
                _ => FailUnsupported(out errorMessage)
            };
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao remover entradas de '{archivePath}'.", ex);
            errorMessage = FileErrorText.Describe(ex, archivePath);
            return false;
        }
    }

    public bool TryExtractEntryToTempAndOpen(
        string archivePath,
        string entryPath,
        out string? errorMessage)
    {
        errorMessage = null;
        var normalizedEntry = ArchivePathHelper.NormalizeEntryPath(entryPath);
        if (string.IsNullOrEmpty(normalizedEntry))
        {
            errorMessage = "Entrada inválida no arquivo.";
            return false;
        }

        var fileName = ArchivePathHelper.GetEntryFileName(normalizedEntry);
        if (string.IsNullOrEmpty(fileName))
        {
            errorMessage = "Não é possível abrir uma pasta dentro do arquivo.";
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "DoraExplorer", "archive-open", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        if (!TryExtractEntry(archivePath, normalizedEntry, tempDir, out errorMessage))
        {
            TryDeleteDirectory(tempDir);
            return false;
        }

        var extracted = Path.Combine(tempDir, fileName);
        if (!File.Exists(extracted))
        {
            // Folder extract or nested path — open first matching file if present.
            extracted = Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories).FirstOrDefault() ?? extracted;
        }

        if (!File.Exists(extracted))
        {
            errorMessage = "Falha ao extrair o ficheiro para abertura.";
            TryDeleteDirectory(tempDir);
            return false;
        }

        return FileOpenService.TryOpen(extracted, out errorMessage);
    }

    private static bool FailUnsupported(out string? errorMessage)
    {
        errorMessage = "Formato de arquivo não suportado.";
        return false;
    }

    private static bool TryValidateArchive(string archivePath, out ArchiveFormat format, out string? errorMessage)
    {
        format = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            errorMessage = "O arquivo compactado não existe.";
            return false;
        }

        if (!ArchivePathHelper.TryGetFormat(archivePath, out format))
        {
            errorMessage = "Formato de arquivo não suportado.";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<FileSystemEntry> ListZipEntries(string archivePath, string entryDirectory)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        var meta = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var zipEntry in zip.Entries)
        {
            // Um zip válido pode repetir um nome (ou repeti-lo mudando só as maiúsculas): vale a primeira, como em GetEntry.
            _ = meta.TryAdd(NormalizeZipName(zipEntry.FullName), zipEntry);
        }

        return BuildLevelEntries(archivePath, entryDirectory, names, name =>
        {
            var key = NormalizeZipName(name);
            if (!meta.TryGetValue(key, out var entry) && !meta.TryGetValue(key + "/", out entry))
            {
                return (null, null);
            }

            return (entry.Length, entry.LastWriteTime.DateTime);
        });
    }

    private static IReadOnlyList<FileSystemEntry> ListTarEntries(
        string archivePath,
        string entryDirectory,
        ArchiveFormat format)
    {
        using var stream = OpenTarReadStream(archivePath, format);
        using var reader = new TarReader(stream, leaveOpen: false);
        var names = new List<string>();
        var meta = new Dictionary<string, (long? Size, DateTime? Modified)>(StringComparer.OrdinalIgnoreCase);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (!stream.CanSeek)
            {
                // Num .tar.gz, pular o conteúdo que não foi lido falha em alguns arquivos ("Unable to parse number");
                // ler e descartar não falha e não custa mais, já que o gzip precisa descomprimir esses bytes de qualquer jeito.
                entry.DataStream?.CopyTo(Stream.Null);
            }

            var name = entry.Name;
            names.Add(name);
            var key = ArchivePathHelper.NormalizeEntryPath(name);
            if (!string.IsNullOrEmpty(key))
            {
                meta[key] = (
                    entry.EntryType == TarEntryType.Directory ? null : entry.Length,
                    entry.ModificationTime.UtcDateTime);
            }
        }

        return BuildLevelEntries(archivePath, entryDirectory, names, name =>
        {
            var key = ArchivePathHelper.NormalizeEntryPath(name);
            return meta.TryGetValue(key, out var info) ? (info.Size, info.Modified) : (null, null);
        });
    }

    /// <summary>
    /// Um .gz solto é um arquivo comprimido sozinho: aparece como uma pasta com um arquivo dentro, que tem o nome do .gz sem
    /// a extensão (como o gunzip faz). Listar lê só o cabeçalho e o rodapé, sem descomprimir nada.
    /// </summary>
    private static IReadOnlyList<FileSystemEntry> ListGzipEntries(string archivePath, string entryDirectory)
    {
        var (size, modified) = ReadGzipInfo(archivePath);
        if (!string.IsNullOrEmpty(ArchivePathHelper.NormalizeEntryPath(entryDirectory)))
        {
            return [];
        }

        var name = GetGzipEntryName(archivePath);
        return
        [
            new FileSystemEntry
            {
                Name = name,
                FullPath = ArchivePathHelper.Combine(archivePath, name),
                IsDirectory = false,
                SizeInBytes = size,
                LastModified = modified
            }
        ];
    }

    private static string GetGzipEntryName(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        var name = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? fileName[..^".gz".Length] : fileName;
        return string.IsNullOrWhiteSpace(name) ? "arquivo" : name;
    }

    // O deflate não passa de uns 1032:1: com até 4 MB comprimidos o conteúdo tem menos de 4 GiB, e aí o tamanho do rodapé
    // (ISIZE, só 32 bits) é exato. Acima disso ele pode estar truncado, então não é mostrado.
    private const long GzipExactSizeLimit = 4_000_000;

    /// <summary>Confere que é mesmo gzip e lê o tamanho (rodapé) e a data (cabeçalho), sem descomprimir.</summary>
    private static (long? Size, DateTime? Modified) ReadGzipInfo(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        var header = new byte[10];
        if (stream.Length < 18 || stream.Read(header, 0, header.Length) != header.Length ||
            header[0] != 0x1F || header[1] != 0x8B || header[2] != 8)
        {
            throw new InvalidDataException("O arquivo não é um .gz válido.");
        }

        var footer = new byte[4];
        stream.Seek(-footer.Length, SeekOrigin.End);
        stream.ReadExactly(footer);
        long? size = stream.Length <= GzipExactSizeLimit ? BinaryPrimitives.ReadUInt32LittleEndian(footer) : null;

        var unixTime = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        DateTime? modified = unixTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime
            : File.GetLastWriteTime(archivePath);

        return (size, modified);
    }

    private static bool ExtractGzipEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        out string? errorMessage)
    {
        var name = GetGzipEntryName(archivePath);
        if (!string.Equals(entryPath, name, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Entrada não encontrada: {entryPath}";
            return false;
        }

        _ = ReadGzipInfo(archivePath); // recusa o que não é gzip antes de criar o arquivo de destino
        if (!TryGetSingleFileDestination(destinationDirectory, name, out var destinationFile, out errorMessage))
        {
            return false;
        }

        try
        {
            using var input = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
            using var output = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
        }
        catch
        {
            TryDeleteFile(destinationFile); // um arquivo pela metade seria lixo
            throw;
        }

        return true;
    }

    private static IReadOnlyList<FileSystemEntry> ListSharpCompressEntries(string archivePath, string entryDirectory)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var names = new List<string>();
        var meta = new Dictionary<string, (long? Size, DateTime? Modified)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var name = entry.Key ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            names.Add(entry.IsDirectory && !name.EndsWith('/') ? name + "/" : name);
            var key = ArchivePathHelper.NormalizeEntryPath(name);
            if (!string.IsNullOrEmpty(key))
            {
                meta[key] = (entry.IsDirectory ? null : entry.Size, entry.LastModifiedTime);
            }
        }

        return BuildLevelEntries(archivePath, entryDirectory, names, name =>
        {
            var key = ArchivePathHelper.NormalizeEntryPath(name);
            return meta.TryGetValue(key, out var info) ? (info.Size, info.Modified) : (null, null);
        });
    }

    /// <summary>
    /// Registra o que há dentro de <paramref name="dirName"/> a partir do resto do caminho da entrada (o que vem depois de
    /// "pasta/"): um nome seguido de barra é subpasta, uma entrada marcada como pasta também, o resto é arquivo.
    /// </summary>
    private static void CountInside(
        Dictionary<string, HashSet<string>> innerFolders,
        Dictionary<string, HashSet<string>> innerFiles,
        string dirName,
        string rest,
        bool isDirectoryEntry)
    {
        if (string.IsNullOrEmpty(rest))
        {
            return; // é a própria entrada da pasta ("pasta/"), não algo dentro dela
        }

        var slash = rest.IndexOf('/');
        var isFolder = slash >= 0 || isDirectoryEntry;
        var name = slash >= 0 ? rest[..slash] : rest;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var target = isFolder ? innerFolders : innerFiles;
        if (!target.TryGetValue(dirName, out var names))
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            target[dirName] = names;
        }

        _ = names.Add(name);
    }

    private static IReadOnlyList<FileSystemEntry> BuildLevelEntries(
        string archivePath,
        string entryDirectory,
        IEnumerable<string> rawNames,
        Func<string, (long? Size, DateTime? Modified)> metaLookup)
    {
        var prefix = ArchivePathHelper.NormalizeEntryPath(entryDirectory);
        if (!string.IsNullOrEmpty(prefix))
        {
            prefix += "/";
        }

        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // O que cada pasta deste nível tem diretamente dentro, contado no mesmo passe: listar cada pasta à parte
        // releria o arquivo inteiro a cada vez (e um .tar.gz precisa ser descomprimido por completo para ser lido).
        var innerFolders = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var innerFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawNames)
        {
            var full = ArchivePathHelper.NormalizeEntryPath(raw);
            if (string.IsNullOrEmpty(full))
            {
                continue;
            }

            var isDirectoryEntry = raw.EndsWith('/') || raw.EndsWith('\\');
            string relative;
            if (string.IsNullOrEmpty(prefix))
            {
                relative = full;
            }
            else
            {
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relative = full[prefix.Length..];
            }

            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var slash = relative.IndexOf('/');
            if (slash >= 0)
            {
                var dirName = relative[..slash];
                if (!string.IsNullOrEmpty(dirName))
                {
                    directories[dirName] = dirName;
                    CountInside(innerFolders, innerFiles, dirName, relative[(slash + 1)..], isDirectoryEntry);
                }
            }
            else if (isDirectoryEntry)
            {
                directories[relative] = relative;
            }
            else
            {
                files[relative] = relative;
            }
        }

        // A directory may only appear as a prefix of nested files.
        foreach (var fileName in files.Keys.ToList())
        {
            // no-op; directories already collected from nested paths
        }

        var result = new List<FileSystemEntry>();

        foreach (var dirName in directories.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var entryPath = string.IsNullOrEmpty(ArchivePathHelper.NormalizeEntryPath(entryDirectory))
                ? dirName
                : ArchivePathHelper.NormalizeEntryPath(entryDirectory) + "/" + dirName;

            innerFolders.TryGetValue(dirName, out var folderNames);
            innerFiles.TryGetValue(dirName, out var fileNames);

            result.Add(new FileSystemEntry
            {
                Name = dirName,
                FullPath = ArchivePathHelper.Combine(archivePath, entryPath),
                IsDirectory = true,
                ChildFolderCount = folderNames?.Count ?? 0,
                ChildFileCount = fileNames?.Count(name => folderNames is null || !folderNames.Contains(name)) ?? 0
            });
        }

        foreach (var fileName in files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (directories.ContainsKey(fileName))
            {
                continue;
            }

            var entryPath = string.IsNullOrEmpty(ArchivePathHelper.NormalizeEntryPath(entryDirectory))
                ? fileName
                : ArchivePathHelper.NormalizeEntryPath(entryDirectory) + "/" + fileName;

            var (size, modified) = metaLookup(entryPath);
            result.Add(new FileSystemEntry
            {
                Name = fileName,
                FullPath = ArchivePathHelper.Combine(archivePath, entryPath),
                IsDirectory = false,
                SizeInBytes = size,
                LastModified = modified
            });
        }

        return result;
    }

    private static bool ExtractZipEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        out string? errorMessage)
    {
        errorMessage = null;
        using var zip = ZipFile.OpenRead(archivePath);
        var matches = zip.Entries
            .Where(e => EntryMatches(NormalizeZipName(e.FullName), entryPath))
            .ToList();

        if (matches.Count == 0)
        {
            errorMessage = $"Entrada não encontrada: {entryPath}";
            return false;
        }

        var isSingleFile = matches.Count == 1 &&
                           !IsZipDirectoryEntry(matches[0]) &&
                           string.Equals(NormalizeZipName(matches[0].FullName), entryPath, StringComparison.OrdinalIgnoreCase);

        if (isSingleFile)
        {
            if (!TryGetSingleFileDestination(destinationDirectory, entryPath, out var destFile, out errorMessage))
            {
                return false;
            }

            matches[0].ExtractToFile(destFile, overwrite: false);
            return true;
        }

        var rootName = ArchivePathHelper.GetEntryFileName(entryPath);
        var destRoot = GetUniqueDestinationPath(destinationDirectory, rootName);

        // Valida todos os destinos antes de escrever qualquer coisa (zip-slip).
        var unsafeEntry = FindUnsafeEntry(
            destRoot,
            matches.Select(e => (e.FullName, GetRelativeEntryPath(NormalizeZipName(e.FullName), entryPath))));
        if (unsafeEntry is not null)
        {
            errorMessage = UnsafeEntryMessage(archivePath, unsafeEntry);
            return false;
        }

        Directory.CreateDirectory(destRoot);

        foreach (var entry in matches)
        {
            var name = NormalizeZipName(entry.FullName);
            var relative = GetRelativeEntryPath(name, entryPath);
            if (relative is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(relative) || IsZipDirectoryEntry(entry))
            {
                if (!string.IsNullOrEmpty(relative))
                {
                    Directory.CreateDirectory(ResolveExtractionPath(destRoot, relative));
                }

                continue;
            }

            var destPath = ResolveExtractionPath(destRoot, relative);
            var destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            entry.ExtractToFile(destPath, overwrite: false);
        }

        return true;
    }

    private static bool ExtractTarEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        ArchiveFormat format,
        out string? errorMessage)
    {
        errorMessage = null;
        using var stream = OpenTarReadStream(archivePath, format);
        using var reader = new TarReader(stream, leaveOpen: false);

        var collected = new List<(string Name, TarEntryType Type, byte[]? Data, DateTimeOffset Modified)>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            var name = ArchivePathHelper.NormalizeEntryPath(entry.Name);
            if (!EntryMatches(name, entryPath))
            {
                continue;
            }

            byte[]? data = null;
            if (entry.DataStream is not null && entry.EntryType is not TarEntryType.Directory)
            {
                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                data = ms.ToArray();
            }

            collected.Add((name, entry.EntryType, data, entry.ModificationTime));
        }

        if (collected.Count == 0)
        {
            errorMessage = $"Entrada não encontrada: {entryPath}";
            return false;
        }

        var isSingleFile = collected.Count == 1 &&
                           collected[0].Type is not TarEntryType.Directory &&
                           string.Equals(collected[0].Name, entryPath, StringComparison.OrdinalIgnoreCase);

        if (isSingleFile)
        {
            if (!TryGetSingleFileDestination(destinationDirectory, entryPath, out var destFile, out errorMessage))
            {
                return false;
            }

            File.WriteAllBytes(destFile, collected[0].Data ?? []);
            return true;
        }

        var rootName = ArchivePathHelper.GetEntryFileName(entryPath);
        var destRoot = GetUniqueDestinationPath(destinationDirectory, rootName);

        // Valida todos os destinos antes de escrever qualquer coisa (path traversal).
        var unsafeEntry = FindUnsafeEntry(
            destRoot,
            collected.Select(item => (item.Name, GetRelativeEntryPath(item.Name, entryPath))));
        if (unsafeEntry is not null)
        {
            errorMessage = UnsafeEntryMessage(archivePath, unsafeEntry);
            return false;
        }

        Directory.CreateDirectory(destRoot);

        foreach (var item in collected)
        {
            var relative = GetRelativeEntryPath(item.Name, entryPath);
            if (relative is null)
            {
                continue;
            }

            if (item.Type == TarEntryType.Directory || string.IsNullOrEmpty(relative))
            {
                if (!string.IsNullOrEmpty(relative))
                {
                    Directory.CreateDirectory(ResolveExtractionPath(destRoot, relative));
                }

                continue;
            }

            var destPath = ResolveExtractionPath(destRoot, relative);
            var destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            File.WriteAllBytes(destPath, item.Data ?? []);
        }

        return true;
    }

    private static bool ExtractSharpCompressEntry(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        out string? errorMessage)
    {
        errorMessage = null;
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var matches = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Key) &&
                        EntryMatches(ArchivePathHelper.NormalizeEntryPath(e.Key!), entryPath))
            .ToList();

        if (matches.Count == 0)
        {
            errorMessage = $"Entrada não encontrada: {entryPath}";
            return false;
        }

        var isSingleFile = matches.Count == 1 &&
                           !matches[0].IsDirectory &&
                           string.Equals(
                               ArchivePathHelper.NormalizeEntryPath(matches[0].Key!),
                               entryPath,
                               StringComparison.OrdinalIgnoreCase);

        // Arquivos a gravar (entrada e destino). Pastas são criadas na hora; os arquivos só depois, de uma vez, porque
        // num arquivo sólido a ordem de leitura é a do arquivo e não a da lista.
        var files = new List<(IArchiveEntry Entry, string DestinationPath)>();

        if (isSingleFile)
        {
            if (!TryGetSingleFileDestination(destinationDirectory, entryPath, out var destFile, out errorMessage))
            {
                return false;
            }

            files.Add((matches[0], destFile));
        }
        else
        {
            var rootName = ArchivePathHelper.GetEntryFileName(entryPath);
            var destRoot = GetUniqueDestinationPath(destinationDirectory, rootName);

            // Valida todos os destinos antes de escrever qualquer coisa (path traversal).
            var unsafeEntry = FindUnsafeEntry(
                destRoot,
                matches.Select(e => (e.Key!, GetRelativeEntryPath(ArchivePathHelper.NormalizeEntryPath(e.Key!), entryPath))));
            if (unsafeEntry is not null)
            {
                errorMessage = UnsafeEntryMessage(archivePath, unsafeEntry);
                return false;
            }

            Directory.CreateDirectory(destRoot);

            foreach (var entry in matches)
            {
                var name = ArchivePathHelper.NormalizeEntryPath(entry.Key!);
                var relative = GetRelativeEntryPath(name, entryPath);
                if (relative is null)
                {
                    continue;
                }

                if (entry.IsDirectory || string.IsNullOrEmpty(relative))
                {
                    if (!string.IsNullOrEmpty(relative))
                    {
                        Directory.CreateDirectory(ResolveExtractionPath(destRoot, relative));
                    }

                    continue;
                }

                var destPath = ResolveExtractionPath(destRoot, relative);
                var destParent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                files.Add((entry, destPath));
            }
        }

        WriteSharpCompressFiles(archive, files);
        return true;
    }

    private static void WriteSharpCompressFiles(IArchive archive, IReadOnlyList<(IArchiveEntry Entry, string DestinationPath)> files)
    {
        var options = new ExtractionOptions { ExtractFullPath = false, Overwrite = false };

        if (!archive.IsSolid)
        {
            foreach (var (entry, destinationPath) in files)
            {
                entry.WriteToFile(destinationPath, options);
            }

            return;
        }

        // Num arquivo sólido cada entrada depende das anteriores. Pedir uma entrada com o arquivo recém-aberto dava
        // conteúdo errado no RAR, e uma pasta inteira de um 7z sólido relia o bloco a cada arquivo (13,6 s contra 0,7 s
        // numa pasta de 43 arquivos). Aqui o arquivo é lido uma vez, em sequência, parando na última entrada pedida.
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, destinationPath) in files)
        {
            _ = pending.TryAdd(entry.Key!, destinationPath);
        }

        using var reader = archive.ExtractAllEntries();
        while (pending.Count > 0 && reader.MoveToNextEntry())
        {
            if (!reader.Entry.IsDirectory &&
                reader.Entry.Key is { } key &&
                pending.Remove(key, out var destinationPath))
            {
                reader.WriteEntryToFile(destinationPath, options);
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidDataException($"Entrada não encontrada na leitura do arquivo: {pending.Keys.First()}");
        }
    }

    private static bool RemoveZipEntries(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        out string? errorMessage)
    {
        errorMessage = null;
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var toDelete = zip.Entries
            .Where(e => entryPaths.Any(p => EntryMatches(NormalizeZipName(e.FullName), p)))
            .ToList();

        if (toDelete.Count == 0)
        {
            errorMessage = "Nenhuma entrada correspondente encontrada no arquivo.";
            return false;
        }

        foreach (var entry in toDelete)
        {
            entry.Delete();
        }

        return true;
    }

    private static bool RemoveTarEntries(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        ArchiveFormat format,
        out string? errorMessage)
    {
        errorMessage = null;
        var tempPath = archivePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var readStream = OpenTarReadStream(archivePath, format))
            using (var reader = new TarReader(readStream, leaveOpen: false))
            using (var writeStream = OpenTarWriteStream(tempPath, format))
            using (var writer = new TarWriter(writeStream, TarEntryFormat.Gnu, leaveOpen: false))
            {
                var kept = 0;
                TarEntry? entry;
                while ((entry = reader.GetNextEntry(copyData: true)) is not null)
                {
                    var name = ArchivePathHelper.NormalizeEntryPath(entry.Name);
                    if (entryPaths.Any(p => EntryMatches(name, p)))
                    {
                        continue;
                    }

                    writer.WriteEntry(entry);
                    kept++;
                }

                if (kept == 0 && !File.Exists(tempPath))
                {
                    // Empty archive still valid — ensure file exists via writer dispose.
                }
            }

            File.Copy(tempPath, archivePath, overwrite: true);
            return true;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static Stream OpenTarReadStream(string archivePath, ArchiveFormat format)
    {
        var fileStream = File.OpenRead(archivePath);
        if (format == ArchiveFormat.TarGz)
        {
            return new GZipStream(fileStream, CompressionMode.Decompress);
        }

        return fileStream;
    }

    private static Stream OpenTarWriteStream(string archivePath, ArchiveFormat format)
    {
        var fileStream = File.Create(archivePath);
        if (format == ArchiveFormat.TarGz)
        {
            return new GZipStream(fileStream, CompressionLevel.Optimal);
        }

        return fileStream;
    }

    private static bool EntryMatches(string entryName, string targetPath)
    {
        var name = ArchivePathHelper.NormalizeEntryPath(entryName);
        var target = ArchivePathHelper.NormalizeEntryPath(targetPath);
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        return string.Equals(name, target, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRelativeEntryPath(string entryName, string rootEntry)
    {
        var name = ArchivePathHelper.NormalizeEntryPath(entryName);
        var root = ArchivePathHelper.NormalizeEntryPath(rootEntry);
        if (string.Equals(name, root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (name.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return name[(root.Length + 1)..];
        }

        return null;
    }

    private static string NormalizeZipName(string fullName) =>
        ArchivePathHelper.NormalizeEntryPath(fullName);

    // O nome já normalizado perde a barra final; é o nome original que diz se a entrada é uma pasta.
    private static bool IsZipDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static string? FindUnsafeEntry(string destinationRoot, IEnumerable<(string EntryName, string? Relative)> entries)
    {
        foreach (var (entryName, relative) in entries)
        {
            if (!string.IsNullOrEmpty(relative) &&
                !ArchivePathHelper.TryResolveSafeDestination(destinationRoot, relative, out _))
            {
                return entryName;
            }
        }

        return null;
    }

    private static string UnsafeEntryMessage(string archivePath, string entryName)
    {
        AppLogService.Warning($"Extração bloqueada (caminho fora da pasta de destino) em '{archivePath}': {entryName}");
        return $"O arquivo contém um caminho inseguro e não foi extraído: {entryName}";
    }

    private static string ResolveExtractionPath(string destinationRoot, string relative) =>
        ArchivePathHelper.TryResolveSafeDestination(destinationRoot, relative, out var fullPath)
            ? fullPath
            : throw new InvalidOperationException($"Caminho de extração inseguro: {relative}");

    private static bool TryGetSingleFileDestination(
        string destinationDirectory,
        string entryPath,
        out string destinationFile,
        out string? errorMessage)
    {
        var fileName = Path.GetFileName(entryPath);
        if (!ArchivePathHelper.TryResolveSafeDestination(destinationDirectory, fileName, out _))
        {
            destinationFile = string.Empty;
            errorMessage = $"Nome de entrada inseguro: {fileName}";
            return false;
        }

        destinationFile = GetUniqueDestinationPath(destinationDirectory, fileName);
        errorMessage = null;
        return true;
    }

    private static string GetUniqueDestinationPath(string destinationDirectory, string fileName)
    {
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
        {
            return destinationPath;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // Preserve .tar.gz style names when extracting folders named like archives.
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            nameWithoutExtension = fileName[..^".tar.gz".Length];
            extension = ".tar.gz";
        }

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(destinationDirectory, $"{nameWithoutExtension} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(destinationDirectory, $"{nameWithoutExtension}-{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            AppLogService.Warning($"Não foi possível remover o arquivo temporário '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            AppLogService.Warning($"Não foi possível remover a pasta temporária '{path}': {ex.Message}");
        }
    }
}
