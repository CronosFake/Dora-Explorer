namespace DoraExplorer.App.Models;

/// <summary>Aplicativo que o usuário escolheu no Dora Explorer para uma extensão.</summary>
public sealed class DefaultApplicationEntry
{
    public string DisplayName { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }
}
