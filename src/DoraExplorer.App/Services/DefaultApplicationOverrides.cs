using System.IO;

namespace DoraExplorer.App.Services;

/// <summary>
/// Aplicativo escolhido pelo usuário para abrir cada extensão dentro do Dora Explorer (não altera o Windows).
/// A escolha vai para as configurações e sobrevive ao fechar o programa.
/// </summary>
internal sealed class DefaultApplicationOverrides
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string DisplayName, string? ExecutablePath)> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string, string?>? _save;
    private readonly Action<string>? _remove;

    public DefaultApplicationOverrides(
        IEnumerable<KeyValuePair<string, (string DisplayName, string? ExecutablePath)>>? initial = null,
        Action<string, string, string?>? save = null,
        Action<string>? remove = null)
    {
        _save = save;
        _remove = remove;

        foreach (var (extension, entry) in initial ?? [])
        {
            if (Normalize(extension) is { } key && !string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                _items[key] = entry;
            }
        }
    }

    /// <summary>Avisa (com a extensão) quando uma escolha muda, para as linhas da tela atualizarem o nome.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>".TXT", "txt" e ".txt" são a mesma extensão: ".txt".</summary>
    public static string? Normalize(string? extension)
    {
        var trimmed = extension?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == ".")
        {
            return null;
        }

        return (trimmed.StartsWith('.') ? trimmed : "." + trimmed).ToLowerInvariant();
    }

    public bool TryGet(string? extension, out (string DisplayName, string? ExecutablePath) entry)
    {
        entry = default;
        if (Normalize(extension) is not { } key)
        {
            return false;
        }

        lock (_sync)
        {
            return _items.TryGetValue(key, out entry);
        }
    }

    public void Set(string extension, string displayName, string? executablePath)
    {
        if (Normalize(extension) is not { } key || string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        lock (_sync)
        {
            _items[key] = (displayName, executablePath);
        }

        Persist(() => _save?.Invoke(key, displayName, executablePath));
        Changed?.Invoke(this, key);
    }

    public bool Clear(string extension)
    {
        if (Normalize(extension) is not { } key)
        {
            return false;
        }

        bool removed;
        lock (_sync)
        {
            removed = _items.Remove(key);
        }

        if (removed)
        {
            Persist(() => _remove?.Invoke(key));
            Changed?.Invoke(this, key);
        }

        return removed;
    }

    private static void Persist(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Não foi possível guardar o aplicativo escolhido: {ex.Message}");
        }
    }
}
