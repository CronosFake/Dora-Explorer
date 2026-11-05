namespace DoraExplorer.App.Models;

public sealed class PaneDisplayOptions
{
    public bool ShowFavorites { get; set; } = true;

    public bool ShowRecycleBin { get; set; } = true;

    public bool ShowFtp { get; set; } = true;

    public bool ShowSftp { get; set; } = true;

    public bool ShowScp { get; set; } = true;

    public bool ShowTftp { get; set; } = true;

    // Largura (px) que o usuário deu à coluna Nome no modo "Linhas compactas em coluna", por painel. 0 = automática (o que
    // sobrar no painel); arquivos de configuração antigos, sem este campo (ou com as larguras de Tipo, Modificado e Tamanho
    // de uma versão anterior, que já não existem), leem 0.
    public double NameColumnWidth { get; set; }
}
