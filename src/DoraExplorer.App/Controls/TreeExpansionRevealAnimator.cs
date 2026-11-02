using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Controls;

/// <summary>
/// Revela filhos de uma pasta expandida com animação em cascata (de cima para baixo).
/// </summary>
public static class TreeExpansionRevealAnimator
{
    private sealed class RevealState
    {
        public int SessionId;
        public int RevealedCount;
        public int HideStamp;
    }

    /// <summary>Quantos filhos entram em cascata com atraso próprio; do 49º em diante entram junto com o 48º.</summary>
    private const int MaxStaggeredChildren = 48;

    private static readonly ConditionalWeakTable<TreeViewItem, RevealState> States = new();
    private static readonly ConditionalWeakTable<TreeViewItem, Storyboard> Running = new();
    private static readonly ConditionalWeakTable<TreeViewItem, Storyboard> Collapsing = new();
    private static readonly ConditionalWeakTable<TreeViewItem, DoubleAnimation> Growing = new();

    public static bool IsCollapsing(TreeViewItem parent) => Collapsing.TryGetValue(parent, out _);

    /// <summary>
    /// Cancela uma animação de colapso em curso e restaura os filhos (usado quando o
    /// utilizador volta a clicar no expander enquanto os filhos estão a sair).
    /// </summary>
    public static void CancelCollapse(TreeViewItem parent)
    {
        if (Collapsing.TryGetValue(parent, out var storyboard))
        {
            storyboard.Stop();
            Collapsing.Remove(parent);
        }

        if (FindItemsHost(parent) is FrameworkElement itemsHost)
        {
            RestoreItemsHost(itemsHost);
        }

        StopAnimations(parent);
    }

