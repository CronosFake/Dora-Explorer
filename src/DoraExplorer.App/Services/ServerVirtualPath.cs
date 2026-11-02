namespace DoraExplorer.App.Services;

/// <summary>
/// Traduz os caminhos que um cliente SFTP/SCP pede ("/docs/../a.txt") para caminhos reais dentro da pasta compartilhada,
/// recusando tudo que possa sair dela: ".." acima da raiz fica na raiz, e nomes com separadores, letras de unidade,
/// fluxos alternativos (":"), nomes reservados do Windows (CON, NUL, COM1...) e links que apontem para fora são recusados.
/// </summary>
internal static class ServerVirtualPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Forma canônica do caminho do cliente: começa em "/", sem "." nem "..", sem barras repetidas.</summary>
    public static string Normalize(string? clientPath)
    {
        var segments = new List<string>();
        foreach (var segment in (clientPath ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Caminho real para o do cliente. Falso quando o caminho não é seguro: o servidor responde "permissão negada"
    /// sem nunca chegar a tocar no disco.
    /// </summary>
    public static bool TryResolve(string rootPath, string? clientPath, out string realPath, out string virtualPath)
    {
        virtualPath = Normalize(clientPath);
        realPath = rootPath;

        foreach (var segment in virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsSafeSegment(segment))
            {
                return false;
            }

            realPath = Path.Combine(realPath, segment);
        }

        return ServerPathGuard.IsInside(rootPath, realPath, allowRoot: true);
    }

    /// <summary>Um nome (de arquivo ou pasta) que o Windows trata como ele mesmo e que não escapa da pasta.</summary>
    public static bool IsSafeSegment(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // O Windows ignora ponto e espaço no fim ("arquivo." vira "arquivo"): dois nomes para o mesmo item.
        if (name[^1] is '.' or ' ')
        {
            return false;
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }
}
