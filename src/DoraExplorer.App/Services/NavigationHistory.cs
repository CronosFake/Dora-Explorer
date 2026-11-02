namespace DoraExplorer.App.Services;

/// <summary>
/// Histórico de pastas visitadas num painel, com voltar e avançar (como num navegador):
/// visitar uma pasta nova descarta o que havia "à frente".
/// </summary>
public sealed class NavigationHistory
{
    private readonly List<string> _entries = [];
    private readonly int _capacity;
    private int _index = -1;

    public NavigationHistory(int capacity = 100)
    {
        _capacity = Math.Max(2, capacity);
    }

    public string? Current => _index >= 0 ? _entries[_index] : null;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>Regista uma nova pasta. Repetir a pasta atual não cria entrada.</summary>
    public void Visit(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(Current, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_index < _entries.Count - 1)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(path);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }

        _index = _entries.Count - 1;
    }

    public string? Back()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _index--;
        return _entries[_index];
    }

    public string? Forward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _index++;
        return _entries[_index];
    }
}
