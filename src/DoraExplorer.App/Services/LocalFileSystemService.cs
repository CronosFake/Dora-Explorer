using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class LocalFileSystemService : ILocalFileSystemService
{
    private readonly ExplorerViewSettingsService _viewSettings;
    private readonly IDirectoryIndexService _index;

    public LocalFileSystemService(ExplorerViewSettingsService viewSettings, IDirectoryIndexService index)
    {
        _viewSettings = viewSettings;
        _index = index;
    }

    public IReadOnlyList<FileSystemEntry> GetEntries(string path)
    {
        if (!Directory.Exists(path))
        {
            AppLogService.Warning($"Diretório não encontrado: {path}");
            return [];
        }

        try
        {
            var viewOptions = _viewSettings.Options;
            var indexed = _index.TryGetEntries(path, viewOptions)
                ?? _index.ReadFromDiskAndQueueStore(path, viewOptions);

            return PrependParentLink(path, indexed);
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao ler diretório: {path}", ex);
            return [];
        }
    }

    public IReadOnlyList<FileSystemEntry> GetDirectoryChildren(string path)
    {
        if (!Directory.Exists(path))
        {
            AppLogService.Warning($"Diretório não encontrado: {path}");
            return [];
        }

        try
        {
            var viewOptions = _viewSettings.Options;
            var indexed = _index.TryGetEntries(path, viewOptions)
                ?? _index.ReadFromDiskAndQueueStore(path, viewOptions);

            return indexed
                .Where(entry => entry.IsDirectory && !entry.IsParentLink)
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao listar subpastas: {path}", ex);
            return [];
        }
    }

    private static IReadOnlyList<FileSystemEntry> PrependParentLink(string path, IReadOnlyList<FileSystemEntry> entries)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is null)
        {
            return entries;
        }

        var result = new List<FileSystemEntry>(entries.Count + 1)
        {
            new()
            {
                Name = "..",
                FullPath = directory.Parent.FullName,
                IsDirectory = true,
                IsParentLink = true
            }
        };
        result.AddRange(entries);
        return result;
    }
}
