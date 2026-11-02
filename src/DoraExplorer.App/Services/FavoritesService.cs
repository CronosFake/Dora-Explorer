using System.Text.Json;
using DoraExplorer.App.Models;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

public static class FavoritesService
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static List<string>? _paths;

    private static string? _storePathOverride;

    private static string StorePath => _storePathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DoraExplorer",
        "favorites.json");

    /// <summary>
    /// Aponta os favoritos para outro arquivo (os testes usam uma pasta temporária, para nunca tocar em
    /// <c>%AppData%\DoraExplorer</c>) e esquece o que estava em memória. <c>null</c> volta ao arquivo de sempre.
    /// </summary>
    internal static void UseStore(string? path)
    {
        lock (Sync)
        {
            _storePathOverride = path;
            _paths = null;
        }
    }

    public static bool HasItems()
    {
        lock (Sync)
        {
            return LoadPathsUnlocked().Count > 0;
        }
    }

    public static bool IsFavorite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (Sync)
        {
            return LoadPathsUnlocked().Exists(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static (int Folders, int Files) GetCounts()
    {
        var folders = 0;
        var files = 0;

        foreach (var entry in GetEntries())
        {
            if (entry.IsDirectory)
            {
                folders++;
            }
            else
            {
                files++;
            }
        }

        return (folders, files);
    }

    public static IReadOnlyList<FileSystemEntry> GetEntries()
    {
        List<string> paths;
        lock (Sync)
        {
            paths = [.. LoadPathsUnlocked()];
        }

        var entries = new List<FileSystemEntry>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isDirectory = Directory.Exists(path) ||
                              (path.Length >= 2 &&
                               path[^1] is '\\' or '/' &&
                               !File.Exists(path));
            if (!isDirectory && !File.Exists(path) && !Directory.Exists(path))
            {
                // Item em falta (unidade desligada, etc.): manter na lista com o nome do caminho.
                isDirectory = LooksLikeDirectory(path);
            }
            else
            {
                isDirectory = Directory.Exists(path);
            }

            long? size = null;
            DateTime? modified = null;
            try
            {
                if (isDirectory && Directory.Exists(path))
                {
                    modified = Directory.GetLastWriteTime(path);
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    modified = info.LastWriteTime;
                }
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                // Metadados opcionais.
            }

            entries.Add(new FileSystemEntry
            {
                Name = GetDisplayName(path),
                FullPath = path,
                OriginalPath = path,
                IsDirectory = isDirectory,
                SizeInBytes = size,
                LastModified = modified
            });
        }

        return entries;
    }

    public static bool TryAdd(string path, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "Caminho inválido.";
            return false;
        }

        if (path.StartsWith("favorites:", StringComparison.OrdinalIgnoreCase) ||
            RecycleBinService.IsRecycleBinDataPath(path) ||
            path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Este item não pode ser adicionado aos favoritos.";
            return false;
        }

        lock (Sync)
        {
            var paths = LoadPathsUnlocked();
            if (paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            paths.Add(path);
            SavePathsUnlocked(paths);
        }

        AppLogService.Info($"Favorito adicionado: {path}");
        return true;
    }

    public static bool TryRemove(string path, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "Caminho inválido.";
            return false;
        }

        lock (Sync)
        {
            var paths = LoadPathsUnlocked();
            var removed = paths.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return true;
            }

            SavePathsUnlocked(paths);
        }

        AppLogService.Info($"Favorito removido: {path}");
        return true;
    }

    public static bool TryToggle(IReadOnlyList<string> paths, out bool added, out string? errorMessage)
    {
        errorMessage = null;
        added = false;
        if (paths.Count == 0)
        {
            errorMessage = "Selecione um ficheiro ou pasta para marcar como favorito.";
            return false;
        }

        var distinct = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
        {
            errorMessage = "Selecione um ficheiro ou pasta para marcar como favorito.";
            return false;
        }

        var allFavorite = distinct.All(IsFavorite);
        added = !allFavorite;

        foreach (var path in distinct)
        {
            if (allFavorite)
            {
                if (!TryRemove(path, out errorMessage))
                {
                    return false;
                }
            }
            else if (!IsFavorite(path) && !TryAdd(path, out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> LoadPathsUnlocked()
    {
        if (_paths is not null)
        {
            return _paths;
        }

        _paths = [];
        try
        {
            if (!File.Exists(StorePath))
            {
                return _paths;
            }

            var json = File.ReadAllText(StorePath);
            var loaded = JsonSerializer.Deserialize<List<string>>(json);
            if (loaded is not null)
            {
                _paths = loaded
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Falha ao carregar favoritos: {ex.Message}");
            _paths = [];
        }

        return _paths;
    }

    private static void SavePathsUnlocked(List<string> paths)
    {
        _paths = paths;
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(paths, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao guardar favoritos.", ex);
        }
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return path;
        }

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool LooksLikeDirectory(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(Path.GetExtension(name));
    }
}
