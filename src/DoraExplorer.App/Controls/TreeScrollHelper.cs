using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Controls;

/// <summary>
/// O que a estimativa de posição precisa saber sobre a altura das linhas: se as linhas são confortáveis (arquivo com o rodapé
/// de data e tamanho sob o nome) e, quando já existe uma linha de arquivo desenhada, a altura real dela.
/// </summary>
public readonly record struct TreeRowMetrics(bool ComfortableRows, double? FileRowHeight = null);

public static class TreeScrollHelper
{
    private const double ViewportPadding = 6;
    private const double TreePaddingTop = 4;

    /// <summary>Altura do rodapé de metadados do modo confortável (linha de 32 px passa a 48 px). Só vale sem medida real.</summary>
    public const double MetadataFooterHeight = 16;

    public static void EnsureItemVisible(TreeViewItem item, ScrollViewer scrollViewer, bool animate = false)
    {
        if (!TryGetScrollMetrics(item, scrollViewer, out var itemTop, out var itemBottom, out var viewportTop, out var viewportBottom))
        {
            return;
        }

        if (itemTop >= viewportTop + ViewportPadding &&
            itemBottom <= viewportBottom - ViewportPadding)
        {
            return;
        }

        ScrollToShowRange(scrollViewer, itemTop, itemBottom, viewportTop, viewportBottom, animate);
    }

    public static void CenterItemInViewport(TreeViewItem item, ScrollViewer scrollViewer, bool animate = false) =>
        TryCenterItemInViewport(item, scrollViewer, animate);

    public static bool TryCenterItemInViewport(TreeViewItem item, ScrollViewer scrollViewer, bool animate = false)
    {
        item.UpdateLayout();
        scrollViewer.UpdateLayout();

        if (scrollViewer.ViewportHeight <= 0)
        {
            return false;
        }

        if (TryCenterUsingViewportDelta(item, scrollViewer, animate))
        {
            return true;
        }

        return TryCenterUsingContentSpace(item, scrollViewer, animate);
    }

    /// <summary>
    /// Centralização para o Localizar: estima o offset pela árvore de ViewModels (funciona com
    /// virtualização) e depois ajusta com a posição real do container.
    /// </summary>
    public static bool TryCenterSearchItem(
        TreeViewItem item,
        NavigationTreeNodeViewModel target,
        IEnumerable<NavigationTreeNodeViewModel> roots,
        ScrollViewer scrollViewer,
        TreeRowMetrics metrics)
    {
        scrollViewer.UpdateLayout();
        item.UpdateLayout();

        // O container do alvo já existe e está desenhado: a posição real dele na tela é exata e basta centralizar por ela.
        // Antes a estimativa vinha primeiro e, quando errava (linhas mais altas que o previsto), o scroll arrastava o alvo
        // para longe e o container deixava de existir.
        if (TryCenterItemInViewport(item, scrollViewer, animate: false))
        {
            return true;
        }

        if (scrollViewer.ViewportHeight > 0 &&
            TryEstimateTargetOffset(roots, target, metrics, out var itemTop))
        {
            var centerY = TreePaddingTop + itemTop + (GetEstimatedRowHeight(target, metrics) / 2);
            var targetOffset = centerY - (scrollViewer.ViewportHeight / 2);
            targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
            ApplyScrollOffset(scrollViewer, targetOffset, animate: false);
        }

        scrollViewer.UpdateLayout();
        item.UpdateLayout();
        return TryCenterItemInViewport(item, scrollViewer, animate: false);
    }

