using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// A largura da linha mais larga da lista INTEIRA (todas as pastas abertas, e não só as linhas que aparecem na tela). A rolagem
/// horizontal do WPF só conhece as linhas que estão desenhadas: com a virtualização, descer a lista mudava a largura rolável e a
/// barra horizontal andava junto. Com esta largura em todas as linhas, a barra fica sempre do tamanho do maior nome.
/// </summary>
public static class NameWidthScanner
{
    /// <summary>
    /// Em cada pasta só os nomes mais compridos (por número de letras) são medidos: medir cada nome de uma pasta com 100 mil
    /// arquivos custaria segundos, e o mais largo está entre os mais compridos (a diferença de largura entre letras é pequena).
    /// </summary>
    public const int CandidatesPerFolder = 4;

    /// <summary>
    /// Há alguma linha das colunas (pasta, arquivo, arquivo compactado) visível? Só as raízes (unidades, Favoritos, Lixeira, Rede)
    /// e os protocolos da Rede não contam: sem nenhuma pasta aberta o cabeçalho das colunas fica vazio.
    /// </summary>
    public static bool HasColumnRows(IEnumerable<NavigationTreeNodeViewModel> roots) => roots.Any(AnyColumnRowBelow);

    private static bool AnyColumnRowBelow(NavigationTreeNodeViewModel node)
    {
        if (!node.IsExpanded)
        {
            return false;
        }

        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            if (child.ParticipatesInColumns || AnyColumnRowBelow(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A largura (recuo, ícone e texto do nome) da linha mais larga entre as pastas abertas. <paramref name="measureText"/>
    /// mede o texto do nome em pixels. Não conta as raízes nem os protocolos da Rede (<see cref="NavigationTreeNodeViewModel.ParticipatesInColumns"/>):
    /// a largura do Nome não tem influência sobre eles.
    /// </summary>
    public static double WidestRowWidth(IEnumerable<NavigationTreeNodeViewModel> roots, Func<string, double> measureText)
    {
        var widest = 0.0;
        foreach (var root in roots)
        {
            Visit(root, 0, measureText, ref widest);
        }

        return widest;
    }

    // O nível vem da própria descida (a raiz é 0): o TreeDepth dos nós só é preenchido pela tela depois de a pasta carregar, e
    // um nível desatualizado subestimaria o recuo por um instante.
    private static void Visit(NavigationTreeNodeViewModel folder, int depth, Func<string, double> measureText, ref double widest)
    {
        if (!folder.IsExpanded || folder.Children.Count == 0)
        {
            return;
        }

        var best = new NavigationTreeNodeViewModel?[CandidatesPerFolder];
        var bestLength = new int[CandidatesPerFolder];
        foreach (var child in folder.Children)
        {
            if (child.IsPlaceholder || string.IsNullOrEmpty(child.Name))
            {
                continue;
            }

            if (child.ParticipatesInColumns)
            {
                Offer(best, bestLength, child);
            }

            if (child.IsExpanded && child.Children.Count > 0)
            {
                Visit(child, depth + 1, measureText, ref widest);
            }
        }

        foreach (var candidate in best)
        {
            if (candidate is null)
            {
                continue;
            }

            var width = PaneColumnWidths.NameOverhead(depth + 1) + measureText(candidate.Name);
            if (width > widest)
            {
                widest = width;
            }
        }
    }

    // mantém os CandidatesPerFolder nomes mais compridos vistos até agora
    private static void Offer(NavigationTreeNodeViewModel?[] best, int[] bestLength, NavigationTreeNodeViewModel child)
    {
        var length = child.Name.Length;
        var slot = -1;
        for (var i = 0; i < best.Length; i++)
        {
            if (best[i] is null)
            {
                slot = i;
                break;
            }

            if (bestLength[i] < length && (slot < 0 || bestLength[i] < bestLength[slot]))
            {
                slot = i;
            }
        }

        if (slot >= 0)
        {
            best[slot] = child;
            bestLength[slot] = length;
        }
    }
}
