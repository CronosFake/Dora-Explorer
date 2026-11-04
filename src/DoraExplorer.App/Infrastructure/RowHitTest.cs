namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Descobre qual linha de uma árvore está sob uma altura (<c>y</c>). É a regra de "onde o mouse está" usada ao soltar itens e ao
/// começar a seleção em retângulo, separada da tela para poder ser testada: só olha a faixa da LINHA de cada item (o cabeçalho),
/// nunca a faixa dos filhos.
/// </summary>
internal static class RowHitTest
{
    /// <summary>
    /// A linha cuja faixa contém <paramref name="y"/>, ou <see langword="null"/> se nenhuma (espaço em branco abaixo da última
    /// linha, entre colunas...). Uma pasta aberta é ela mesma quando <c>y</c> está na sua linha: antes, a busca descia para os
    /// filhos e devolvia o filho mais próximo, então soltar um arquivo sobre a linha de uma unidade aberta (C:, D:, um disco
    /// externo) o jogava dentro da primeira subpasta e nunca na raiz.
    /// </summary>
    public static T? FindExact<T>(
        IEnumerable<T> rows,
        double y,
        Func<T, (double Top, double Bottom)> header,
        Func<T, IEnumerable<T>> children)
        where T : class
    {
        foreach (var row in rows)
        {
            var (top, bottom) = header(row);
            if (y >= top && y <= bottom)
            {
                return row;
            }

            if (FindExact(children(row), y, header, children) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
