namespace DoraExplorer.App.Infrastructure;

public static class PathSafetyService
{
    /// <summary>
    /// Indica se <paramref name="childPath"/> está estritamente dentro de <paramref name="parentPath"/>
    /// (o próprio pai não conta). Funciona também quando o pai é a raiz de uma unidade (<c>C:\</c>).
    /// </summary>
    public static bool IsSubPath(string childPath, string parentPath)
    {
        try
        {
            var parent = Normalize(parentPath);
            var child = Normalize(childPath);

            if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }
    }

    // Caminho absoluto sem barra final, exceto na raiz: "C:\" tem de continuar "C:\" (e não "C:", que
    // significa "a pasta atual da unidade C").
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        return full.Length > rootLength
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }
}
