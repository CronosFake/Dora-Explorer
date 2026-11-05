namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Larguras do modo "Linhas compactas em coluna". Tipo (aplicativo padrão e contagens), Modificado (data e hora) e Tamanho têm
/// largura FIXA e folgada: mostram sempre o conteúdo inteiro e não mudam com o painel nem com a rolagem. Só o Nome se alarga
/// (arrastando a borda direita dele no cabeçalho); ele nunca fica menor que o espaço que sobra no painel, e o que passar da
/// largura do painel vira rolagem horizontal. Tudo puro (sem tela) para ser testado.
/// </summary>
public static class PaneColumnWidths
{
    /// <summary>Cabe "Visual Studio Code" ou "7-Zip File Manager" na caixa do aplicativo padrão, ou "12345 pastas 12345 arquivos".</summary>
    public const double Type = 160;

    /// <summary>Cabe "04/11/2026" e "23:22" lado a lado, com folga.</summary>
    public const double Date = 120;

    /// <summary>Cabe "1.023,99 MB" com folga.</summary>
    public const double Size = 80;

    /// <summary>Margem à esquerda das colunas Modificado e Tamanho.</summary>
    public const double Gap = 8;

    public const double MaxName = 4000;

    /// <summary>Borda (1) e espaçamento interno (4) à direita do item da árvore: o conteúdo das linhas termina antes da borda.</summary>
    public const double RowRightInset = 5;

    /// <summary>Largura guardada do Nome: 0 (ou inválida) é "automática" (o que sobrar no painel).</summary>
    public static double ClampName(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) ? 0 : Math.Clamp(width, 0, MaxName);

    /// <summary>Largura do Nome depois de arrastar <paramref name="horizontalChange"/> pixels (positivo = para a direita, alarga).</summary>
    public static double AfterNameDrag(double startWidth, double horizontalChange) =>
        ClampName(startWidth + horizontalChange);

    /// <summary>As colunas à direita do Nome (as três fixas, as duas margens e a reserva para a estrela e a marca de seleção).</summary>
    public static double FixedColumnsWidth(double trailingWidth) => Type + Date + Size + (2 * Gap) + trailingWidth;

    /// <summary>
    /// Fora o recuo dos níveis: a seta de expandir (16 + 4), o ícone (16 + 8) e a borda do item (1) antes do nome, e a margem de
    /// 8 depois dele (medido na tela: a coluna Nome do cabeçalho tem 91 px a mais que o texto numa linha do nível 2).
    /// </summary>
    public const double NameLeadingWidth = 53;

    /// <summary>Recuo de cada nível da árvore.</summary>
    public const double IndentPerLevel = 19;

    /// <summary>
    /// O que há à esquerda do nome (recuo dos níveis, ícone e margem) na linha mais funda: a coluna Nome do cabeçalho tem esta
    /// largura a mais que a largura mínima do texto do nome.
    /// </summary>
    public static double NameOverhead(int deepestLevel) => (Math.Max(0, deepestLevel) * IndentPerLevel) + NameLeadingWidth;

    /// <summary>
    /// A largura em que o arrasto da borda do Nome começa: a que já estava guardada ou a que a coluna tem na tela (a maior), para
    /// a coluna acompanhar o mouse desde o primeiro pixel em vez de esperar o arrasto passar da sobra do painel.
    /// </summary>
    public static double NameDragStart(double storedWidth, double visibleColumnWidth) =>
        ClampName(Math.Max(storedWidth, visibleColumnWidth));

    /// <summary>
    /// Largura mínima da célula do nome de uma linha do nível <paramref name="depth"/>: a largura da linha mais larga da lista
    /// (<paramref name="rowWidth"/>, com o recuo e o ícone) menos o que fica à esquerda do nome naquela linha. Com todas as
    /// linhas assim, a largura natural delas é a mesma e a extensão rolável não muda quando a lista rola.
    /// </summary>
    public static double NameCellMinWidth(double rowWidth, int depth) =>
        double.IsNaN(rowWidth) || rowWidth <= 0 ? 0 : Math.Max(0, rowWidth - NameOverhead(depth));
}