    /// <summary>
    /// Anima o fecho do container dos filhos (altura + opacidade a encolher), de modo a
    /// que as linhas abaixo subam DURANTE a animação, e invoca <paramref name="onCompleted"/>
    /// no fim, onde o colapso real (IsExpanded=false) deve ser aplicado.
    /// </summary>
    public static void CollapseChildren(TreeViewItem parent, Action onCompleted)
    {
        if (!parent.IsExpanded)
        {
            onCompleted();
            return;
        }

        parent.UpdateLayout();

        if (FindItemsHost(parent) is not FrameworkElement itemsHost || itemsHost.ActualHeight <= 0)
        {
            onCompleted();
            return;
        }

        if (Running.TryGetValue(parent, out var pending))
        {
            pending.Stop();
            Running.Remove(parent);
        }

        StopAnimations(parent);

        var startHeight = itemsHost.ActualHeight;
        itemsHost.ClipToBounds = true;

        if (Growing.Remove(parent))
        {
            // Fechar no meio da abertura: parte da altura que a lista já tem, sem salto.
            itemsHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            itemsHost.Height = startHeight;
        }

        var storyboard = new Storyboard();
        Timeline.SetDesiredFrameRate(storyboard, AnimationFrameRateService.Rate);

        // EaseIn: o container acelera a fechar e termina de forma crisp, para que as linhas
        // acabem de subir exatamente quando IsExpanded=false (seta volta + linha some juntas),
        // sem o "arrasto" final que o EaseOut deixava.
        var heightAnim = new DoubleAnimation
        {
            From = startHeight,
            To = 0,
            Duration = AnimationFrameRateService.ChildRevealDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(heightAnim, itemsHost);
        Storyboard.SetTargetProperty(heightAnim, new PropertyPath(FrameworkElement.HeightProperty));

        var opacityAnim = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = AnimationFrameRateService.ChildRevealDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opacityAnim, itemsHost);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));

        storyboard.Children.Add(heightAnim);
        storyboard.Children.Add(opacityAnim);

        storyboard.Completed += (_, _) =>
        {
            Collapsing.Remove(parent);
            onCompleted();
            RestoreItemsHost(itemsHost);
        };

        Collapsing.Add(parent, storyboard);
        storyboard.Begin();
    }

    private static FrameworkElement? FindItemsHost(TreeViewItem parent) =>
        parent.Template?.FindName("ItemsHost", parent) as FrameworkElement;

    private static void RestoreItemsHost(FrameworkElement itemsHost)
    {
        itemsHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        itemsHost.BeginAnimation(UIElement.OpacityProperty, null);
        itemsHost.Height = double.NaN;
        itemsHost.Opacity = 1;
        itemsHost.ClipToBounds = false;
    }

    /// <summary>
    /// Esconde os filhos já no primeiro desenho depois de abrir a pasta. Montar os itens custa algumas centenas de
    /// milissegundos de layout e o WPF desenha antes dos callbacks que ajustam o recuo: sem isto os filhos
    /// aparecem por um quadro sem recuo e a animação os esconde depois (o "pisca"). A lista fica com altura 0 e não só
    /// transparente: transparente com altura cheia segurava o espaço de todos os filhos e as linhas abaixo da pasta
    /// sumiam de uma vez (empurradas para fora da tela) até a cascata terminar, e isso também era o "pisca". Se a
    /// revelação não acontecer, um temporizador de segurança devolve a lista ao normal.
    /// </summary>
    public static void HideForReveal(TreeViewItem parent)
    {
        if (FindItemsHost(parent) is not FrameworkElement host)
        {
            return;
        }

        Growing.Remove(parent);
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        host.ClipToBounds = true;
        host.Height = 0;
        host.Opacity = 0;

        var state = States.GetOrCreateValue(parent);
        var stamp = ++state.HideStamp;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // Só vale para o esconder que a criou: um segundo HideForReveal da mesma pasta tem o seu próprio prazo.
            if (state.HideStamp == stamp && !Collapsing.TryGetValue(parent, out _) && IsHiddenForReveal(host))
            {
                RestoreItemsHost(host);
            }
        };
        timer.Start();
    }

    /// <summary>Garante que a lista não ficou escondida: cada filho já foi posto em opacidade 0 pela revelação em cascata.</summary>
    public static void ShowAfterReveal(TreeViewItem parent)
    {
        if (FindItemsHost(parent) is FrameworkElement host &&
            !Collapsing.TryGetValue(parent, out _) &&
            !Growing.TryGetValue(parent, out _))
        {
            RestoreItemsHost(host);
        }
    }

    private static bool IsHiddenForReveal(FrameworkElement host) => host.Opacity == 0 && host.Height == 0;

    /// <summary>
    /// A lista dos filhos cresce de 0 até a altura dos filhos que o WPF criou (os que aparecem na tela) e as linhas abaixo
    /// da pasta descem junto (o fechar já faz o contrário). A duração acompanha a cascata: cada filho entra 26 ms depois do
    /// anterior, então com muitos filhos a lista abre na velocidade em que as linhas aparecem, e com poucos dura o tempo de
    /// um filho. Numa pasta enorme só o que cabe na tela importa: o resto está fora de vista e entra no fim.
    /// </summary>
    private static void GrowItemsHost(TreeViewItem parent, FrameworkElement host, int revealedCount)
    {
        var natural = host.ActualHeight;
        var shown = 0.0;
        foreach (var child in TreeContainers.Realized(parent))
        {
            if (child.DataContext is not NavigationTreeNodeViewModel { IsPlaceholder: true })
            {
                shown += child.ActualHeight;
            }
        }

        var target = Math.Min(natural, shown);
        if (target <= 1)
        {
            RestoreItemsHost(host);
            return;
        }

        var cascadeMs = Math.Min(revealedCount, MaxStaggeredChildren) * AnimationFrameRateService.ChildRevealStaggerMs;
        var durationMs = Math.Max(cascadeMs, AnimationFrameRateService.ChildRevealDuration.TimeSpan.TotalMilliseconds);

        var animation = new DoubleAnimation
        {
            From = 0,
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, AnimationFrameRateService.Rate);
        animation.Completed += (_, _) =>
        {
            // Fechar ou reabrir no meio troca ou cancela a animação: só a que ainda está em curso restaura a lista.
            if (Growing.TryGetValue(parent, out var current) && ReferenceEquals(current, animation))
            {
                Growing.Remove(parent);
                RestoreItemsHost(host);
            }
        };

        host.Height = 0;
        host.ClipToBounds = true;
        host.Opacity = 1;
        Growing.AddOrUpdate(parent, animation);
        host.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    public static void RevealChildren(TreeViewItem parent, int sessionId)
    {
        if (!parent.IsExpanded)
        {
            return;
        }

        // HideForReveal deixou a lista com altura 0. Solta para medir o tamanho real dos filhos (nada é desenhado antes
        // do fim deste método) e, com a cascata pronta, GrowItemsHost volta a 0 e faz a lista crescer.
        FrameworkElement? hiddenHost = null;
        if (FindItemsHost(parent) is FrameworkElement host && IsHiddenForReveal(host))
        {
            hiddenHost = host;
            host.Height = double.NaN;
        }

        parent.UpdateLayout();

        var state = States.GetOrCreateValue(parent);
        if (state.SessionId != sessionId)
        {
            state.SessionId = sessionId;
            state.RevealedCount = 0;
            StopAnimations(parent);
        }

        var visibleIndex = 0;
        foreach (var child in TreeContainers.Realized(parent))
        {
            if (child.DataContext is NavigationTreeNodeViewModel { IsPlaceholder: true })
            {
                continue;
            }

            if (visibleIndex < state.RevealedCount)
            {
                visibleIndex++;
                continue;
            }

            PlayReveal(child, visibleIndex);
            visibleIndex++;
        }

        state.RevealedCount = visibleIndex;

        if (hiddenHost is not null)
        {
            GrowItemsHost(parent, hiddenHost, visibleIndex);
        }
    }

    public static void Reset(TreeViewItem parent)
    {
        StopAnimations(parent);
        States.Remove(parent);

        // Fechou no meio da abertura (ou antes de ela começar): a lista volta ao normal para a próxima vez.
        var growing = Growing.Remove(parent);
        if (FindItemsHost(parent) is FrameworkElement host && (growing || IsHiddenForReveal(host)))
        {
            RestoreItemsHost(host);
        }
    }

    private static void StopAnimations(TreeViewItem parent)
    {
        foreach (var child in TreeContainers.Realized(parent))
        {
            if (Running.TryGetValue(child, out var storyboard))
            {
                storyboard.Stop();
                Running.Remove(child);
            }

            child.BeginAnimation(UIElement.OpacityProperty, null);
            if (child.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0;
            }

            child.Opacity = 1;
        }
    }

    private static void PlayReveal(TreeViewItem item, int index)
    {
        if (Running.TryGetValue(item, out var existing))
        {
            existing.Stop();
            Running.Remove(item);
        }

        item.RenderTransformOrigin = new Point(0, 0);
        var translate = item.RenderTransform as TranslateTransform;
        if (translate is null)
        {
            translate = new TranslateTransform(0, AnimationFrameRateService.ChildRevealOffsetY);
            item.RenderTransform = translate;
        }
        else
        {
            translate.Y = AnimationFrameRateService.ChildRevealOffsetY;
        }

        item.Opacity = 0;

        var beginMs = Math.Min(
            index * AnimationFrameRateService.ChildRevealStaggerMs,
            MaxStaggeredChildren * AnimationFrameRateService.ChildRevealStaggerMs);

        var storyboard = new Storyboard();
        Timeline.SetDesiredFrameRate(storyboard, AnimationFrameRateService.Rate);

        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = AnimationFrameRateService.ChildRevealDuration,
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, item);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));

        var slide = new DoubleAnimation
        {
            From = AnimationFrameRateService.ChildRevealOffsetY,
            To = 0,
            Duration = AnimationFrameRateService.ChildRevealDuration,
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, item);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        storyboard.Children.Add(opacity);
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) =>
        {
            item.Opacity = 1;
            translate.Y = 0;
            Running.Remove(item);
        };

        Running.Add(item, storyboard);
        storyboard.Begin();
    }
}