    /// <summary>
    /// Pede ao painel da pasta (o VirtualizingStackPanel dos filhos dela) que traga o filho de índice
    /// <paramref name="childIndex"/> para a tela e crie o container dele. É por índice, então não depende de contas em
    /// pixels (o VirtualizingStackPanel refaz a estimativa do tamanho total a cada linha que mede, e um offset calculado em
    /// pixels cai em outra linha). Devolve false se a pasta ainda não tem painel de filhos.
    /// </summary>
    public static bool TryBringChildIntoView(TreeViewItem parentItem, int childIndex, ScrollViewer scrollViewer)
    {
        if (childIndex < 0 || FindItemsHostPanel(parentItem) is not { } panel)
        {
            return false;
        }

        try
        {
            panel.BringIndexIntoViewPublic(childIndex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
            return false;
        }

        scrollViewer.UpdateLayout();
        return true;
    }

    private static VirtualizingPanel? FindItemsHostPanel(TreeViewItem owner)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(owner);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is VirtualizingPanel panel && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            {
                return panel;
            }

            // não desce para dentro das subpastas: o painel que procuramos é o dos filhos diretos
            if (!ReferenceEquals(current, owner) && current is TreeViewItem)
            {
                continue;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        return null;
    }

    /// <summary>
    /// Pula o scroll para perto do alvo usando como régua uma linha IRMÃ dele que já está desenhada (por exemplo as do topo
    /// da pasta recém-aberta): a posição na tela e a altura dela são reais, então a distância até o alvo (diferença de
    /// índices vezes a altura) não depende de constante nenhuma. Devolve false quando nenhuma irmã está desenhada.
    /// </summary>
    public static bool TryJumpUsingRealizedSibling(
        DependencyObject treeRoot,
        NavigationTreeNodeViewModel target,
        NavigationTreeNodeViewModel parent,
        ScrollViewer scrollViewer)
    {
        scrollViewer.UpdateLayout();
        if (scrollViewer.ViewportHeight <= 0)
        {
            return false;
        }

        var targetIndex = parent.Children.IndexOf(target);
        if (targetIndex < 0)
        {
            return false;
        }

        TreeViewItem? reference = null;
        var referenceIndex = -1;
        var pending = new Stack<DependencyObject>();
        pending.Push(treeRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is TreeViewItem row &&
                row.ActualHeight > 0 &&
                row.DataContext is NavigationTreeNodeViewModel node &&
                !ReferenceEquals(node, target) &&
                (node.Children.Count == 0 || !node.IsExpanded))
            {
                // linha de uma pasta aberta não serve: o ActualHeight dela inclui os filhos
                var index = parent.Children.IndexOf(node);
                if (index >= 0 &&
                    (reference is null || Math.Abs(index - targetIndex) < Math.Abs(referenceIndex - targetIndex)))
                {
                    reference = row;
                    referenceIndex = index;
                }
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        if (reference is null)
        {
            return false;
        }

        double referenceTop;
        try
        {
            referenceTop = reference.TranslatePoint(new Point(0, 0), scrollViewer).Y;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        var rowHeight = reference.ActualHeight;
        var targetCenter = referenceTop + ((targetIndex - referenceIndex) * rowHeight) + (rowHeight / 2);
        var offset = scrollViewer.VerticalOffset + targetCenter - (scrollViewer.ViewportHeight / 2);
        offset = Math.Clamp(offset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
        ApplyScrollOffset(scrollViewer, offset, animate: false);
        return true;
    }

    /// <summary>
    /// Distância (em pixels, do topo da árvore) até o topo da linha do alvo, somando a altura de cada linha visível antes
    /// dele. As linhas de arquivo só têm o rodapé de metadados no modo confortável: no compacto (o padrão) a linha mede só
    /// <see cref="TreeViewHierarchy.HeaderRowHeight"/>, e contar o rodapé a mais fazia a estimativa passar muito do alvo em
    /// pastas grandes (1777 arquivos acima: cerca de 35 mil pixels de erro). No confortável, a altura real de uma linha já
    /// desenhada (<see cref="TreeRowMetrics.FileRowHeight"/>) vale mais que a constante.
    /// </summary>
    public static bool TryEstimateTargetOffset(
        IEnumerable<NavigationTreeNodeViewModel> roots,
        NavigationTreeNodeViewModel target,
        TreeRowMetrics metrics,
        out double offsetTop)
    {
        offsetTop = 0;
        return WalkVisibleNodes(roots, target, metrics, ref offsetTop);
    }

    public static void JumpScrollNearTarget(
        NavigationTreeNodeViewModel target,
        IEnumerable<NavigationTreeNodeViewModel> roots,
        ScrollViewer scrollViewer,
        TreeRowMetrics metrics)
    {
        scrollViewer.UpdateLayout();

        if (scrollViewer.ViewportHeight <= 0 ||
            !TryEstimateTargetOffset(roots, target, metrics, out var itemTop))
        {
            return;
        }

        var centerY = TreePaddingTop + itemTop + (GetEstimatedRowHeight(target, metrics) / 2);
        var targetOffset = centerY - (scrollViewer.ViewportHeight / 2);
        targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
        ApplyScrollOffset(scrollViewer, targetOffset, animate: false);
    }

    private static bool WalkVisibleNodes(
        IEnumerable<NavigationTreeNodeViewModel> nodes,
        NavigationTreeNodeViewModel target,
        TreeRowMetrics metrics,
        ref double y)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder)
            {
                continue;
            }

            if (ReferenceEquals(node, target))
            {
                return true;
            }

            y += GetEstimatedRowHeight(node, metrics);

            if (node.IsExpanded && node.Children.Count > 0 &&
                WalkVisibleNodes(node.Children, target, metrics, ref y))
            {
                return true;
            }
        }

        return false;
    }

    public static double GetEstimatedRowHeight(NavigationTreeNodeViewModel node, TreeRowMetrics metrics)
    {
        var height = TreeViewHierarchy.HeaderRowHeight;

        // O rodapé (tamanho/data abaixo do nome) só aparece com linhas confortáveis; no modo compacto os metadados ficam
        // nas colunas da própria linha.
        if (metrics.ComfortableRows && node.ShowFileMetadataFooter)
        {
            height = metrics.FileRowHeight is { } measured and > 0
                ? measured
                : height + MetadataFooterHeight;
        }

        if (node.IsNavigationRoot)
        {
            height += TreeViewHierarchy.ItemsPresenterGap;
        }

        return height;
    }

    private static bool TryCenterUsingViewportDelta(TreeViewItem item, ScrollViewer scrollViewer, bool animate)
    {
        if (!TryGetScrollMetrics(item, scrollViewer, out var itemTop, out var itemBottom, out _, out _))
        {
            return false;
        }

        var itemCenter = (itemTop + itemBottom) / 2;
        var viewportCenter = scrollViewer.ViewportHeight / 2;
        var delta = itemCenter - viewportCenter;

        if (Math.Abs(delta) < 0.5)
        {
            return true;
        }

        var targetOffset = scrollViewer.VerticalOffset + delta;
        targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
        ApplyScrollOffset(scrollViewer, targetOffset, animate);
        return true;
    }

    private static bool TryCenterUsingContentSpace(TreeViewItem item, ScrollViewer scrollViewer, bool animate)
    {
        var element = item.Template?.FindName("ItemBorder", item) is FrameworkElement header
                      && header.IsVisible
                      && header.ActualHeight > 0
            ? header
            : item;

        if (scrollViewer.Content is not Visual contentRoot)
        {
            return false;
        }

        try
        {
            var topLeft = element.TransformToAncestor(contentRoot).Transform(new Point(0, 0));
            var itemHeight = element.ActualHeight > 0
                ? element.ActualHeight
                : TreeViewHierarchy.HeaderRowHeight;
            var itemCenterY = topLeft.Y + itemHeight / 2;
            var targetOffset = itemCenterY - scrollViewer.ViewportHeight / 2;
            targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
            ApplyScrollOffset(scrollViewer, targetOffset, animate);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static void ApplyScrollOffset(ScrollViewer scrollViewer, double targetOffset, bool animate)
    {
        if (animate)
        {
            SmoothScrollAnimator.ScrollTo(scrollViewer, targetOffset);
            return;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        SmoothScrollAnimator.SyncTarget(scrollViewer, targetOffset);
    }

    /// <summary>
    /// Scroll mínimo para navegação por teclado: não reposiciona itens já visíveis no viewport.
    /// </summary>
    public static void EnsureItemVisibleForKeyboard(TreeViewItem item, ScrollViewer scrollViewer)
    {
        if (!TryGetScrollMetrics(item, scrollViewer, out var itemTop, out var itemBottom, out var viewportTop, out var viewportBottom))
        {
            return;
        }

        if (itemBottom > viewportTop + ViewportPadding && itemTop < viewportBottom - ViewportPadding)
        {
            return;
        }

        ScrollToShowRange(scrollViewer, itemTop, itemBottom, viewportTop, viewportBottom, animate: false);
    }

    private static bool TryGetScrollMetrics(
        TreeViewItem item,
        ScrollViewer scrollViewer,
        out double itemTop,
        out double itemBottom,
        out double viewportTop,
        out double viewportBottom)
    {
        itemTop = 0;
        itemBottom = 0;
        viewportTop = 0;
        viewportBottom = 0;

        if (scrollViewer.ViewportHeight <= 0)
        {
            return false;
        }

        item.UpdateLayout();
        scrollViewer.UpdateLayout();

        if (item.Template?.FindName("ItemBorder", item) is FrameworkElement header && header.ActualHeight > 0)
        {
            try
            {
                var origin = header.TranslatePoint(new Point(0, 0), scrollViewer);
                itemTop = origin.Y;
                itemBottom = itemTop + header.ActualHeight;
                viewportTop = scrollViewer.VerticalOffset;
                viewportBottom = viewportTop + scrollViewer.ViewportHeight;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }

        if (!item.IsVisible)
        {
            return false;
        }

        if (item.Template?.FindName("ItemBorder", item) is FrameworkElement itemBorder
            && itemBorder.IsVisible
            && itemBorder.ActualHeight > 0)
        {
            var origin = itemBorder.TranslatePoint(new Point(0, 0), scrollViewer);
            itemTop = origin.Y;
            itemBottom = itemTop + itemBorder.ActualHeight;
        }
        else
        {
            itemTop = item.TranslatePoint(new Point(0, 0), scrollViewer).Y;
            itemBottom = itemTop + Math.Min(item.ActualHeight, TreeViewHierarchy.HeaderRowHeight);
        }

        viewportTop = scrollViewer.VerticalOffset;
        viewportBottom = viewportTop + scrollViewer.ViewportHeight;
        return true;
    }

    private static void ScrollToShowRange(
        ScrollViewer scrollViewer,
        double itemTop,
        double itemBottom,
        double viewportTop,
        double viewportBottom,
        bool animate)
    {
        double targetOffset;
        if (itemTop < viewportTop + ViewportPadding)
        {
            targetOffset = scrollViewer.VerticalOffset + itemTop - ViewportPadding;
        }
        else
        {
            targetOffset = scrollViewer.VerticalOffset + (itemBottom - scrollViewer.ViewportHeight) + ViewportPadding;
        }

        targetOffset = Math.Clamp(targetOffset, 0, scrollViewer.ScrollableHeight);

        if (animate)
        {
            SmoothScrollAnimator.ScrollTo(scrollViewer, targetOffset);
            return;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        SmoothScrollAnimator.SyncTarget(scrollViewer, targetOffset);
    }
}
