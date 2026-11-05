namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Larguras do modo "Linhas compactas em coluna". Tipo (aplicativo padrão e contagens), Modificado (data e hora) e Tamanho têm
/// largura FIXA e folgada: mostram sempre o conteúdo inteiro e não mudam com o painel nem com a rolagem. Só o Nome se ajusta
/// (arrastando a borda direita dele no cabeçalho). Sem ajuste ele abre automático: do tamanho do maior nome da lista (ou do que
/// sobra no painel, se for maior). Depois de arrastar a largura é a do usuário, maior OU MENOR que a automática (nomes que não
/// cabem terminam em reticências); as colunas à direita acompanham a borda dele, e o que passar da largura do painel vira rolagem
/// horizontal. Tudo puro (sem tela) para ser testado.
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

    /// <summary>Menor largura que o usuário pode dar ao Nome (cabe o título "Nome ▲" e algumas letras).</summary>
    public const double MinName = 100;

    /// <summary>Borda (1) e espaçamento interno (4) à direita do item da árvore: o conteúdo das linhas termina antes da borda.</summary>
    public const double RowRightInset = 5;

    /// <summary>
    /// Largura guardada do Nome: 0 (ou inválida) é "automática"; qualquer outra fica entre <see cref="MinName"/> e
    /// <see cref="MaxName"/>.
    /// </summary>
    public static double ClampName(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? 0 : Math.Clamp(width, MinName, MaxName);

    /// <summary>
    /// Largura do Nome depois de arrastar <paramref name="horizontalChange"/> pixels (positivo = para a direita, alarga; negativo
    /// diminui). Fica entre <see cref="MinName"/> e <see cref="MaxName"/>: arrastar tudo para a esquerda NÃO volta ao automático
    /// (isso é o duplo clique), senão a coluna saltaria de largura no meio do arrasto.
    /// </summary>
    public static double AfterNameDrag(double startWidth, double horizontalChange) =>
        double.IsNaN(startWidth) || double.IsInfinity(startWidth) || double.IsNaN(horizontalChange)
            ? MinName
            : Math.Clamp(startWidth + horizontalChange, MinName, MaxName);

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
    /// A largura em que o arrasto da borda do Nome começa: a guardada, se o usuário já ajustou; senão a que a coluna automática
    /// tem na tela. Assim a coluna acompanha o mouse desde o primeiro pixel, para os dois lados.
    /// </summary>
    public static double NameDragStart(double storedWidth, double visibleColumnWidth) =>
        storedWidth > 0 ? ClampName(storedWidth) : Math.Clamp(double.IsNaN(visibleColumnWidth) ? MinName : visibleColumnWidth, MinName, MaxName);

    /// <summary>O ícone (16 + margem 8) e a margem de 8 depois do nome: o que a grade da linha tem além do nome e das colunas.</summary>
    public const double ContentLeadingWidth = 32;

    /// <summary>
    /// Largura da grade de conteúdo de uma linha (nível <paramref name="depth"/>) quando o usuário ajustou o Nome para
    /// <paramref name="manualName"/>: o nome (a largura da coluna menos o que fica à esquerda dele na linha), as colunas fixas e
    /// o ícone com as margens. Com esta largura fixa e alinhada à esquerda, as colunas à direita acompanham a borda do Nome e o
    /// nome que não cabe termina em reticências.
    /// </summary>
    public static double NameRowContentWidth(double manualName, double fixedColumnsWidth, int depth) =>
        Math.Max(0, manualName - NameOverhead(depth)) + fixedColumnsWidth + ContentLeadingWidth;

    /// <summary>
    /// Largura mínima da célula do nome de uma linha do nível <paramref name="depth"/>: a largura da linha mais larga da lista
    /// (<paramref name="rowWidth"/>, com o recuo e o ícone) menos o que fica à esquerda do nome naquela linha. Com todas as
    /// linhas assim, a largura natural delas é a mesma e a extensão rolável não muda quando a lista rola.
    /// </summary>
    public static double NameCellMinWidth(double rowWidth, int depth) =>
        double.IsNaN(rowWidth) || rowWidth <= 0 ? 0 : Math.Max(0, rowWidth - NameOverhead(depth));
}
