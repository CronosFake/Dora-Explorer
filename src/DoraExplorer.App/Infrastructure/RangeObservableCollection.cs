using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Coleção observável que troca todo o conteúdo com uma única notificação. Adicionar milhares de itens um a um a uma
/// coleção ligada a uma árvore na tela custa caro (cada item dispara o trabalho da ligação), e numa pasta de
/// 100 mil arquivos isso levava mais de 3 minutos; uma só troca leva uma fração de segundo.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Acima disso, uma troca única custa menos que milhares de avisos de remoção e inserção.</summary>
    public const int SyncToLimit = 1000;

    /// <summary>
    /// Leva a coleção ao conteúdo de <paramref name="target"/> mexendo só no que mudou (remoções, inserções e
    /// movimentos pontuais). Uma lista idêntica não gera aviso algum, e os itens que ficam não são recriados na
    /// tela: trocar tudo (limpar e adicionar de novo) faz a árvore descartar os containers e os itens piscam.
    /// Os itens são comparados pelo operador de igualdade padrão e não podem se repetir.
    /// </summary>
    public void SyncTo(IReadOnlyList<T> target)
    {
        var wanted = new HashSet<T>(target);
        if (target.Count > SyncToLimit || Count > SyncToLimit || wanted.Count != target.Count)
        {
            ReplaceAll(target);
            return;
        }

        for (var i = Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(this[i]))
            {
                RemoveAt(i);
            }
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Count && comparer.Equals(this[i], target[i]))
            {
                continue;
            }

            var from = -1;
            for (var j = i + 1; j < Count; j++)
            {
                if (comparer.Equals(this[j], target[i]))
                {
                    from = j;
                    break;
                }
            }

            if (from >= 0)
            {
                Move(from, i);
            }
            else
            {
                Insert(i, target[i]);
            }
        }
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
