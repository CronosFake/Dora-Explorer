using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.Infrastructure;

public static class ArchivePathHelper
{
    public static bool TryGetFormat(string pathOrName, out ArchiveFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return false;
        }

        var name = Path.GetFileName(pathOrName.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.TarGz;
            return true;
        }

        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Tar;
            return true;
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Zip;
            return true;
        }

        // Depois de .tar.gz: um .gz solto é um arquivo comprimido sozinho, não um tar.
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Gzip;
            return true;
        }

        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.SevenZip;
            return true;
        }

        if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.Rar;
            return true;
        }

        return false;
    }

    public static bool IsArchiveFile(string pathOrName) =>
        TryGetFormat(pathOrName, out _);

    public static bool SupportsRewrite(ArchiveFormat format) =>
        format is ArchiveFormat.Zip or ArchiveFormat.Tar or ArchiveFormat.TarGz;

    public static bool TrySplit(string path, out string archivePath, out string entryPath)
    {
        archivePath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (NetworkFileService.TryParseRemotePath(path, out _, out _, out _))
        {
            return false;
        }

        try
        {
            if (File.Exists(path) && TryGetFormat(path, out _))
            {
                archivePath = Path.GetFullPath(path);
                entryPath = string.Empty;
                return true;
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            // Path may be virtual (archive + entry); continue walking segments.
        }

        string root;
        try
        {
            root = Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }

        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var relative = path.Length > root.Length
            ? path[root.Length..].TrimStart('\\', '/')
            : string.Empty;

        if (string.IsNullOrEmpty(relative))
        {
            return false;
        }

        var segments = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var accumulated = root;

        for (var i = 0; i < segments.Length; i++)
        {
            accumulated = Path.Combine(accumulated, segments[i]);

            try
            {
                if (!File.Exists(accumulated))
                {
                    continue;
                }

                if (!TryGetFormat(accumulated, out _))
                {
                    return false;
                }

                archivePath = Path.GetFullPath(accumulated);
                entryPath = i + 1 < segments.Length
                    ? string.Join('/', segments.Skip(i + 1))
                    : string.Empty;
                return true;
            }
            catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
            {
                return false;
            }
        }

        return false;
    }

    public static bool IsVirtualArchivePath(string path) =>
        TrySplit(path, out _, out var entryPath) && !string.IsNullOrEmpty(entryPath);

    public static bool IsInsideOrIsArchive(string path) =>
        TrySplit(path, out _, out _);

    public static string Combine(string archivePath, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return archivePath;
        }

        var normalized = entryPath.Replace('/', '\\').Trim('\\');
        return Path.Combine(archivePath, normalized);
    }

    public static string NormalizeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return string.Empty;
        }

        return entryPath.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Resolve o caminho relativo de uma entrada de arquivo compactado dentro de
    /// <paramref name="destinationRoot"/>. Falha se o resultado sair da pasta de destino
    /// (<c>..</c>, caminho absoluto, letra de unidade ou stream alternativo NTFS), o que
    /// impede ataques de path traversal (zip-slip).
    /// </summary>
    public static bool TryResolveSafeDestination(string destinationRoot, string relativeEntryPath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(destinationRoot) ||
            string.IsNullOrWhiteSpace(relativeEntryPath) ||
            relativeEntryPath.Contains(':') ||
            relativeEntryPath.Contains('\0'))
        {
            return false;
        }

        var native = relativeEntryPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(native))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, native));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string GetEntryFileName(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    public static string? GetEntryParent(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[..slash] : string.Empty;
    }
}
