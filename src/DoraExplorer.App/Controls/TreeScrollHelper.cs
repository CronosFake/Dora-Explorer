using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Controls;

public static class TreeScrollHelper
{
    private const double ViewportPadding = 6;
    private const double TreePaddingTop = 4;

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
        ScrollViewer scrollViewer)
    {
        scrollViewer.UpdateLayout();

        if (scrollViewer.ViewportHeight > 0 &&
            TryEstimateTargetOffset(roots, target, out var itemTop))
        {
            var centerY = TreePaddingTop + itemTop + (GetEstimatedRowHeight(target) / 2);
            var targetOffset = centerY - (scrollViewer.ViewportHeight / 2);
            targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
            ApplyScrollOffset(scrollViewer, targetOffset, animate: false);
        }

        scrollViewer.UpdateLayout();
        item.UpdateLayout();
        return TryCenterItemInViewport(item, scrollViewer, animate: false);
    }

    public static bool TryEstimateTargetOffset(
        IEnumerable<NavigationTreeNodeViewModel> roots,
        NavigationTreeNodeViewModel target,
        out double offsetTop)
    {
        offsetTop = 0;
        return WalkVisibleNodes(roots, target, ref offsetTop);
    }

    public static void JumpScrollNearTarget(
        NavigationTreeNodeViewModel target,
        IEnumerable<NavigationTreeNodeViewModel> roots,
        ScrollViewer scrollViewer)
    {
        scrollViewer.UpdateLayout();

        if (scrollViewer.ViewportHeight <= 0 ||
            !TryEstimateTargetOffset(roots, target, out var itemTop))
        {
            return;
        }

        var centerY = TreePaddingTop + itemTop + (GetEstimatedRowHeight(target) / 2);
        var targetOffset = centerY - (scrollViewer.ViewportHeight / 2);
        targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
        ApplyScrollOffset(scrollViewer, targetOffset, animate: false);
    }

    private static bool WalkVisibleNodes(
        IEnumerable<NavigationTreeNodeViewModel> nodes,
        NavigationTreeNodeViewModel target,
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

            y += GetEstimatedRowHeight(node);

            if (node.IsExpanded && node.Children.Count > 0 &&
                WalkVisibleNodes(node.Children, target, ref y))
            {
                return true;
            }
        }

        return false;
    }

    private static double GetEstimatedRowHeight(NavigationTreeNodeViewModel node)
    {
        var height = TreeViewHierarchy.HeaderRowHeight;

        if (node.ShowFileMetadataFooter)
        {
            height += 20;
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
