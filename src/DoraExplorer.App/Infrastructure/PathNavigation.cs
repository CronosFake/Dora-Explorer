using DoraExplorer.App.Services;

namespace DoraExplorer.App.Infrastructure;

public static class PathNavigation
{
    /// <summary>
    /// Pasta que contém <paramref name="path"/>, ou nulo quando não há para onde subir (raiz da unidade,
    /// raiz de um servidor remoto, caminhos especiais como Favoritos e Lixeira).
    /// Vale para pastas locais, arquivos compactados abertos como pasta e caminhos remotos.
    /// </summary>
    public static string? GetParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (NetworkFileService.TryParseRemotePath(path, out var protocol, out var host, out var remotePath))
        {
            var trimmed = remotePath.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                return null; // raiz do servidor
            }

            var slash = trimmed.LastIndexOf('/');
            return NetworkFileService.BuildRemotePath(protocol, host, slash <= 0 ? "/" : trimmed[..slash]);
        }

        if (!Path.IsPathRooted(path))
        {
            return null;
        }

        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = (Path.GetPathRoot(path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0 || string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            {
                return null; // raiz da unidade
            }

            return Path.GetDirectoryName(trimmed);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return null;
        }
    }
}
