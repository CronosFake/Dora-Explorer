namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Quanto a lista anda por clique da roda do mouse. O passo é de <see cref="RowsPerNotch"/> linhas em qualquer modo de
/// linha: no compacto (linhas de 32 px) são 64 px, como sempre foi, e no confortável, onde a linha de arquivo mede 54 px,
/// são 108 px. Antes o passo era fixo em 64 px, o que no confortável era só 1,2 linha e dava a impressão de rolar mais
/// devagar.
/// </summary>
internal static class WheelScroll
{
    /// <summary>Linhas por clique (2/3 do padrão de 3 linhas do Windows).</summary>
    public const int RowsPerNotch = 2;

    /// <summary>
    /// Pixels a somar ao destino da rolagem para um giro de <paramref name="wheelDelta"/> (120 = um clique, positivo = roda
    /// para cima = diminui o deslocamento). Escala linear, para as rodas de alta resolução que mandam fração de clique.
    /// </summary>
    public static double StepPixels(int wheelDelta, double rowHeight) => -(wheelDelta / 120.0) * RowsPerNotch * rowHeight;

    /// <summary>
    /// Altura da linha que está no topo da lista rolada até <paramref name="offset"/>, somando as alturas na ordem em que as
    /// linhas aparecem (a primeira começa em <paramref name="topPadding"/>). Lê só até achar: numa pasta com 100 mil
    /// arquivos o custo é o de chegar até a posição, não o de percorrer tudo. Depois do fim vale a altura da última linha, e
    /// sem nenhuma linha vale <paramref name="fallback"/>.
    /// </summary>
    public static double RowHeightAtOffset(IEnumerable<double> rowHeights, double offset, double topPadding, double fallback)
    {
        var top = topPadding;
        var last = fallback;
        foreach (var height in rowHeights)
        {
            last = height;
            if (offset < top + height)
            {
                return height;
            }

            top += height;
        }

        return last;
    }
}
